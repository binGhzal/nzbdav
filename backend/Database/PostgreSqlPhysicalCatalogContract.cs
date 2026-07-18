using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace NzbWebDAV.Database;

internal enum PostgreSqlCatalogState
{
    EmptySchema,
    EmptyHistory,
    Baseline,
    Head
}

/// <summary>
/// Captures an OID-independent, inspectable PostgreSQL physical catalog. The
/// checked-in inventories are generated only against the pinned server patch.
/// Exact text comparison makes absence part of the contract: an unexpected
/// catalog object contributes a line and therefore fails validation.
/// </summary>
internal static class PostgreSqlPhysicalCatalogContract
{
    private const string CatalogFormat = "postgresql-catalog-v3";

    // Compatibility surface for the model manifest. The inspectable inventory
    // is authoritative; this value is always derived from its embedded bytes.
    internal static string ExpectedSha256 =>
        Sha256(ReadExpectedInventory(PostgreSqlCatalogState.Head));

    internal static Task ValidateAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        return ValidateAsync(connection, PostgreSqlCatalogState.Head, cancellationToken);
    }

    internal static async Task ValidateAsync(
        NpgsqlConnection connection,
        PostgreSqlCatalogState expectedState,
        CancellationToken cancellationToken = default)
    {
        var actual = await CaptureCanonicalAsync(connection, cancellationToken);
        ValidateCanonical(actual, expectedState);
    }

    internal static async Task ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlCatalogState expectedState,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateTransactionContext(connection, transaction, commandTimeoutSeconds);
        var actual = await CaptureCanonicalAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            cancellationToken);
        ValidateCanonical(actual, expectedState);
    }

    private static void ValidateCanonical(
        string actual,
        PostgreSqlCatalogState expectedState)
    {
        var expected = ReadExpectedInventory(expectedState);
        if (string.Equals(actual, expected, StringComparison.Ordinal)) return;

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var firstDifference = Enumerable.Range(0, Math.Max(expectedLines.Length, actualLines.Length))
            .First(index => index >= expectedLines.Length
                            || index >= actualLines.Length
                            || !string.Equals(
                                expectedLines[index],
                                actualLines[index],
                                StringComparison.Ordinal));
        var expectedLine = firstDifference < expectedLines.Length
            ? expectedLines[firstDifference]
            : "<end-of-inventory>";
        var actualLine = firstDifference < actualLines.Length
            ? actualLines[firstDifference]
            : "<end-of-inventory>";
        throw new InvalidOperationException(
            $"PostgreSQL physical catalog validation failed for {expectedState} at line {firstDifference + 1}; " +
            $"expected=[{expectedLine}], actual=[{actualLine}], " +
            $"expectedSha256={Sha256(expected)}, actualSha256={Sha256(actual)}.");
    }

    internal static string ReadExpectedInventory(PostgreSqlCatalogState state)
    {
        var suffix = state switch
        {
            PostgreSqlCatalogState.EmptySchema => "postgresql-native-empty-schema-catalog.txt",
            PostgreSqlCatalogState.EmptyHistory => "postgresql-native-empty-history-catalog.txt",
            PostgreSqlCatalogState.Baseline => "postgresql-native-baseline-catalog.txt",
            PostgreSqlCatalogState.Head => "postgresql-native-head-catalog.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
        var assembly = typeof(PostgreSqlPhysicalCatalogContract).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException(
                $"Embedded PostgreSQL catalog inventory '{suffix}' is missing.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded PostgreSQL catalog inventory '{resourceName}' could not be read.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return NormalizeInventory(reader.ReadToEnd());
    }

    internal static async Task<string> CaptureCanonicalAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        NpgsqlTransaction? transaction = null;
        var commandTimeoutSeconds = connection.CommandTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        var targetSchema = await ScalarAsync<string>(
            connection,
            transaction,
            commandTimeoutSeconds,
            "SELECT current_schema()",
            cancellationToken);
        return await CaptureCanonicalAsync(
            connection,
            transaction,
            targetSchema,
            commandTimeoutSeconds,
            cancellationToken);
    }

    internal static async Task<string> CaptureCanonicalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateTransactionContext(connection, transaction, commandTimeoutSeconds);
        var targetSchema = await ScalarAsync<string>(
            connection,
            transaction,
            commandTimeoutSeconds,
            "SELECT current_schema()",
            cancellationToken);
        return await CaptureCanonicalAsync(
            connection,
            transaction,
            targetSchema,
            commandTimeoutSeconds,
            cancellationToken);
    }

    internal static Task<string> CaptureCanonicalAsync(
        NpgsqlConnection connection,
        string targetSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        NpgsqlTransaction? transaction = null;
        var commandTimeoutSeconds = connection.CommandTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        return CaptureCanonicalAsync(
            connection,
            transaction,
            targetSchema,
            commandTimeoutSeconds,
            cancellationToken);
    }

    private static async Task<string> CaptureCanonicalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string targetSchema,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSchema);

        var lines = new List<string>
        {
            $"contract|{CatalogFormat}"
        };

        await AddAsync(lines, connection,
            "SELECT concat_ws('|', 'server', current_setting('server_version'), current_setting('server_version_num'))",
            targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'scope',
                CASE WHEN d.datdba = current_user::regrole::oid THEN '<owner>' ELSE d_owner.rolname END,
                CASE WHEN n.nspowner = current_user::regrole::oid THEN '<owner>' ELSE n_owner.rolname END,
                CASE WHEN d.datacl IS NULL THEN 'default-database-acl' ELSE 'explicit-database-acl' END,
                CASE WHEN n.nspacl IS NULL THEN 'default-schema-acl' ELSE 'explicit-schema-acl' END)
            FROM pg_database AS d
            JOIN pg_roles AS d_owner ON d_owner.oid = d.datdba
            JOIN pg_namespace AS n ON n.nspname = @target_schema
            JOIN pg_roles AS n_owner ON n_owner.oid = n.nspowner
            WHERE d.datname = current_database()
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'environment-count', object_kind, object_count::text)
            FROM (
                SELECT 'default-acl' AS object_kind, count(*) AS object_count
                FROM pg_default_acl AS da
                JOIN pg_namespace AS n ON n.nspname = @target_schema
                WHERE da.defaclrole = current_user::regrole::oid
                  AND (da.defaclnamespace = 0 OR da.defaclnamespace = n.oid)
                UNION ALL SELECT 'event-trigger', count(*) FROM pg_event_trigger
                UNION ALL SELECT 'publication', count(*) FROM pg_publication
                UNION ALL SELECT 'publication-namespace', count(*) FROM pg_publication_namespace
                UNION ALL SELECT 'subscription', count(*) FROM pg_subscription
                    WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            ) AS counts
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        // Effective database and schema ACLs preserve both default-vs-explicit
        // state above and every grant/grant-option below.
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'database-acl',
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_database AS d
            CROSS JOIN LATERAL aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE d.datname = current_database()
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'schema-acl',
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_namespace AS n
            CROSS JOIN LATERAL aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'relation', c.relname, c.relkind, c.relpersistence,
                CASE WHEN c.relowner = current_user::regrole::oid THEN '<owner>' ELSE owner_role.rolname END,
                coalesce(am.amname, ''), coalesce(ts.spcname, ''),
                c.relchecks::text, c.relhasindex::text, c.relhasrules::text,
                c.relhastriggers::text, c.relrowsecurity::text, c.relforcerowsecurity::text,
                c.relreplident, c.relhassubclass::text, c.relispartition::text,
                (c.reltoastrelid <> 0)::text,
                coalesce((SELECT string_agg(option_value, ',' ORDER BY option_value)
                          FROM unnest(c.reloptions) AS option_value), ''),
                CASE WHEN c.relacl IS NULL THEN 'default-acl' ELSE 'explicit-acl' END)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            JOIN pg_roles AS owner_role ON owner_role.oid = c.relowner
            LEFT JOIN pg_am AS am ON am.oid = c.relam
            LEFT JOIN pg_tablespace AS ts ON ts.oid = c.reltablespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'relation-acl', c.relname,
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(coalesce(c.relacl,
                acldefault(CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END, c.relowner))) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE n.nspname = @target_schema
              AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'toast', parent.relname, toast.relpersistence,
                CASE WHEN toast.relowner = current_user::regrole::oid THEN '<owner>' ELSE owner_role.rolname END,
                coalesce(am.amname, ''), coalesce(ts.spcname, ''), toast.relhasindex::text,
                coalesce((SELECT string_agg(option_value, ',' ORDER BY option_value)
                          FROM unnest(toast.reloptions) AS option_value), ''),
                CASE WHEN toast.relacl IS NULL THEN 'default-acl' ELSE 'explicit-acl' END)
            FROM pg_class AS parent
            JOIN pg_namespace AS n ON n.oid = parent.relnamespace
            JOIN pg_class AS toast ON toast.oid = parent.reltoastrelid
            JOIN pg_roles AS owner_role ON owner_role.oid = toast.relowner
            LEFT JOIN pg_am AS am ON am.oid = toast.relam
            LEFT JOIN pg_tablespace AS ts ON ts.oid = toast.reltablespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'toast-index', parent.relname, idx.indisunique::text,
                idx.indisprimary::text, idx.indisvalid::text, idx.indisready::text,
                idx.indislive::text, idx.indimmediate::text, idx.indnkeyatts::text,
                idx.indnatts::text, idx.indkey::text,
                coalesce(am.amname, ''), coalesce(ts.spcname, ''))
            FROM pg_class AS parent
            JOIN pg_namespace AS n ON n.oid = parent.relnamespace
            JOIN pg_index AS idx ON idx.indrelid = parent.reltoastrelid
            JOIN pg_class AS index_class ON index_class.oid = idx.indexrelid
            LEFT JOIN pg_am AS am ON am.oid = index_class.relam
            LEFT JOIN pg_tablespace AS ts ON ts.oid = index_class.reltablespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'column', c.relname, a.attnum::text,
                CASE WHEN a.attisdropped THEN '<dropped>' ELSE a.attname END,
                a.attisdropped::text, pg_catalog.format_type(a.atttypid, a.atttypmod),
                a.atttypmod::text, a.attndims::text, a.attnotnull::text,
                a.atthasdef::text, a.atthasmissing::text,
                coalesce(a.attmissingval::text, ''), a.attidentity, a.attgenerated,
                CASE WHEN a.attcollation = 0 THEN ''
                     WHEN col_ns.nspname = 'pg_catalog' THEN 'pg_catalog.' || col.collname
                     WHEN col_ns.nspname = @target_schema THEN '<schema>.' || col.collname
                     ELSE col_ns.nspname || '.' || col.collname END,
                a.attstorage, a.attcompression, a.attstattarget::text,
                a.attinhcount::text, a.attislocal::text,
                coalesce((SELECT string_agg(option_value, ',' ORDER BY option_value)
                          FROM unnest(a.attoptions) AS option_value), ''),
                coalesce(pg_get_expr(def.adbin, def.adrelid, true), ''),
                CASE WHEN a.attacl IS NULL THEN 'default-acl' ELSE 'explicit-acl' END)
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef AS def ON def.adrelid = a.attrelid AND def.adnum = a.attnum
            LEFT JOIN pg_collation AS col ON col.oid = a.attcollation
            LEFT JOIN pg_namespace AS col_ns ON col_ns.oid = col.collnamespace
            WHERE n.nspname = @target_schema
              AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'c')
              AND a.attnum > 0
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'column-acl', c.relname, a.attnum::text,
                CASE WHEN a.attisdropped THEN '<dropped>' ELSE a.attname END,
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(a.attacl) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE n.nspname = @target_schema AND a.attnum > 0
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'constraint', coalesce(table_class.relname, ''), con.conname,
                con.contype, con.condeferrable::text, con.condeferred::text,
                con.convalidated::text, con.conislocal::text, con.coninhcount::text,
                con.connoinherit::text,
                coalesce(parent_table.relname || '.' || parent_con.conname, ''),
                coalesce(ref_class.relname, ''), coalesce(con.conkey::text, ''),
                coalesce(con.confkey::text, ''), coalesce(con.conpfeqop::text, ''),
                coalesce(con.conppeqop::text, ''), coalesce(con.conffeqop::text, ''),
                coalesce(con.conexclop::text, ''), pg_get_constraintdef(con.oid, true))
            FROM pg_constraint AS con
            JOIN pg_namespace AS con_ns ON con_ns.oid = con.connamespace
            LEFT JOIN pg_class AS table_class ON table_class.oid = con.conrelid
            LEFT JOIN pg_class AS ref_class ON ref_class.oid = con.confrelid
            LEFT JOIN pg_constraint AS parent_con ON parent_con.oid = con.conparentid
            LEFT JOIN pg_class AS parent_table ON parent_table.oid = parent_con.conrelid
            WHERE con_ns.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'index', table_class.relname, index_class.relname,
                CASE WHEN index_class.relowner = current_user::regrole::oid THEN '<owner>' ELSE owner_role.rolname END,
                am.amname, coalesce(ts.spcname, ''),
                idx.indisunique::text, idx.indnullsnotdistinct::text,
                idx.indisprimary::text, idx.indisexclusion::text,
                idx.indimmediate::text, idx.indisclustered::text,
                idx.indisvalid::text, idx.indcheckxmin::text, idx.indisready::text,
                idx.indislive::text, idx.indisreplident::text,
                idx.indnkeyatts::text, idx.indnatts::text, idx.indkey::text,
                coalesce((SELECT string_agg(pg_get_indexdef(idx.indexrelid, key_no, true), ',' ORDER BY key_no)
                          FROM generate_series(1, idx.indnatts) AS key_no), ''),
                coalesce(pg_get_expr(idx.indexprs, idx.indrelid, true), ''),
                coalesce(pg_get_expr(idx.indpred, idx.indrelid, true), ''),
                idx.indcollation::text, idx.indclass::text, idx.indoption::text,
                coalesce((SELECT string_agg(option_value, ',' ORDER BY option_value)
                          FROM unnest(index_class.reloptions) AS option_value), ''))
            FROM pg_index AS idx
            JOIN pg_class AS index_class ON index_class.oid = idx.indexrelid
            JOIN pg_class AS table_class ON table_class.oid = idx.indrelid
            JOIN pg_namespace AS n ON n.oid = table_class.relnamespace
            JOIN pg_roles AS owner_role ON owner_role.oid = index_class.relowner
            JOIN pg_am AS am ON am.oid = index_class.relam
            LEFT JOIN pg_tablespace AS ts ON ts.oid = index_class.reltablespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'inherits', child.relname, parent_ns.nspname, parent.relname,
                inh.inhseqno::text, inh.inhdetachpending::text)
            FROM pg_inherits AS inh
            JOIN pg_class AS child ON child.oid = inh.inhrelid
            JOIN pg_namespace AS child_ns ON child_ns.oid = child.relnamespace
            JOIN pg_class AS parent ON parent.oid = inh.inhparent
            JOIN pg_namespace AS parent_ns ON parent_ns.oid = parent.relnamespace
            WHERE child_ns.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'rule', c.relname, r.rulename, r.ev_type,
                r.ev_enabled, r.is_instead::text,
                replace(replace(pg_get_ruledef(r.oid, true), format('%I.', @target_schema), '<schema>.'),
                        @target_schema || '.', '<schema>.'))
            FROM pg_rewrite AS r
            JOIN pg_class AS c ON c.oid = r.ev_class
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        // Internal FK trigger names contain object OIDs and are intentionally
        // represented by stable constraint/table/function semantics instead.
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', CASE WHEN tr.tgisinternal THEN 'internal-trigger' ELSE 'trigger' END,
                table_class.relname,
                CASE WHEN tr.tgisinternal THEN coalesce(con.conname, '<unbound-internal>') ELSE tr.tgname END,
                function_proc.proname, tr.tgtype::text, tr.tgenabled,
                tr.tgdeferrable::text, tr.tginitdeferred::text,
                tr.tgnargs::text, encode(tr.tgargs, 'hex'), tr.tgattr::text,
                coalesce(pg_get_expr(tr.tgqual, tr.tgrelid, true), ''),
                coalesce(ref_class.relname, ''))
            FROM pg_trigger AS tr
            JOIN pg_class AS table_class ON table_class.oid = tr.tgrelid
            JOIN pg_namespace AS n ON n.oid = table_class.relnamespace
            JOIN pg_proc AS function_proc ON function_proc.oid = tr.tgfoid
            LEFT JOIN pg_constraint AS con ON con.oid = tr.tgconstraint
            LEFT JOIN pg_class AS ref_class ON ref_class.oid = con.confrelid
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'function', p.proname,
                pg_get_function_identity_arguments(p.oid), pg_get_function_result(p.oid),
                l.lanname,
                CASE WHEN p.proowner = current_user::regrole::oid THEN '<owner>' ELSE owner_role.rolname END,
                p.prokind, p.provolatile, p.proparallel, p.prosecdef::text,
                p.proleakproof::text, p.proisstrict::text, p.proretset::text,
                p.pronargs::text, p.pronargdefaults::text, p.procost::text, p.prorows::text,
                coalesce(array_to_string(p.proconfig, ','), ''),
                replace(replace(replace(p.prosrc, E'\\', E'\\\\'), E'\n', E'\\n'), '|', E'\\p'),
                CASE WHEN p.proacl IS NULL THEN 'default-acl' ELSE 'explicit-acl' END)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            JOIN pg_roles AS owner_role ON owner_role.oid = p.proowner
            JOIN pg_language AS l ON l.oid = p.prolang
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'function-acl', p.proname,
                pg_get_function_identity_arguments(p.oid),
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'type', t.typname, t.typtype, t.typcategory,
                CASE WHEN t.typowner = current_user::regrole::oid THEN '<owner>' ELSE owner_role.rolname END,
                t.typlen::text, t.typbyval::text, t.typalign, t.typstorage,
                t.typnotnull::text, t.typisdefined::text, t.typdelim,
                coalesce(base_ns.nspname || '.' || base_type.typname, ''),
                t.typtypmod::text, t.typndims::text,
                coalesce(coll_ns.nspname || '.' || coll.collname, ''),
                coalesce(class.relname, ''), coalesce(array_type.typname, ''),
                coalesce(element_type.typname, ''), coalesce(t.typdefault, ''),
                CASE WHEN t.typacl IS NULL THEN 'default-acl' ELSE 'explicit-acl' END)
            FROM pg_type AS t
            JOIN pg_namespace AS n ON n.oid = t.typnamespace
            JOIN pg_roles AS owner_role ON owner_role.oid = t.typowner
            LEFT JOIN pg_type AS base_type ON base_type.oid = t.typbasetype
            LEFT JOIN pg_namespace AS base_ns ON base_ns.oid = base_type.typnamespace
            LEFT JOIN pg_collation AS coll ON coll.oid = t.typcollation
            LEFT JOIN pg_namespace AS coll_ns ON coll_ns.oid = coll.collnamespace
            LEFT JOIN pg_class AS class ON class.oid = t.typrelid
            LEFT JOIN pg_type AS array_type ON array_type.oid = t.typarray
            LEFT JOIN pg_type AS element_type ON element_type.oid = t.typelem
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'type-acl', t.typname,
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     WHEN acl.grantee = current_user::regrole::oid THEN '<owner>'
                     ELSE grantee.rolname END,
                acl.privilege_type, acl.is_grantable::text,
                CASE WHEN acl.grantor = current_user::regrole::oid THEN '<owner>' ELSE grantor.rolname END)
            FROM pg_type AS t
            JOIN pg_namespace AS n ON n.oid = t.typnamespace
            CROSS JOIN LATERAL aclexplode(coalesce(t.typacl, acldefault('T', t.typowner))) AS acl
            LEFT JOIN pg_roles AS grantee ON grantee.oid = acl.grantee
            JOIN pg_roles AS grantor ON grantor.oid = acl.grantor
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'policy', c.relname, p.polname, p.polcmd,
                p.polpermissive::text,
                coalesce((SELECT string_agg(CASE WHEN role_oid = 0 THEN 'PUBLIC' ELSE role_oid::regrole::text END, ',' ORDER BY role_oid)
                          FROM unnest(p.polroles) AS role_oid), ''),
                coalesce(pg_get_expr(p.polqual, p.polrelid, true), ''),
                coalesce(pg_get_expr(p.polwithcheck, p.polrelid, true), ''))
            FROM pg_policy AS p
            JOIN pg_class AS c ON c.oid = p.polrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'statistics', s.stxname, c.relname, s.stxkeys::text,
                s.stxkind::text, coalesce(pg_get_statisticsobjdef(s.oid), ''))
            FROM pg_statistic_ext AS s
            JOIN pg_class AS c ON c.oid = s.stxrelid
            JOIN pg_namespace AS n ON n.oid = s.stxnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'collation', c.collname, c.collprovider,
                c.collisdeterministic::text, c.collencoding::text,
                coalesce(c.collcollate, ''), coalesce(c.collctype, ''),
                coalesce(c.colliculocale, ''), coalesce(c.collicurules, ''),
                coalesce(c.collversion, ''))
            FROM pg_collation AS c
            JOIN pg_namespace AS n ON n.oid = c.collnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'operator', o.oprname, o.oprkind, o.oprcanmerge::text,
                o.oprcanhash::text, o.oprleft::regtype::text, o.oprright::regtype::text,
                o.oprresult::regtype::text, o.oprcode::regprocedure::text)
            FROM pg_operator AS o JOIN pg_namespace AS n ON n.oid = o.oprnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'opfamily', f.opfname, am.amname)
            FROM pg_opfamily AS f
            JOIN pg_namespace AS n ON n.oid = f.opfnamespace
            JOIN pg_am AS am ON am.oid = f.opfmethod
            WHERE n.nspname = @target_schema
            UNION ALL
            SELECT concat_ws('|', 'opclass', c.opcname, am.amname, c.opcdefault::text,
                c.opcintype::regtype::text, f.opfname)
            FROM pg_opclass AS c
            JOIN pg_namespace AS n ON n.oid = c.opcnamespace
            JOIN pg_am AS am ON am.oid = c.opcmethod
            JOIN pg_opfamily AS f ON f.oid = c.opcfamily
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'conversion', c.conname, c.conforencoding::text,
                c.contoencoding::text, c.conproc::regprocedure::text, c.condefault::text)
            FROM pg_conversion AS c JOIN pg_namespace AS n ON n.oid = c.connamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'text-search-config', c.cfgname) FROM pg_ts_config AS c
            JOIN pg_namespace AS n ON n.oid = c.cfgnamespace WHERE n.nspname = @target_schema
            UNION ALL
            SELECT concat_ws('|', 'text-search-dictionary', d.dictname) FROM pg_ts_dict AS d
            JOIN pg_namespace AS n ON n.oid = d.dictnamespace WHERE n.nspname = @target_schema
            UNION ALL
            SELECT concat_ws('|', 'text-search-parser', p.prsname) FROM pg_ts_parser AS p
            JOIN pg_namespace AS n ON n.oid = p.prsnamespace WHERE n.nspname = @target_schema
            UNION ALL
            SELECT concat_ws('|', 'text-search-template', t.tmplname) FROM pg_ts_template AS t
            JOIN pg_namespace AS n ON n.oid = t.tmplnamespace WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);
        await AddAsync(lines, connection,
            """
            SELECT concat_ws('|', 'extension', e.extname, e.extversion, e.extrelocatable::text)
            FROM pg_extension AS e JOIN pg_namespace AS n ON n.oid = e.extnamespace
            WHERE n.nspname = @target_schema
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken);

        var quotedTargetSchema = new NpgsqlCommandBuilder().QuoteIdentifier(targetSchema);
        await AddAsync(lines, connection,
            $"""
            SELECT concat_ws('|', 'history-row', "MigrationId", "ProductVersion")
            FROM {quotedTargetSchema}."{DatabaseMigrationPolicy.PostgreSqlHistoryTableName}"
            ORDER BY "MigrationId"
            """, targetSchema, transaction, commandTimeoutSeconds, cancellationToken,
            tolerateUndefinedTable: true);

        return NormalizeInventory(string.Join('\n', lines.Order(StringComparer.Ordinal)) + '\n');
    }

    internal static string Sha256(string inventory)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeInventory(inventory))));
    }

    private static async Task AddAsync(
        List<string> lines,
        NpgsqlConnection connection,
        string sql,
        string targetSchema,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        bool tolerateUndefinedTable = false)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = sql;
            if (sql.Contains("@target_schema", StringComparison.Ordinal))
                command.Parameters.AddWithValue("target_schema", targetSchema);
            reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lines.Add(reader.GetString(0));
        }
        catch (PostgresException exception) when (
            tolerateUndefinedTable && exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The exact empty-schema inventory has no EF history relation.
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                reader,
                command,
                primaryFailure)
            .ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        string sql,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        T? result = default;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = sql;
            result = (T)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                reader: null,
                command,
                primaryFailure)
            .ConfigureAwait(false);
        return result!;
    }

    private static void ValidateTransactionContext(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException(
                "PostgreSQL physical catalog capture requires an open connection.");
        NpgsqlConnection? transactionConnection;
        try
        {
            transactionConnection = transaction.Connection;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }

        if (!ReferenceEquals(transactionConnection, connection))
            throw TransactionFailure();

        try
        {
            // Npgsql 10.0.3 keeps Connection available after commit/rollback;
            // the public IsolationLevel getter invokes its internal CheckReady.
            _ = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }
    }

    private static InvalidOperationException TransactionFailure(Exception? inner = null) =>
        new(
            "PostgreSQL physical catalog capture requires an active transaction owned by the supplied connection.",
            inner);

    private static string NormalizeInventory(string inventory)
    {
        return inventory.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n') + '\n';
    }
}

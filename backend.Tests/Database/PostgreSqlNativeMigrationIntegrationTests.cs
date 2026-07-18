using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class PostgreSqlNativeMigrationIntegrationTests
{
    [PostgreSqlFact]
    public async Task EmptySchemaMigratesToTheExactNativePhysicalContractAndIsIdempotent()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_physical");

        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);

        Assert.Equal(28L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = current_schema()
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
            """));
        Assert.Equal(240L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
            """));
        Assert.Equal(28L, await ConstraintCountAsync(schema, "p"));
        Assert.Equal(8L, await ConstraintCountAsync(schema, "f"));
        Assert.Equal(36L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_constraint AS c
            JOIN pg_class AS t ON t.oid = c.conrelid
            JOIN pg_namespace AS n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema()
              AND c.contype IN ('p', 'f')
              AND t.relname <> '__EFMigrationsHistory_PostgreSql'
            """));
        Assert.Equal(56L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_index AS i
            JOIN pg_class AS t ON t.oid = i.indrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema()
              AND t.relname <> '__EFMigrationsHistory_PostgreSql'
              AND NOT i.indisprimary
            """));
        Assert.Equal(84L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_index AS i
            JOIN pg_class AS t ON t.oid = i.indrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema()
              AND t.relname <> '__EFMigrationsHistory_PostgreSql'
            """));
        Assert.Equal(0L, await ObjectCountAsync(schema, "S"));
        Assert.Equal(0L, await ObjectCountAsync(schema, "v"));
        Assert.Equal(0L, await ObjectCountAsync(schema, "m"));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_type AS t
            JOIN pg_namespace AS n ON n.oid = t.typnamespace
            WHERE n.nspname = current_schema()
              AND t.typtype IN ('d', 'e')
            """));
        Assert.Equal(9L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
        Assert.Equal(9L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_trigger AS tr
            JOIN pg_class AS t ON t.oid = tr.tgrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema()
              AND NOT tr.tgisinternal
            """));
        Assert.Equal(2L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(5L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM "DavItems"
            WHERE "Id"::text LIKE '00000000-0000-0000-0000-00000000000_'
              AND "IdPrefix" = '00000'
              AND isfinite("CreatedAt")
              AND "CreatedAt" = timestamp '0001-01-01 00:00:00'
            """));
        Assert.Equal(3L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM "ConfigItems"
            WHERE ("ConfigName" IN ('api.key', 'api.strm-key')
                   AND "ConfigValue" ~ '^[0-9a-f]{32}$')
               OR ("ConfigName" = 'database.import-state'
                   AND "ConfigValue" = '{"formatVersion":3,"state":"fresh"}')
            """));
        Assert.Equal(2L, await schema.ScalarAsync<long>(
            """
            SELECT count(DISTINCT "ConfigValue")
            FROM "ConfigItems"
            WHERE "ConfigName" IN ('api.key', 'api.strm-key')
            """));
    }

    [PostgreSqlFact]
    public async Task ZeroToHeadCatalogMatchesTheCheckedInPhysicalContractHash()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_catalog");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);

        await using var connection = await schema.OpenConnectionAsync();
        var canonicalCatalog = await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        Assert.Equal(
            PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.Head),
            canonicalCatalog);
        var actualHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalCatalog)));
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(
                "backend.Tests/TestData/postgresql-native-schema-contract.json")));
        var expectedHash = manifest.RootElement.GetProperty("catalogSha256").GetString();

        Assert.True(
            string.Equals(actualHash, expectedHash, StringComparison.Ordinal),
            $"Canonical PostgreSQL 16.14 catalog hash is {actualHash}.");
    }

    [PostgreSqlFact]
    public async Task KnownHistoryWithCatalogDefinitionDriftIsRefusedWithoutRepairingIt()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_catalog_drift");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await schema.ExecuteAsync(
            """
            DROP INDEX "IX_WorkerJobs_ClaimOrder";
            CREATE INDEX "IX_WorkerJobs_ClaimOrder"
            ON "WorkerJobs" ("Kind", "Status", "Priority");
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(\"Kind\", \"Status\", \"Priority\")",
            await schema.ScalarAsync<string>(
                "SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() " +
                "AND indexname = 'IX_WorkerJobs_ClaimOrder'"),
            StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task BaselineHistoryWithPhysicalColumnDriftIsRefusedWithoutApplyingHead()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_baseline_drift");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(
            "ALTER TABLE \"Accounts\" ALTER COLUMN \"PasswordHash\" SET STORAGE EXTERNAL");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("e", await schema.ScalarAsync<string>(
            """
            SELECT a.attstorage::text
            FROM pg_attribute AS a
            JOIN pg_class AS c ON c.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
              AND c.relname = 'Accounts'
              AND a.attname = 'PasswordHash'
            """));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task BaselineHistoryWithBootstrapDataDriftIsRefusedWithoutApplyingHead()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_baseline_data_drift");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(
            "DELETE FROM \"ConfigItems\" WHERE \"ConfigName\" = 'database.import-state'");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("bootstrap", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task BaselineHistoryWithExactBootstrapAppliesHead()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_baseline_exact");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }

        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);

        Assert.Equal(
        [
            "20260712000000_PostgreSqlNativeBaseline",
            "20260712000100_PostgreSqlOperationalTriggers"
        ],
            await ReadStringsAsync(
                schema,
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory_PostgreSql\" ORDER BY \"MigrationId\""));
    }

    [PostgreSqlFact]
    public async Task KnownHistoryWithDisabledOperationalTriggerIsRefusedWithoutReenablingIt()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_trigger_state_drift");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await schema.ExecuteAsync(
            "ALTER TABLE \"DavItems\" DISABLE TRIGGER \"TR_DavItems_DeleteDirectory\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("D", await schema.ScalarAsync<string>(
            """
            SELECT tr.tgenabled::text
            FROM pg_trigger AS tr
            JOIN pg_class AS t ON t.oid = tr.tgrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema()
              AND t.relname = 'DavItems'
              AND tr.tgname = 'TR_DavItems_DeleteDirectory'
            """));
    }

    [PostgreSqlFact]
    public async Task HeadCatalogRefusesPublicColumnAclWithoutRevokingIt()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_column_acl_drift");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await schema.ExecuteAsync("GRANT SELECT (\"Username\") ON \"Accounts\" TO PUBLIC");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await schema.ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_attribute AS a
                JOIN pg_class AS c ON c.oid = a.attrelid
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL aclexplode(a.attacl) AS acl
                WHERE n.nspname = current_schema()
                  AND c.relname = 'Accounts'
                  AND a.attname = 'Username'
                  AND acl.grantee = 0
                  AND acl.privilege_type = 'SELECT')
            """));
    }

    [PostgreSqlTheory]
    [InlineData("CREATE OPERATOR FAMILY sentinel_family USING btree", "opfamily|sentinel_family|btree")]
    [InlineData(
        "CREATE TEXT SEARCH CONFIGURATION sentinel_text_search (COPY=pg_catalog.simple)",
        "text-search-config|sentinel_text_search")]
    [InlineData(
        "CREATE STATISTICS sentinel_statistics (dependencies) ON \"Kind\", \"Status\" FROM \"WorkerJobs\"",
        "statistics|sentinel_statistics|WorkerJobs")]
    [InlineData(
        "CREATE RULE sentinel_rule AS ON UPDATE TO \"Accounts\" DO ALSO NOTHING",
        "rule|Accounts|sentinel_rule")]
    [InlineData("ALTER TABLE \"Accounts\" SET (fillfactor=80)", "fillfactor=80")]
    [InlineData(
        "ALTER TABLE \"Accounts\" SET (toast.autovacuum_enabled=false)",
        "toast|Accounts|p|<owner>|heap||true|autovacuum_enabled=false")]
    [InlineData("ALTER TABLE \"Accounts\" ENABLE ROW LEVEL SECURITY", "relation|Accounts|r|p|<owner>")]
    [InlineData("CREATE TYPE sentinel_enum AS ENUM ('a')", "type|sentinel_enum|e|E|<owner>")]
    public async Task HeadCatalogNamespaceAndPhysicalDriftIsRefusedByteForByteWithoutRepair(
        string mutationSql,
        string expectedCatalogFragment)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_catalog_closure");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await schema.ExecuteAsync(mutationSql);
        await using var connection = await schema.OpenConnectionAsync();
        var driftedCatalog = await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        Assert.Contains(expectedCatalogFragment, driftedCatalog, StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("physical", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            driftedCatalog,
            await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection));
    }

    [PostgreSqlFact]
    public async Task HeadCatalogConstraintAndInternalTriggerStateDriftIsRefusedWithoutRepair()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_internal_trigger_drift");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var triggerName = await schema.ScalarAsync<string>(
            """
            SELECT tr.tgname
            FROM pg_trigger AS tr
            JOIN pg_constraint AS con ON con.oid = tr.tgconstraint
            JOIN pg_class AS c ON c.oid = tr.tgrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
              AND con.conname = 'FK_DavMultipartFiles_DavItems_Id'
              AND c.relname = 'DavMultipartFiles'
              AND tr.tgisinternal
            ORDER BY tr.tgfoid::regprocedure::text
            LIMIT 1
            """);
        var quotedTrigger = new NpgsqlCommandBuilder().QuoteIdentifier(triggerName);
        await schema.ExecuteAsync(
            "ALTER TABLE \"DavMultipartFiles\" ALTER CONSTRAINT " +
            "\"FK_DavMultipartFiles_DavItems_Id\" DEFERRABLE INITIALLY DEFERRED");
        await schema.ExecuteAsync(
            $"ALTER TABLE \"DavMultipartFiles\" DISABLE TRIGGER {quotedTrigger}");
        await using var connection = await schema.OpenConnectionAsync();
        var driftedCatalog = await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        Assert.Contains(
            "constraint|DavMultipartFiles|FK_DavMultipartFiles_DavItems_Id|f|true|true",
            driftedCatalog,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal-trigger|DavMultipartFiles|FK_DavMultipartFiles_DavItems_Id",
            driftedCatalog,
            StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            driftedCatalog,
            await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection));
    }

    [PostgreSqlFact]
    public async Task OperationalFunctionsExposeNoPublicExecutePrivilege()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_function_acl");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);

        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) AS acl
            WHERE n.nspname = current_schema()
              AND acl.grantee = 0
              AND acl.privilege_type = 'EXECUTE'
            """));
        Assert.Equal(9L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) AS acl
            WHERE n.nspname = current_schema()
              AND acl.grantee = p.proowner
              AND acl.privilege_type = 'EXECUTE'
            """));
    }

    [PostgreSqlFact]
    public async Task KnownHistoryWithExpectedButInvalidIndexIsRefusedWithoutRepairingIt()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_invalid_index_drift");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var duplicateDavItemId = Guid.NewGuid();
        var duplicateHistoryItemId = Guid.NewGuid();
        await schema.ExecuteAsync(
            $$"""
            DROP INDEX "IX_ImportReceipts_DavItemId_HistoryItemId";
            INSERT INTO "ImportReceipts"
                ("Id", "DavItemId", "HistoryItemId", "State", "CreatedAt", "UpdatedAt")
            VALUES
                ('{{Guid.NewGuid()}}', '{{duplicateDavItemId}}', '{{duplicateHistoryItemId}}', 0, 0, 0),
                ('{{Guid.NewGuid()}}', '{{duplicateDavItemId}}', '{{duplicateHistoryItemId}}', 0, 0, 0);
            """);
        await Assert.ThrowsAsync<PostgresException>(() => schema.ExecuteAsync(
            """
            CREATE UNIQUE INDEX CONCURRENTLY "IX_ImportReceipts_DavItemId_HistoryItemId"
            ON "ImportReceipts" ("DavItemId", "HistoryItemId")
            """));
        Assert.False(await schema.ScalarAsync<bool>(
            """
            SELECT x.indisvalid
            FROM pg_index AS x
            JOIN pg_class AS i ON i.oid = x.indexrelid
            JOIN pg_namespace AS n ON n.oid = i.relnamespace
            WHERE n.nspname = current_schema()
              AND i.relname = 'IX_ImportReceipts_DavItemId_HistoryItemId'
            """));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await schema.ScalarAsync<bool>(
            """
            SELECT x.indisvalid
            FROM pg_index AS x
            JOIN pg_class AS i ON i.oid = x.indexrelid
            JOIN pg_namespace AS n ON n.oid = i.relnamespace
            WHERE n.nspname = current_schema()
              AND i.relname = 'IX_ImportReceipts_DavItemId_HistoryItemId'
            """));
    }

    [PostgreSqlFact]
    public async Task NonemptyApplicationSchemaIsRefusedWithoutCreatingHistoryOrTables()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_nonempty");
        await schema.ExecuteAsync("CREATE TABLE sentinel (id integer PRIMARY KEY)");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("empty application schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
        Assert.Null(await schema.ScalarAsync<string?>(
            "SELECT to_regclass(format('%I.%I', current_schema(), '__EFMigrationsHistory_PostgreSql'))::text"));
    }

    [PostgreSqlFact]
    public async Task UnknownPostgreSqlHistoryIsRefusedWithoutApplyingAnyAppMigration()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_history");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "__EFMigrationsHistory_PostgreSql" (
                "MigrationId" character varying(150) PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory_PostgreSql" VALUES ('unknown_migration', '10.0.9');
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("history", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
        Assert.Equal("unknown_migration", await schema.ScalarAsync<string>(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory_PostgreSql\""));
    }

    [PostgreSqlFact]
    public async Task NativeMigratorRefusesBaselineWithWrongProductVersionWithoutApplyingHead()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_history_version");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(
            """
            UPDATE "__EFMigrationsHistory_PostgreSql"
            SET "ProductVersion" = '10.0.8'
            WHERE "MigrationId" = '20260712000000_PostgreSqlNativeBaseline'
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("history", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.0.8", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("10.0.8", await schema.ScalarAsync<string>(
            "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task DirectEfRefusesBaselineWithWrongProductVersionWithoutApplyingOperationalMigration()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("direct_history_version");
        await using (var baselineContext = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = baselineContext.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(
            """
            UPDATE "__EFMigrationsHistory_PostgreSql"
            SET "ProductVersion" = '10.0.8'
            WHERE "MigrationId" = '20260712000000_PostgreSqlNativeBaseline'
            """);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Contains("exact native baseline prefix", error.MessageText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("10.0.8", await schema.ScalarAsync<string>(
            "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task SharedTransactionContractsRejectACommittedUndisposedTransaction()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("completed_contract_tx");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.CommitAsync();

        var history = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlNativeMigrationContract.CaptureAsync(
                connection,
                transaction,
                1,
                CancellationToken.None));
        var bootstrap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlFreshBootstrapContract.CaptureAsync(
                connection,
                transaction,
                1,
                CancellationToken.None));
        var catalog = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlPhysicalCatalogContract.ValidateAsync(
                connection,
                transaction,
                PostgreSqlCatalogState.Head,
                1,
                CancellationToken.None));

        Assert.Contains("active transaction", history.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active transaction", bootstrap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active transaction", catalog.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlTheory]
    [InlineData(
        "UPDATE \"DavItems\" SET \"Path\" = '/mutated' " +
        "WHERE \"Id\" = '00000000-0000-0000-0000-000000000002'",
        "SELECT count(*) FROM \"DavItems\" WHERE \"Path\" = '/mutated'")]
    [InlineData(
        "UPDATE \"DavItems\" SET \"CreatedAt\" = " +
        "timestamp '0001-01-01 00:00:00 BC' " +
        "WHERE \"Id\" = '00000000-0000-0000-0000-000000000002'",
        "SELECT count(*) FROM \"DavItems\" WHERE \"CreatedAt\" < " +
        "timestamp '0001-01-01 00:00:00'")]
    [InlineData(
        "INSERT INTO \"DavItems\" " +
        "(\"Id\", \"IdPrefix\", \"CreatedAt\", \"Name\", \"Type\", \"SubType\", \"Path\") " +
        "VALUES ('00000000-0000-0000-0000-000000000005', '00000', " +
        "timestamp '0001-01-01 00:00:00', 'extra', 1, 107, '/extra')",
        "SELECT count(*) FROM \"DavItems\" " +
        "WHERE \"Id\" = '00000000-0000-0000-0000-000000000005'")]
    [InlineData(
        "INSERT INTO \"ConfigItems\" (\"ConfigName\", \"ConfigValue\") " +
        "VALUES ('task6.extra', 'TASK6-EXTRA-BOOTSTRAP-CANARY')",
        "SELECT count(*) FROM \"ConfigItems\" WHERE \"ConfigName\" = 'task6.extra'")]
    public async Task BaselineHistoryWithMutatedOrExtraBootstrapRowIsRefusedWithoutApplyingHead(
        string mutationSql,
        string preservationQuery)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_bootstrap_exact");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(mutationSql);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("bootstrap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "TASK6-EXTRA-BOOTSTRAP-CANARY",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1L, await schema.ScalarAsync<long>(preservationQuery));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlTheory]
    [InlineData(
        "INSERT INTO \"BlobCleanupItems\" (\"Id\") " +
        "VALUES ('10000000-0000-0000-0000-000000000006')",
        "SELECT count(*) FROM \"BlobCleanupItems\" " +
        "WHERE \"Id\" = '10000000-0000-0000-0000-000000000006'")]
    [InlineData(
        "INSERT INTO \"HealthCheckStats\" " +
        "(\"DateStartInclusive\", \"DateEndExclusive\", \"Result\", \"RepairStatus\", \"Count\") " +
        "VALUES (0, 86400, 1, 0, 1)",
        "SELECT count(*) FROM \"HealthCheckStats\" " +
        "WHERE \"DateStartInclusive\" = 0 AND \"DateEndExclusive\" = 86400 " +
        "AND \"Result\" = 1 AND \"RepairStatus\" = 0 AND \"Count\" = 1")]
    public async Task BaselineHistoryWithNonemptyOtherRelationIsRefusedWithoutApplyingHead(
        string mutationSql,
        string preservationQuery)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_bootstrap_nonempty");
        await using (var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions()))
        {
            var migrator = context.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
        }
        await schema.ExecuteAsync(mutationSql);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("bootstrap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(preservationQuery));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory_PostgreSql\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task MalformedEmptyHistoryIsRefusedBeforeApplyingAnyAppMigration()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_bad_empty_history");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "__EFMigrationsHistory_PostgreSql" (
                "MigrationId" character varying(150) PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL,
                "Unexpected" text);
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("history", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shape", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
        Assert.Equal(3L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = '__EFMigrationsHistory_PostgreSql'
            """));
    }

    [PostgreSqlFact]
    public async Task DirectEfRefusesMalformedEmptyHistoryBeforeCreatingApplicationTables()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_direct_bad_history");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "__EFMigrationsHistory_PostgreSql" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                "Unexpected" text,
                CONSTRAINT "PK___EFMigrationsHistory_PostgreSql" PRIMARY KEY ("MigrationId"));
            """);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Contains("exact EF-created empty migration history", error.MessageText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
    }

    [PostgreSqlFact]
    public async Task DirectEfRefusesPublicHistoryAclBeforeCreatingApplicationTables()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_direct_history_acl");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "__EFMigrationsHistory_PostgreSql" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory_PostgreSql" PRIMARY KEY ("MigrationId"));
            GRANT SELECT ON "__EFMigrationsHistory_PostgreSql" TO PUBLIC;
            """);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => context.Database.MigrateAsync());

        Assert.Contains("exact EF-created empty migration history", error.MessageText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
        Assert.True(await schema.ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class AS c
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL aclexplode(c.relacl) AS acl
                WHERE n.nspname = current_schema()
                  AND c.relname = '__EFMigrationsHistory_PostgreSql'
                  AND acl.grantee = 0
                  AND acl.privilege_type = 'SELECT')
            """));
    }

    [PostgreSqlFact]
    public async Task CustomCompositeTypeMakesSchemaNonemptyAndIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_composite");
        await schema.ExecuteAsync("CREATE TYPE sentinel_composite AS (id integer)");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("empty application schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relkind = 'c'
            """));
        Assert.Null(await schema.ScalarAsync<string?>(
            "SELECT to_regclass(format('%I.%I', current_schema(), '__EFMigrationsHistory_PostgreSql'))::text"));
    }

    [PostgreSqlFact]
    public async Task CustomRangeAndMultirangeTypesMakeSchemaNonemptyAndAreRefused()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_range");
        await schema.ExecuteAsync("CREATE TYPE sentinel_range AS RANGE (subtype = integer)");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("empty application schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_type AS t
            JOIN pg_namespace AS n ON n.oid = t.typnamespace
            WHERE n.nspname = current_schema() AND t.typtype IN ('r', 'm')
            """));
    }

    [PostgreSqlFact]
    public async Task SchemaOwnedCollationMakesSchemaNonemptyAndIsRefused()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_collation");
        await schema.ExecuteAsync("CREATE COLLATION sentinel_collation FROM \"C\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        Assert.Contains("empty application schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM pg_collation AS c
            JOIN pg_namespace AS n ON n.oid = c.collnamespace
            WHERE n.nspname = current_schema()
            """));
    }

    [PostgreSqlFact]
    public async Task ConcurrentNativeMigratorsSerializeAndProduceOneExactHistoryChain()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_concurrent");

        await Task.WhenAll(
            PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString),
            PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString));

        var history = await ReadStringsAsync(
            schema,
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory_PostgreSql\" ORDER BY \"MigrationId\"");
        Assert.Equal(
        [
            "20260712000000_PostgreSqlNativeBaseline",
            "20260712000100_PostgreSqlOperationalTriggers"
        ],
            history);
    }

    [PostgreSqlFact]
    public async Task FailedSessionUnlockDisposesCallerConnectionAndReleasesLockForSecondSession()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_unlock_failure");
        var scope = $"unlock-probe:{Guid.NewGuid():N}";
        await using var lockedConnection = await schema.OpenConnectionAsync();
        await using var secondConnection = await schema.OpenConnectionAsync();
        await using (var acquire = lockedConnection.CreateCommand())
        {
            acquire.CommandText =
                "SELECT pg_advisory_lock(hashtextextended(@scope, 5645897944034397513))";
            acquire.Parameters.AddWithValue("scope", scope);
            await acquire.ExecuteNonQueryAsync();
        }

        try
        {
            var release = typeof(PostgreSqlNativeMigrator).GetMethod(
                "ReleaseAdvisoryLockAsync",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(release);
            var releaseTask = Assert.IsAssignableFrom<Task>(
                release.Invoke(null, [lockedConnection, scope + ":not-owned"]));

            Assert.NotNull(await Record.ExceptionAsync(() => releaseTask));
            Assert.Equal(System.Data.ConnectionState.Closed, lockedConnection.State);
            Assert.True(PostgreSqlNativeMigrator.AdvisoryUnlockCommandTimeoutSeconds > 0);

            await using var tryLock = secondConnection.CreateCommand();
            tryLock.CommandText =
                "SELECT pg_try_advisory_lock(hashtextextended(@scope, 5645897944034397513))";
            tryLock.Parameters.AddWithValue("scope", scope);
            var releaseDeadline = Stopwatch.StartNew();
            var acquired = false;
            do
            {
                acquired = (bool)(await tryLock.ExecuteScalarAsync())!;
                if (!acquired)
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
            } while (!acquired && releaseDeadline.Elapsed < TimeSpan.FromSeconds(2));

            Assert.True(
                acquired,
                "The disposed failed-unlock session retained its advisory lock beyond the bounded disconnect window.");
            tryLock.CommandText =
                "SELECT pg_advisory_unlock(hashtextextended(@scope, 5645897944034397513))";
            Assert.True((bool)(await tryLock.ExecuteScalarAsync())!);
        }
        finally
        {
            if (lockedConnection.State != System.Data.ConnectionState.Closed)
                await lockedConnection.CloseAsync();
        }
    }

    [PostgreSqlFact]
    public async Task AlpinePasswordConnectionUsesExplicitGssDisableAndExactTimezone()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_alpine");
        var builder = new NpgsqlConnectionStringBuilder(schema.ConnectionString);
        await using var connection = await schema.OpenConnectionAsync();

        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
        Assert.Equal(TimeZoneInfo.Local.Id, builder.Timezone);
        Assert.Equal(TimeZoneInfo.Local.Id, connection.Timezone);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        Assert.Contains("PostgreSQL 16.14", (string)(await command.ExecuteScalarAsync())!,
            StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task OpenConnectionPathRefusesNoncompliantGssBeforeSchemaMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_open_gss");
        var builder = new NpgsqlConnectionStringBuilder(schema.ConnectionString)
        {
            GssEncryptionMode = GssEncryptionMode.Prefer
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateOpenConnectionAsync(connection));

        Assert.Contains("Gss Encryption Mode=Disable", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
    }

    [PostgreSqlFact]
    public async Task SessionTimezoneMismatchIsRefusedBeforeSchemaMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_session_tz");
        await using var connection = await schema.OpenConnectionAsync();
        var mismatch = TimeZoneInfo.Local.Id == "Etc/UTC" ? "Asia/Dubai" : "Etc/UTC";
        await PostgreSqlTestSchema.ExecuteAsync(connection, $"SET TimeZone TO '{mismatch}'");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlNativeMigrator.MigrateOpenConnectionAsync(connection));

        Assert.Contains("session TimeZone", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()"));
    }

    [PostgreSqlFact]
    public async Task OperationalTriggersPreserveCurrentCleanupAndHealthBucketBehavior()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("native_triggers");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var directoryId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        var oldFileBlobId = Guid.NewGuid();
        var newFileBlobId = Guid.NewGuid();
        var davNzbBlobId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var historyNzbBlobId = Guid.NewGuid();
        var queueId = Guid.NewGuid();
        var healthId = Guid.NewGuid();

        await schema.ExecuteAsync(
            $$"""
            INSERT INTO "DavItems"
                ("Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "Type", "SubType", "Path")
            VALUES
                ('{{directoryId}}', 'dir00', timestamp '2026-01-01 00:00:00',
                 '00000000-0000-0000-0000-000000000002', 'trigger-dir', 1, 101, '/content/trigger-dir');
            INSERT INTO "DavItems"
                ("Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "Type", "SubType", "Path",
                 "FileBlobId", "NzbBlobId")
            VALUES
                ('{{davItemId}}', 'file0', timestamp '2026-01-01 00:00:00',
                 '{{directoryId}}', 'trigger.bin', 2, 201, '/content/trigger-dir/trigger.bin',
                 '{{oldFileBlobId}}', '{{davNzbBlobId}}');
            UPDATE "DavItems" SET "FileBlobId" = '{{newFileBlobId}}' WHERE "Id" = '{{davItemId}}';
            DELETE FROM "DavItems" WHERE "Id" = '{{davItemId}}';
            DELETE FROM "DavItems" WHERE "Id" = '{{directoryId}}';

            INSERT INTO "HistoryItems"
                ("Id", "CreatedAt", "FileName", "JobName", "Category", "DownloadStatus",
                 "TotalSegmentBytes", "DownloadTimeSeconds", "NzbBlobId")
            VALUES
                ('{{historyId}}', timestamp '2026-01-01 00:00:00', 'history.nzb', 'history', 'movies',
                 2, 100, 1, '{{historyNzbBlobId}}');
            DELETE FROM "HistoryItems" WHERE "Id" = '{{historyId}}';

            INSERT INTO "QueueItems"
                ("Id", "CreatedAt", "FileName", "JobName", "NzbFileSize", "TotalSegmentBytes",
                 "Category", "Priority", "PostProcessing")
            VALUES
                ('{{queueId}}', timestamp '2026-01-01 00:00:00', 'queue.nzb', 'queue', 10, 100,
                 'movies', 0, 0);
            DELETE FROM "QueueItems" WHERE "Id" = '{{queueId}}';

            INSERT INTO "HealthCheckResults"
                ("Id", "CreatedAt", "DavItemId", "Path", "Result", "RepairStatus")
            VALUES ('{{healthId}}', 172801, '00000000-0000-0000-0000-000000000002', '/content', 1, 0);
            """);

        Assert.Equal(2L, await schema.ScalarAsync<long>(
            $$"""
            SELECT count(*) FROM "BlobCleanupItems"
            WHERE "Id" IN ('{{oldFileBlobId}}', '{{newFileBlobId}}')
            """));
        Assert.Equal(1L, await schema.ScalarAsync<long>(
            $"SELECT count(*) FROM \"DavCleanupItems\" WHERE \"Id\" = '{directoryId}'"));
        Assert.Equal(3L, await schema.ScalarAsync<long>(
            $$"""
            SELECT count(*) FROM "NzbBlobCleanupItems"
            WHERE "Id" IN ('{{davNzbBlobId}}', '{{historyNzbBlobId}}', '{{queueId}}')
            """));
        Assert.Equal(1, await schema.ScalarAsync<int>(
            """
            SELECT "Count" FROM "HealthCheckStats"
            WHERE "DateStartInclusive" = 172800 AND "DateEndExclusive" = 259200
              AND "Result" = 1 AND "RepairStatus" = 0
            """));

        await schema.ExecuteAsync(
            $$"""
            UPDATE "HealthCheckResults"
            SET "CreatedAt" = 259201, "Result" = 2, "RepairStatus" = 3
            WHERE "Id" = '{{healthId}}';
            """);
        Assert.Equal(0L, await schema.ScalarAsync<long>(
            """
            SELECT count(*) FROM "HealthCheckStats"
            WHERE "DateStartInclusive" = 172800 AND "DateEndExclusive" = 259200
              AND "Result" = 1 AND "RepairStatus" = 0
            """));
        Assert.Equal(1, await schema.ScalarAsync<int>(
            """
            SELECT "Count" FROM "HealthCheckStats"
            WHERE "DateStartInclusive" = 259200 AND "DateEndExclusive" = 345600
              AND "Result" = 2 AND "RepairStatus" = 3
            """));

        await schema.ExecuteAsync($"DELETE FROM \"HealthCheckResults\" WHERE \"Id\" = '{healthId}'");
        Assert.Equal(0L, await schema.ScalarAsync<long>("SELECT count(*) FROM \"HealthCheckStats\""));
    }

    private static Task<long> ConstraintCountAsync(PostgreSqlTestSchema schema, string constraintType) =>
        schema.ScalarAsync<long>(
            $$"""
            SELECT count(*)
            FROM pg_constraint AS c
            JOIN pg_class AS t ON t.oid = c.conrelid
            JOIN pg_namespace AS n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema()
              AND c.contype = '{{constraintType}}'
              AND t.relname <> '__EFMigrationsHistory_PostgreSql'
            """);

    private static Task<long> ObjectCountAsync(PostgreSqlTestSchema schema, string relationKind) =>
        schema.ScalarAsync<long>(
            $$"""
            SELECT count(*)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
              AND c.relkind = '{{relationKind}}'
            """);

    private static async Task<string[]> ReadStringsAsync(PostgreSqlTestSchema schema, string sql)
    {
        await using var connection = await schema.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

}

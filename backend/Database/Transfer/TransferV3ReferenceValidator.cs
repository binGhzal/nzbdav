using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3ReferenceValidator
{
    internal static async Task<IReadOnlyList<TransferV3ReferenceSummary>> ValidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        CancellationToken cancellationToken)
    {
        var summaries = new List<TransferV3ReferenceSummary>();
        foreach (var table in contract.Tables)
            foreach (var reference in table.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.Policy == TransferV3ReferencePolicy.DeclaredForeignKeyHard
                    || reference.Policy == TransferV3ReferencePolicy.CleanupTombstone)
                    continue;
                if (reference.Policy == TransferV3ReferencePolicy.ConditionalCleanupTombstone)
                {
                    await ValidateConditionalNzbNameCleanupAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                    or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
                {
                    summaries.Add(await SummarizeInformationalAsync(
                        connection, transaction, table.Name, reference, cancellationToken).ConfigureAwait(false));
                    continue;
                }

                var unresolved = await FindFirstUnresolvedAsync(
                    connection, transaction, table.Name, reference, cancellationToken).ConfigureAwait(false);
                if (unresolved is not null)
                {
                    var code = reference.Policy == TransferV3ReferencePolicy.StateAwareHard
                        ? "reference-state"
                        : "reference-hard";
                    throw Failure(code, table.Name, reference.Columns[0], unresolved.Value, unresolved.Ordinal);
                }
            }

        await ValidateOnlyCanonicalRootHasNullParentAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateMetadataSourcesAsync(connection, transaction, contract, cancellationToken)
            .ConfigureAwait(false);
        return summaries;
    }

    private static async Task<UnresolvedReference?> FindFirstUnresolvedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTable,
        TransferV3ReferenceContract reference,
        CancellationToken cancellationToken)
    {
        var sourceColumn = reference.Columns.Single();
        var predicates = new List<string>();
        for (var index = 0; index < reference.PrincipalTables.Count; index++)
        {
            var principal = reference.PrincipalTables[index];
            if (principal == "@blob")
            {
                predicates.Add("EXISTS (SELECT 1 FROM scratch.blob_inventory AS b WHERE b.normalized_uuid = v.normalized_uuid)");
            }
            else
            {
                predicates.Add(
                    $"EXISTS (SELECT 1 FROM scratch.uuid_values AS p{index} "
                    + $"WHERE p{index}.table_name = $principal{index} "
                    + $"AND p{index}.column_name = $principalColumn{index} "
                    + $"AND p{index}.normalized_uuid = v.normalized_uuid)");
            }
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT v.normalized_uuid, ordinal.ordinal FROM scratch.uuid_values AS v "
            + "JOIN scratch.scan_ordinals AS ordinal ON ordinal.table_name = v.table_name "
            + "AND ordinal.source_rowid = v.source_rowid "
            + "WHERE v.table_name = $table AND v.column_name = $column "
            + $"AND NOT ({string.Join(" OR ", predicates)}) "
            + "ORDER BY v.normalized_uuid LIMIT 1;";
        command.Parameters.AddWithValue("$table", sourceTable);
        command.Parameters.AddWithValue("$column", sourceColumn);
        for (var index = 0; index < reference.PrincipalTables.Count; index++)
        {
            if (reference.PrincipalTables[index] == "@blob") continue;
            command.Parameters.AddWithValue($"$principal{index}", reference.PrincipalTables[index]);
            command.Parameters.AddWithValue($"$principalColumn{index}", reference.PrincipalColumns.Single());
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UnresolvedReference(raw.sqlite3_column_blob(reader.Handle, 0).ToArray(), reader.GetInt64(1))
            : null;
    }

    private static async Task<TransferV3ReferenceSummary> SummarizeInformationalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTable,
        TransferV3ReferenceContract reference,
        CancellationToken cancellationToken)
    {
        if (reference.Policy == TransferV3ReferencePolicy.PolymorphicInformationalDigest)
        {
            return await SummarizePolymorphicInformationalAsync(
                connection, transaction, sourceTable, reference, cancellationToken).ConfigureAwait(false);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(reference.Name));
        long count = 0;
        var sourceColumn = reference.Columns.Single();
        var principalPredicates = reference.PrincipalTables.Select((_, index) =>
            $"EXISTS (SELECT 1 FROM scratch.uuid_values AS p{index} "
            + $"WHERE p{index}.table_name = $principal{index} "
            + $"AND p{index}.column_name = $principalColumn{index} "
            + $"AND p{index}.normalized_uuid = v.normalized_uuid)").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT owner.normalized_uuid, v.normalized_uuid FROM scratch.uuid_values AS v "
            + "JOIN scratch.uuid_values AS owner ON owner.table_name = v.table_name "
            + "AND owner.column_name = 'Id' AND owner.source_rowid = v.source_rowid "
            + "WHERE v.table_name = $table AND v.column_name = $column "
            + $"AND NOT ({string.Join(" OR ", principalPredicates)}) "
            + "ORDER BY owner.normalized_uuid, v.normalized_uuid;";
        command.Parameters.AddWithValue("$table", sourceTable);
        command.Parameters.AddWithValue("$column", sourceColumn);
        for (var index = 0; index < reference.PrincipalTables.Count; index++)
        {
            command.Parameters.AddWithValue($"$principal{index}", reference.PrincipalTables[index]);
            command.Parameters.AddWithValue($"$principalColumn{index}", reference.PrincipalColumns.Single());
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hash.AppendData(raw.sqlite3_column_blob(reader.Handle, 0));
            hash.AppendData(raw.sqlite3_column_blob(reader.Handle, 1));
            count++;
        }
        return new TransferV3ReferenceSummary(
            reference.Name,
            count,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<TransferV3ReferenceSummary> SummarizePolymorphicInformationalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceTable,
        TransferV3ReferenceContract reference,
        CancellationToken cancellationToken)
    {
        var discriminatorColumn = reference.DiscriminatorColumn
                                  ?? throw new InvalidDataException("A polymorphic reference has no discriminator.");
        var cases = reference.PolymorphicCases
                    ?? throw new InvalidDataException("A polymorphic reference has no cases.");
        var predicates = cases.Select((_, index) =>
            $"(source_row.{QuoteIdentifier(discriminatorColumn)} = $kind{index} AND EXISTS ("
            + "SELECT 1 FROM scratch.uuid_values AS principal "
            + $"WHERE principal.table_name = $principal{index} "
            + "AND principal.column_name = $principalColumn "
            + "AND principal.normalized_uuid = v.normalized_uuid))").ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(reference.Name));
        long count = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT owner.normalized_uuid, source_row."
            + QuoteIdentifier(discriminatorColumn)
            + ", v.normalized_uuid FROM scratch.uuid_values AS v "
            + "JOIN scratch.uuid_values AS owner ON owner.table_name = v.table_name "
            + "AND owner.column_name = 'Id' AND owner.source_rowid = v.source_rowid "
            + $"JOIN source.{QuoteIdentifier(sourceTable)} AS source_row ON source_row.rowid = v.source_rowid "
            + "WHERE v.table_name = $table AND v.column_name = $column "
            + $"AND NOT ({string.Join(" OR ", predicates)}) "
            + "ORDER BY owner.normalized_uuid, source_row."
            + QuoteIdentifier(discriminatorColumn)
            + ", v.normalized_uuid;";
        command.Parameters.AddWithValue("$table", sourceTable);
        command.Parameters.AddWithValue("$column", reference.Columns.Single());
        command.Parameters.AddWithValue("$principalColumn", reference.PrincipalColumns.Single());
        for (var index = 0; index < cases.Count; index++)
        {
            command.Parameters.AddWithValue($"$kind{index}", cases[index].DiscriminatorValue);
            command.Parameters.AddWithValue($"$principal{index}", cases[index].PrincipalTable);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var discriminator = new byte[sizeof(int)];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hash.AppendData(raw.sqlite3_column_blob(reader.Handle, 0));
            var value = reader.GetInt64(1);
            if (value is < int.MinValue or > int.MaxValue)
                throw new InvalidDataException("A validated polymorphic discriminator was outside Int32.");
            BinaryPrimitives.WriteInt32BigEndian(discriminator, (int)value);
            hash.AppendData(discriminator);
            hash.AppendData(raw.sqlite3_column_blob(reader.Handle, 2));
            count++;
        }

        return new TransferV3ReferenceSummary(
            reference.Name,
            count,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task ValidateConditionalNzbNameCleanupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT names.normalized_uuid, ordinal.ordinal
            FROM scratch.uuid_values AS names
            JOIN scratch.scan_ordinals AS ordinal
              ON ordinal.table_name = names.table_name
             AND ordinal.source_rowid = names.source_rowid
            WHERE names.table_name = 'NzbNames' AND names.column_name = 'Id'
              AND NOT EXISTS (SELECT 1 FROM scratch.blob_inventory AS b
                              WHERE b.normalized_uuid = names.normalized_uuid)
              AND (
                  NOT EXISTS (SELECT 1 FROM scratch.uuid_values AS cleanup
                              WHERE cleanup.table_name = 'NzbBlobCleanupItems'
                                AND cleanup.column_name = 'Id'
                                AND cleanup.normalized_uuid = names.normalized_uuid)
                  OR EXISTS (SELECT 1 FROM scratch.uuid_values AS live
                             WHERE ((live.table_name = 'QueueItems' AND live.column_name = 'Id')
                                 OR (live.table_name = 'DavItems' AND live.column_name = 'NzbBlobId')
                                 OR (live.table_name = 'HistoryItems' AND live.column_name = 'NzbBlobId'))
                               AND live.normalized_uuid = names.normalized_uuid)
              )
            ORDER BY names.normalized_uuid LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure(
                "reference-hard", "NzbNames", "Id", raw.sqlite3_column_blob(reader.Handle, 0),
                reader.GetInt64(1));
    }

    private static async Task ValidateOnlyCanonicalRootHasNullParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT CAST(item.Id AS BLOB), ordinal.ordinal FROM source.DavItems AS item "
            + "JOIN scratch.scan_ordinals AS ordinal ON ordinal.table_name = 'DavItems' "
            + "AND ordinal.source_rowid = item.rowid "
            + "WHERE item.ParentId IS NULL "
            + "AND lower(item.Id) <> '00000000-0000-0000-0000-000000000000' "
            + "ORDER BY ordinal.ordinal LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure(
                "reference-state", "DavItems", "ParentId", raw.sqlite3_column_blob(reader.Handle, 0),
                reader.GetInt64(1));
    }

    private static async Task ValidateMetadataSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        CancellationToken cancellationToken)
    {
        var rule = contract.Tables.Single(table => table.Name == "DavItems").MetadataRule!;
        await ValidateTypeSubtypeDomainAsync(
            connection, transaction, rule, cancellationToken).ConfigureAwait(false);
        foreach (var subtype in rule.Subtypes)
        {
            await using var mismatch = connection.CreateCommand();
            mismatch.Transaction = transaction;
            mismatch.CommandText =
                "SELECT legacy.normalized_uuid, ordinal.ordinal FROM scratch.uuid_values AS legacy "
                + "JOIN scratch.uuid_values AS itemId ON itemId.table_name = 'DavItems' "
                + "AND itemId.column_name = 'Id' AND itemId.normalized_uuid = legacy.normalized_uuid "
                + "JOIN source.DavItems AS item ON item.rowid = itemId.source_rowid "
                + "JOIN scratch.scan_ordinals AS ordinal ON ordinal.table_name = 'DavItems' "
                + "AND ordinal.source_rowid = item.rowid "
                + "WHERE legacy.table_name = $legacy AND legacy.column_name = 'Id' "
                + "AND (item.Type <> 2 OR item.SubType <> $subtype) "
                + "ORDER BY ordinal.ordinal LIMIT 1;";
            mismatch.Parameters.AddWithValue("$legacy", subtype.LegacyTable);
            mismatch.Parameters.AddWithValue("$subtype", subtype.SubType);
            await using var mismatchReader = await mismatch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await mismatchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw Failure(
                    "metadata-subtype", subtype.LegacyTable, "Id",
                    raw.sqlite3_column_blob(mismatchReader.Handle, 0), mismatchReader.GetInt64(1));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var subtypeParameters = rule.Subtypes
            .Select((_, index) => $"$metadataSubtype{index}")
            .ToArray();
        var legacyPredicates = rule.Subtypes
            .Select((_, index) =>
                $"(item.SubType = $metadataSubtype{index} AND EXISTS ("
                + "SELECT 1 FROM scratch.uuid_values AS legacy "
                + $"WHERE legacy.table_name = $metadataTable{index} "
                + "AND legacy.column_name = 'Id' "
                + "AND legacy.normalized_uuid = itemId.normalized_uuid))")
            .ToArray();
        var knownSubtype = $"item.SubType IN ({string.Join(", ", subtypeParameters)})";
        command.CommandText =
            "SELECT itemId.normalized_uuid, "
            + $"CASE WHEN {knownSubtype} THEN 0 ELSE 1 END "
            + ", ordinal.ordinal "
            + "FROM source.DavItems AS item "
            + "JOIN scratch.uuid_values AS itemId "
            + "ON itemId.table_name = 'DavItems' AND itemId.column_name = 'Id' "
            + "AND itemId.source_rowid = item.rowid "
            + "JOIN scratch.scan_ordinals AS ordinal ON ordinal.table_name = 'DavItems' "
            + "AND ordinal.source_rowid = item.rowid "
            + "WHERE item.Type = 2 AND ("
            + $"NOT ({knownSubtype}) OR ("
            + "NOT EXISTS (SELECT 1 FROM scratch.uuid_values AS fileBlob "
            + "WHERE fileBlob.table_name = 'DavItems' "
            + "AND fileBlob.column_name = 'FileBlobId' "
            + "AND fileBlob.source_rowid = item.rowid) "
            + $"AND NOT ({string.Join(" OR ", legacyPredicates)}))) "
            + "ORDER BY ordinal.ordinal LIMIT 1;";
        for (var index = 0; index < rule.Subtypes.Count; index++)
        {
            command.Parameters.AddWithValue($"$metadataSubtype{index}", rule.Subtypes[index].SubType);
            command.Parameters.AddWithValue($"$metadataTable{index}", rule.Subtypes[index].LegacyTable);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
        var id = raw.sqlite3_column_blob(reader.Handle, 0);
        throw reader.GetInt32(1) != 0
            ? Failure("metadata-subtype", "DavItems", "SubType", id, reader.GetInt64(2))
            : Failure("metadata-source", "DavItems", "FileBlobId", id, reader.GetInt64(2));
    }

    private static async Task ValidateTypeSubtypeDomainAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3MetadataRuleContract rule,
        CancellationToken cancellationToken)
    {
        var domains = rule.TypeDomains.Select((domain, domainIndex) =>
        {
            var subtypes = domain.SubTypes.Select((_, subtypeIndex) =>
                $"$domain{domainIndex}Subtype{subtypeIndex}");
            return $"(item.{QuoteIdentifier(rule.TypeColumn)} = $domain{domainIndex}Type "
                   + $"AND item.{QuoteIdentifier(rule.SubTypeColumn)} IN ({string.Join(", ", subtypes)}))";
        }).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT itemId.normalized_uuid, ordinal.ordinal FROM source.DavItems AS item "
            + "JOIN scratch.uuid_values AS itemId ON itemId.table_name = 'DavItems' "
            + "AND itemId.column_name = 'Id' AND itemId.source_rowid = item.rowid "
            + "JOIN scratch.scan_ordinals AS ordinal ON ordinal.table_name = 'DavItems' "
            + "AND ordinal.source_rowid = item.rowid "
            + $"WHERE NOT ({string.Join(" OR ", domains)}) "
            + "ORDER BY ordinal.ordinal LIMIT 1;";
        for (var domainIndex = 0; domainIndex < rule.TypeDomains.Count; domainIndex++)
        {
            var domain = rule.TypeDomains[domainIndex];
            command.Parameters.AddWithValue($"$domain{domainIndex}Type", domain.Type);
            for (var subtypeIndex = 0; subtypeIndex < domain.SubTypes.Count; subtypeIndex++)
                command.Parameters.AddWithValue(
                    $"$domain{domainIndex}Subtype{subtypeIndex}", domain.SubTypes[subtypeIndex]);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Failure(
                "type-subtype-domain", "DavItems", "Type+SubType",
                raw.sqlite3_column_blob(reader.Handle, 0), reader.GetInt64(1));
    }

    private static TransferV3SourceValidationException Failure(
        string code, string table, string column, ReadOnlySpan<byte> value, long ordinal) =>
        TransferV3SourceValidationException.Create(
            code,
            table,
            column,
            ordinal,
            Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()[..12]);

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record UnresolvedReference(byte[] Value, long Ordinal);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3SqliteSchemaManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    internal static async Task ValidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var expected = JsonSerializer.Deserialize<SourceSchemaManifest>(
                           TransferV3SourceContract.ReadEmbeddedSourceSchema(), JsonOptions)
                       ?? throw new InvalidDataException("The embedded SQLite schema manifest is empty.");

        // sqlite_schema is checked first and capped at expected+1. Only names from the
        // reviewed manifest are used for subsequent PRAGMA reads, so hostile objects
        // cannot create attacker-controlled PRAGMA fanout.
        await ValidateSqliteSchemaAsync(
            connection, transaction, expected.Physical.SqliteSchema, cancellationToken).ConfigureAwait(false);
        foreach (var capture in expected.Physical.TableXInfo)
            await ValidateTableXInfoAsync(
                connection, transaction, capture, cancellationToken).ConfigureAwait(false);
        foreach (var capture in expected.Physical.IndexList)
            await ValidateIndexListAsync(
                connection, transaction, capture, cancellationToken).ConfigureAwait(false);
        foreach (var capture in expected.Physical.IndexXInfo)
            await ValidateIndexXInfoAsync(
                connection, transaction, capture, cancellationToken).ConfigureAwait(false);
        foreach (var capture in expected.Physical.ForeignKeyList)
            await ValidateForeignKeysAsync(
                connection, transaction, capture, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSqliteSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SchemaRow> expected,
        CancellationToken cancellationToken)
    {
        var maxType = MaxUtf8(expected.Select(row => row.Type));
        var maxName = MaxUtf8(expected.Select(row => row.Name));
        var maxTable = MaxUtf8(expected.Select(row => row.TableName));
        var maxSql = MaxUtf8(expected.Where(row => row.Sql.Value is not null).Select(row => row.Sql.Value!));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT typeof(type), length(CAST(type AS BLOB)), type, "
            + "typeof(name), length(CAST(name AS BLOB)), name, "
            + "typeof(tbl_name), length(CAST(tbl_name AS BLOB)), tbl_name, "
            + "typeof(sql), length(CAST(sql AS BLOB)), sql "
            + "FROM source.sqlite_schema ORDER BY type, name, tbl_name LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", expected.Count + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Count) throw Drift("sqlite_schema", index + 1);
            var actual = new SchemaRow(
                ReadBoundedText(reader, 0, 1, 2, maxType),
                ReadBoundedText(reader, 3, 4, 5, maxName),
                ReadBoundedText(reader, 6, 7, 8, maxTable),
                ReadSql(reader, 9, 10, 11, maxSql));
            if (actual != expected[index]) throw Drift("sqlite_schema", index + 1);
            index++;
        }
        if (index != expected.Count) throw Drift("sqlite_schema", index + 1);
    }

    private static async Task ValidateTableXInfoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableXInfoCapture expected,
        CancellationToken cancellationToken)
    {
        var maxName = MaxUtf8(expected.Rows.Select(row => row.Name));
        var maxType = MaxUtf8(expected.Rows.Select(row => row.DeclaredType));
        var maxDefault = MaxUtf8(expected.Rows.Where(row => row.DefaultValue.Value is not null)
            .Select(row => row.DefaultValue.Value!));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT cid, typeof(name), length(CAST(name AS BLOB)), name, "
            + "typeof(type), length(CAST(type AS BLOB)), type, \"notnull\", "
            + "typeof(dflt_value), length(CAST(dflt_value AS BLOB)), dflt_value, pk, hidden "
            + "FROM pragma_table_xinfo($table, 'source') ORDER BY cid LIMIT $limit;";
        command.Parameters.AddWithValue("$table", expected.Table);
        command.Parameters.AddWithValue("$limit", expected.Rows.Count + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Rows.Count) throw Drift("table_xinfo:" + expected.Table, index + 1);
            var actual = new TableXInfoRow(
                ReadInt32(reader, 0),
                ReadBoundedText(reader, 1, 2, 3, maxName),
                ReadBoundedText(reader, 4, 5, 6, maxType),
                ReadInt32(reader, 7) != 0,
                ReadSql(reader, 8, 9, 10, maxDefault),
                ReadInt32(reader, 11),
                ReadInt32(reader, 12));
            if (actual != expected.Rows[index]) throw Drift("table_xinfo:" + expected.Table, index + 1);
            index++;
        }
        if (index != expected.Rows.Count) throw Drift("table_xinfo:" + expected.Table, index + 1);
    }

    private static async Task ValidateIndexListAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IndexListCapture expected,
        CancellationToken cancellationToken)
    {
        var maxName = MaxUtf8(expected.Rows.Select(row => row.Name));
        var maxOrigin = MaxUtf8(expected.Rows.Select(row => row.Origin));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT seq, typeof(name), length(CAST(name AS BLOB)), name, \"unique\", "
            + "typeof(origin), length(CAST(origin AS BLOB)), origin, partial "
            + "FROM pragma_index_list($table, 'source') "
            + "ORDER BY seq, name COLLATE BINARY LIMIT $limit;";
        command.Parameters.AddWithValue("$table", expected.Table);
        command.Parameters.AddWithValue("$limit", expected.Rows.Count + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Rows.Count) throw Drift("index_list:" + expected.Table, index + 1);
            var actual = new IndexListRow(
                ReadInt32(reader, 0),
                ReadBoundedText(reader, 1, 2, 3, maxName),
                ReadInt32(reader, 4) != 0,
                ReadBoundedText(reader, 5, 6, 7, maxOrigin),
                ReadInt32(reader, 8) != 0);
            if (actual != expected.Rows[index]) throw Drift("index_list:" + expected.Table, index + 1);
            index++;
        }
        if (index != expected.Rows.Count) throw Drift("index_list:" + expected.Table, index + 1);
    }

    private static async Task ValidateIndexXInfoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IndexXInfoCapture expected,
        CancellationToken cancellationToken)
    {
        var maxName = MaxUtf8(expected.Rows.Where(row => row.Name is not null).Select(row => row.Name!));
        var maxCollation = MaxUtf8(expected.Rows.Select(row => row.Collation));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT seqno, cid, typeof(name), length(CAST(name AS BLOB)), name, desc, "
            + "typeof(coll), length(CAST(coll AS BLOB)), coll, key "
            + "FROM pragma_index_xinfo($index, 'source') ORDER BY seqno LIMIT $limit;";
        command.Parameters.AddWithValue("$index", expected.Index);
        command.Parameters.AddWithValue("$limit", expected.Rows.Count + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Rows.Count) throw Drift("index_xinfo:" + expected.Index, index + 1);
            var actual = new IndexXInfoRow(
                ReadInt32(reader, 0),
                ReadInt32(reader, 1),
                ReadNullableBoundedText(reader, 2, 3, 4, maxName),
                ReadInt32(reader, 5) != 0,
                ReadBoundedText(reader, 6, 7, 8, maxCollation),
                ReadInt32(reader, 9) != 0);
            if (actual != expected.Rows[index]) throw Drift("index_xinfo:" + expected.Index, index + 1);
            index++;
        }
        if (index != expected.Rows.Count) throw Drift("index_xinfo:" + expected.Index, index + 1);
    }

    private static async Task ValidateForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ForeignKeyListCapture expected,
        CancellationToken cancellationToken)
    {
        var maxPrincipal = MaxUtf8(expected.Rows.Select(row => row.PrincipalTable));
        var maxFrom = MaxUtf8(expected.Rows.Select(row => row.FromColumn));
        var maxTo = MaxUtf8(expected.Rows.Where(row => row.ToColumn is not null).Select(row => row.ToColumn!));
        var maxUpdate = MaxUtf8(expected.Rows.Select(row => row.OnUpdate));
        var maxDelete = MaxUtf8(expected.Rows.Select(row => row.OnDelete));
        var maxMatch = MaxUtf8(expected.Rows.Select(row => row.Match));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, seq, typeof(\"table\"), length(CAST(\"table\" AS BLOB)), \"table\", "
            + "typeof(\"from\"), length(CAST(\"from\" AS BLOB)), \"from\", "
            + "typeof(\"to\"), length(CAST(\"to\" AS BLOB)), \"to\", "
            + "typeof(on_update), length(CAST(on_update AS BLOB)), on_update, "
            + "typeof(on_delete), length(CAST(on_delete AS BLOB)), on_delete, "
            + "typeof(match), length(CAST(match AS BLOB)), match "
            + "FROM pragma_foreign_key_list($table, 'source') ORDER BY id, seq LIMIT $limit;";
        command.Parameters.AddWithValue("$table", expected.Table);
        command.Parameters.AddWithValue("$limit", expected.Rows.Count + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Rows.Count) throw Drift("foreign_key_list:" + expected.Table, index + 1);
            var actual = new ForeignKeyRow(
                ReadInt32(reader, 0),
                ReadInt32(reader, 1),
                ReadBoundedText(reader, 2, 3, 4, maxPrincipal),
                ReadBoundedText(reader, 5, 6, 7, maxFrom),
                ReadNullableBoundedText(reader, 8, 9, 10, maxTo),
                ReadBoundedText(reader, 11, 12, 13, maxUpdate),
                ReadBoundedText(reader, 14, 15, 16, maxDelete),
                ReadBoundedText(reader, 17, 18, 19, maxMatch));
            if (actual != expected.Rows[index]) throw Drift("foreign_key_list:" + expected.Table, index + 1);
            index++;
        }
        if (index != expected.Rows.Count) throw Drift("foreign_key_list:" + expected.Table, index + 1);
    }

    private static string ReadBoundedText(
        SqliteDataReader reader, int storageOrdinal, int lengthOrdinal, int valueOrdinal, int maxBytes)
    {
        if (reader.GetString(storageOrdinal) != "text"
            || reader.IsDBNull(lengthOrdinal)
            || reader.GetInt64(lengthOrdinal) < 0
            || reader.GetInt64(lengthOrdinal) > maxBytes)
            throw Drift("raw-text", 0);
        return reader.GetString(valueOrdinal);
    }

    private static string? ReadNullableBoundedText(
        SqliteDataReader reader, int storageOrdinal, int lengthOrdinal, int valueOrdinal, int maxBytes) =>
        reader.GetString(storageOrdinal) == "null"
            ? reader.IsDBNull(lengthOrdinal) && reader.IsDBNull(valueOrdinal)
                ? null
                : throw Drift("raw-null", 0)
            : ReadBoundedText(reader, storageOrdinal, lengthOrdinal, valueOrdinal, maxBytes);

    private static SqlText ReadSql(
        SqliteDataReader reader, int storageOrdinal, int lengthOrdinal, int valueOrdinal, int maxBytes) =>
        reader.GetString(storageOrdinal) == "null"
            ? reader.IsDBNull(lengthOrdinal) && reader.IsDBNull(valueOrdinal)
                ? new SqlText("database-null", null)
                : throw Drift("raw-null", 0)
            : new SqlText("sql", ReadBoundedText(
                reader, storageOrdinal, lengthOrdinal, valueOrdinal, maxBytes));

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetInt64(ordinal);
        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw Drift("integer-range", 0);
    }

    private static int MaxUtf8(IEnumerable<string> values)
    {
        var max = 0;
        foreach (var value in values) max = Math.Max(max, Encoding.UTF8.GetByteCount(value));
        return max;
    }

    private static TransferV3SourceValidationException Drift(string section, int ordinal)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(section)))
            .ToLowerInvariant()[..12];
        return TransferV3SourceValidationException.Create(
            "schema-drift", "<schema>", section, ordinal, digest);
    }

    private sealed record SourceSchemaManifest(PhysicalSchema Physical);
    private sealed record PhysicalSchema(
        IReadOnlyList<SchemaRow> SqliteSchema,
        IReadOnlyList<TableXInfoCapture> TableXInfo,
        IReadOnlyList<IndexListCapture> IndexList,
        IReadOnlyList<IndexXInfoCapture> IndexXInfo,
        IReadOnlyList<ForeignKeyListCapture> ForeignKeyList,
        IReadOnlyList<string> TriggerNames,
        IReadOnlyList<string> ViewNames,
        IReadOnlyList<SchemaRow> UnrecognizedObjects);
    private sealed record SchemaRow(string Type, string Name, string TableName, SqlText Sql);
    private sealed record SqlText(string Kind, string? Value);
    private sealed record TableXInfoCapture(string Table, IReadOnlyList<TableXInfoRow> Rows);
    private sealed record IndexListCapture(string Table, IReadOnlyList<IndexListRow> Rows);
    private sealed record IndexXInfoCapture(string Index, IReadOnlyList<IndexXInfoRow> Rows);
    private sealed record ForeignKeyListCapture(string Table, IReadOnlyList<ForeignKeyRow> Rows);
    private sealed record TableXInfoRow(
        int Cid, string Name, string DeclaredType, bool NotNull, SqlText DefaultValue,
        int PrimaryKeyOrdinal, int Hidden);
    private sealed record IndexListRow(int Sequence, string Name, bool Unique, string Origin, bool Partial);
    private sealed record IndexXInfoRow(
        int Sequence, int Cid, string? Name, bool Descending, string Collation, bool IsKey);
    private sealed record ForeignKeyRow(
        int Id, int Sequence, string PrincipalTable, string FromColumn, string? ToColumn,
        string OnUpdate, string OnDelete, string Match);
}

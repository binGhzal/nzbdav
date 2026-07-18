using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3SqliteRawScanner
{
    private const int IoBufferBytes = 16 * 1024;
    private const int MaxCapturedTextBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] LocalWallFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    internal static async Task<TransferV3RawScanResult> ScanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        TransferV3SqliteValidationOptions options,
        CancellationToken cancellationToken)
    {
        await BuildNormalizedUuidKeysetsAsync(
            connection, transaction, contract, options, cancellationToken).ConfigureAwait(false);

        var tables = new List<TransferV3ValidatedTable>(contract.Tables.Count);
        var maxRows = 0;
        long maxBytes = 0;
        foreach (var table in contract.Tables.Concat(contract.DerivedTables))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metrics = await ScanTableAsync(
                connection, transaction, table, options, cancellationToken).ConfigureAwait(false);
            maxRows = Math.Max(maxRows, metrics.MaxRowsPerBatch);
            maxBytes = Math.Max(maxBytes, metrics.MaxBytesPerBatch);
            if (contract.Tables.Contains(table))
            {
                tables.Add(new TransferV3ValidatedTable(
                    table.Name,
                    metrics.RowCount,
                    table.Keyset,
                    BuildOrder(table.Keyset, sqlite: true),
                    BuildOrder(table.Keyset, sqlite: false)));
            }
        }

        return new TransferV3RawScanResult(tables, maxRows, maxBytes, IoBufferBytes);
    }

    private static async Task BuildNormalizedUuidKeysetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        TransferV3SqliteValidationOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var table in contract.Tables)
        {
            if (table.Keyset.Count != 1) continue;
            var keyColumn = table.Columns.Single(column => column.Name == table.Keyset[0].Column);
            if (keyColumn.Kind != TransferV3ColumnKind.Uuid) continue;

            long? lastRowId = null;
            long rowOrdinal = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<(long RowId, byte[] Key, long Ordinal)>(options.MaxRowsPerBatch);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        $"SELECT rowid, typeof({QuoteIdentifier(keyColumn.Name)}), "
                        + $"CAST({QuoteIdentifier(keyColumn.Name)} AS BLOB) "
                        + $"FROM source.{QuoteIdentifier(table.Name)} "
                        + "WHERE ($first = 1 OR rowid > $last) ORDER BY rowid LIMIT $limit;";
                    command.Parameters.AddWithValue("$first", lastRowId is null ? 1 : 0);
                    command.Parameters.AddWithValue("$last", lastRowId ?? 0);
                    command.Parameters.AddWithValue("$limit", options.MaxRowsPerBatch);
                    await using var reader = await command.ExecuteReaderAsync(
                        CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var rowId = reader.GetInt64(0);
                        rowOrdinal++;
                        var storage = reader.GetString(1);
                        if (storage != "text")
                            throw Failure("storage-class", table.Name, keyColumn.Name, rowOrdinal, Digest(storage));
                        var text = ReadText(
                            reader, 2, capture: true, keyColumn.MaxRunes, cancellationToken);
                        if (text.InvalidUtf8)
                            throw Failure("utf8", table.Name, keyColumn.Name, rowOrdinal, text.DigestPrefix);
                        if (text.ContainsNul)
                            throw Failure("text-nul", table.Name, keyColumn.Name, rowOrdinal, text.DigestPrefix);
                        var key = ParseUuid(text.Text, table.Name, keyColumn.Name, rowOrdinal, text.DigestPrefix);
                        batch.Add((rowId, GuidNetworkBytes(key), rowOrdinal));
                        lastRowId = rowId;
                    }
                }

                if (batch.Count == 0) break;
                foreach (var entry in batch)
                {
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO scratch.normalized_keysets(table_name, normalized_key, source_rowid) "
                        + "VALUES ($table, $key, $rowid);";
                    insert.Parameters.AddWithValue("$table", table.Name);
                    insert.Parameters.Add("$key", SqliteType.Blob).Value = entry.Key;
                    insert.Parameters.AddWithValue("$rowid", entry.RowId);
                    try
                    {
                        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                    {
                        throw Failure(
                            "uuid-normalized-collision", table.Name, keyColumn.Name, entry.Ordinal,
                            Digest(entry.Key));
                    }
                }
            }
        }
    }

    private static async Task<TableScanMetrics> ScanTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        TransferV3SqliteValidationOptions options,
        CancellationToken cancellationToken)
    {
        var uuidKeyset = table.Keyset.Count == 1
                         && table.Columns.Single(column => column.Name == table.Keyset[0].Column).Kind
                         == TransferV3ColumnKind.Uuid;
        byte[]? uuidCursor = null;
        long? accountTypeCursor = null;
        string? textCursor = null;
        long? rowIdCursor = null;
        long rowCount = 0;
        var maxRows = 0;
        long maxBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchRows = new List<ValidatedRow>(options.MaxRowsPerBatch);
            var batchBytes = 0L;
            await using (var command = BuildScanCommand(
                             connection,
                             transaction,
                             table,
                             uuidKeyset,
                             uuidCursor,
                             accountTypeCursor,
                             textCursor,
                             rowIdCursor,
                             options.MaxRowsPerBatch))
            await using (var reader = await command.ExecuteReaderAsync(
                             CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[]? normalizedCursor = null;
                    var ordinal = 0;
                    if (uuidKeyset) normalizedCursor = reader.GetFieldValue<byte[]>(ordinal++);
                    var rowId = reader.GetInt64(ordinal++);
                    var cells = new Dictionary<string, ValidatedCell>(StringComparer.Ordinal);
                    long rowBytes = 0;
                    foreach (var column in table.Columns)
                    {
                        var storage = reader.GetString(ordinal++);
                        var valueOrdinal = ordinal++;
                        var cell = ValidateCell(
                            reader,
                            valueOrdinal,
                            storage,
                            table,
                            column,
                            rowCount + batchRows.Count + 1,
                            cells,
                            cancellationToken);
                        rowBytes = checked(rowBytes + cell.RawBytes);
                        cells.Add(column.Name, cell);
                    }

                    var validated = new ValidatedRow(rowId, cells, rowBytes);
                    batchRows.Add(validated);
                    batchBytes = checked(batchBytes + rowBytes);
                    if (uuidKeyset) uuidCursor = normalizedCursor;
                    else if (table.Name == "Accounts")
                    {
                        accountTypeCursor = cells["Type"].Integer;
                        textCursor = cells["Username"].Text;
                    }
                    else if (table.Name == "ConfigItems")
                    {
                        textCursor = cells["ConfigName"].Text;
                    }
                    else if (table.Name == "HealthCheckStats")
                    {
                        rowIdCursor = rowId;
                    }
                    else
                    {
                        throw new InvalidDataException("An unreviewed Transfer-v3 keyset was encountered.");
                    }

                    if (batchBytes >= options.MaxBytesPerBatch) break;
                }
            }

            if (batchRows.Count == 0) break;
            foreach (var row in batchRows)
            {
                await PersistScanOrdinalAsync(
                    connection, transaction, table.Name, row.RowId, rowCount + 1, cancellationToken)
                    .ConfigureAwait(false);
                await PersistValidatedFieldsAsync(
                    connection, transaction, table, row, cancellationToken).ConfigureAwait(false);
                await PersistUuidValuesAsync(
                    connection, transaction, table, row, cancellationToken).ConfigureAwait(false);
                await PersistNormalizedUniqueValuesAsync(
                    connection, transaction, table, row, rowCount + 1, cancellationToken).ConfigureAwait(false);
                rowCount++;
            }
            maxRows = Math.Max(maxRows, batchRows.Count);
            maxBytes = Math.Max(maxBytes, batchBytes);
            options.Progress?.Report(new TransferV3ValidationProgress(
                "table-batch-validated", table.Name, rowCount));
        }

        await using var coverage = connection.CreateCommand();
        coverage.Transaction = transaction;
        coverage.CommandText = $"SELECT count(*) FROM source.{QuoteIdentifier(table.Name)};";
        var physicalRowCount = Convert.ToInt64(
            await coverage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (physicalRowCount != rowCount)
        {
            throw Failure(
                "row-coverage",
                table.Name,
                "rowid",
                rowCount,
                Digest($"{physicalRowCount}:{rowCount}"));
        }

        return new TableScanMetrics(rowCount, maxRows, maxBytes);
    }

    private static async Task PersistScanOrdinalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long sourceRowId,
        long ordinal,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO scratch.scan_ordinals(table_name, source_rowid, ordinal) "
            + "VALUES ($table, $rowid, $ordinal);";
        insert.Parameters.AddWithValue("$table", table);
        insert.Parameters.AddWithValue("$rowid", sourceRowId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand BuildScanCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        bool uuidKeyset,
        byte[]? uuidCursor,
        long? accountTypeCursor,
        string? textCursor,
        long? rowIdCursor,
        int limit)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var prefix = uuidKeyset ? "k.normalized_key, s.rowid" : "s.rowid";
        var cellSql = string.Join(", ", table.Columns.Select(column =>
            $"typeof(s.{QuoteIdentifier(column.Name)}), "
            + $"CASE WHEN typeof(s.{QuoteIdentifier(column.Name)}) = 'text' "
            + $"THEN CAST(s.{QuoteIdentifier(column.Name)} AS BLOB) "
            + $"ELSE s.{QuoteIdentifier(column.Name)} END"));

        if (uuidKeyset)
        {
            command.CommandText =
                $"SELECT {prefix}, {cellSql} FROM source.{QuoteIdentifier(table.Name)} AS s "
                + "JOIN scratch.normalized_keysets AS k "
                + "ON k.table_name = $table AND k.source_rowid = s.rowid "
                + "WHERE ($first = 1 OR k.normalized_key > $cursor) "
                + "ORDER BY k.normalized_key LIMIT $limit;";
            command.Parameters.AddWithValue("$table", table.Name);
            command.Parameters.AddWithValue("$first", uuidCursor is null ? 1 : 0);
            command.Parameters.Add("$cursor", SqliteType.Blob).Value = uuidCursor ?? [];
        }
        else if (table.Name == "Accounts")
        {
            command.CommandText =
                $"SELECT {prefix}, {cellSql} FROM source.{QuoteIdentifier(table.Name)} AS s "
                + "WHERE ($first = 1 OR s.\"Type\" > $type "
                + "OR (s.\"Type\" = $type AND s.\"Username\" COLLATE BINARY > $text)) "
                + "ORDER BY s.\"Type\", s.\"Username\" COLLATE BINARY LIMIT $limit;";
            command.Parameters.AddWithValue("$first", accountTypeCursor is null ? 1 : 0);
            command.Parameters.AddWithValue("$type", accountTypeCursor ?? 0);
            command.Parameters.AddWithValue("$text", textCursor ?? "");
        }
        else if (table.Name == "ConfigItems")
        {
            command.CommandText =
                $"SELECT {prefix}, {cellSql} FROM source.{QuoteIdentifier(table.Name)} AS s "
                + "WHERE ($first = 1 OR s.\"ConfigName\" COLLATE BINARY > $text) "
                + "ORDER BY s.\"ConfigName\" COLLATE BINARY LIMIT $limit;";
            command.Parameters.AddWithValue("$first", textCursor is null ? 1 : 0);
            command.Parameters.AddWithValue("$text", textCursor ?? "");
        }
        else if (table.Name == "HealthCheckStats")
        {
            command.CommandText =
                $"SELECT {prefix}, {cellSql} FROM source.{QuoteIdentifier(table.Name)} AS s "
                + "WHERE ($first = 1 OR s.rowid > $rowid) ORDER BY s.rowid LIMIT $limit;";
            command.Parameters.AddWithValue("$first", rowIdCursor is null ? 1 : 0);
            command.Parameters.AddWithValue("$rowid", rowIdCursor ?? 0);
        }
        else
        {
            command.Dispose();
            throw new InvalidDataException("An unreviewed Transfer-v3 keyset was encountered.");
        }

        command.Parameters.AddWithValue("$limit", limit);
        return command;
    }

    private static ValidatedCell ValidateCell(
        SqliteDataReader reader,
        int valueOrdinal,
        string storage,
        TransferV3TableContract table,
        TransferV3ColumnContract column,
        long rowOrdinal,
        IReadOnlyDictionary<string, ValidatedCell> priorCells,
        CancellationToken cancellationToken)
    {
        if (storage == "null")
        {
            if (!column.Nullable)
                throw Failure("required-null", table.Name, column.Name, rowOrdinal, Digest("null"));
            return ValidatedCell.Null;
        }
        if (storage != column.RawStorageClass)
            throw Failure("storage-class", table.Name, column.Name, rowOrdinal, Digest(storage));

        if (storage == "integer")
        {
            var value = reader.GetInt64(valueOrdinal);
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            var digest = Digest(bytes);
            switch (column.Kind)
            {
                case TransferV3ColumnKind.Boolean when value is not 0 and not 1:
                    throw Failure("boolean-domain", table.Name, column.Name, rowOrdinal, digest);
                case TransferV3ColumnKind.EnumInt32 when value is < int.MinValue or > int.MaxValue:
                case TransferV3ColumnKind.Int32 when value is < int.MinValue or > int.MaxValue:
                    throw Failure("int32-range", table.Name, column.Name, rowOrdinal, digest);
                case TransferV3ColumnKind.EnumInt32 when !column.AllowedIntegers.Contains(value):
                    throw Failure("enum-domain", table.Name, column.Name, rowOrdinal, digest);
                case TransferV3ColumnKind.Instant when column.InstantEncoding == TransferV3InstantEncoding.UtcTicks
                                                       && (value < 0 || value > DateTime.MaxValue.Ticks):
                    throw Failure("instant-range", table.Name, column.Name, rowOrdinal, digest);
                case TransferV3ColumnKind.Instant when column.InstantEncoding == TransferV3InstantEncoding.UnixSeconds
                                                       && (value < DateTimeOffset.MinValue.ToUnixTimeSeconds()
                                                           || value > DateTimeOffset.MaxValue.ToUnixTimeSeconds()):
                    throw Failure("instant-range", table.Name, column.Name, rowOrdinal, digest);
            }
            return new ValidatedCell(false, value, null, null, null, sizeof(long), digest, null);
        }

        var capture = ShouldCaptureText(table, column, priorCells);
        var text = ReadText(reader, valueOrdinal, capture, column.MaxRunes, cancellationToken);
        if (text.InvalidUtf8)
            throw Failure("utf8", table.Name, column.Name, rowOrdinal, text.DigestPrefix);
        if (text.ContainsNul)
            throw Failure("text-nul", table.Name, column.Name, rowOrdinal, text.DigestPrefix);
        if (text.CaptureTooLarge)
            throw Failure("keyset-text-bytes", table.Name, column.Name, rowOrdinal, text.DigestPrefix);
        if (column.MaxRunes is { } maxRunes && text.RuneCount > maxRunes)
            throw Failure("varchar-runes", table.Name, column.Name, rowOrdinal, text.DigestPrefix);

        if (column.Kind == TransferV3ColumnKind.Uuid)
        {
            var uuid = ParseUuid(text.Text, table.Name, column.Name, rowOrdinal, text.DigestPrefix);
            return new ValidatedCell(
                false, null, uuid.ToString("D").ToLowerInvariant(), uuid, GuidNetworkBytes(uuid),
                text.RawBytes, text.DigestPrefix, text.ContentSha256);
        }
        if (column.Kind == TransferV3ColumnKind.LocalWallTimestamp)
        {
            if (text.Text is null
                || !DateTime.TryParseExact(
                    text.Text,
                    LocalWallFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var value)
                || value.Kind != DateTimeKind.Unspecified)
            {
                throw Failure("timestamp-local-wall", table.Name, column.Name, rowOrdinal, text.DigestPrefix);
            }
            if (value.Ticks % 10 != 0)
                throw Failure("timestamp-microseconds", table.Name, column.Name, rowOrdinal, text.DigestPrefix);
        }

        return new ValidatedCell(
            false, null, text.Text, null, null, text.RawBytes, text.DigestPrefix, text.ContentSha256);
    }

    private static bool ShouldCaptureText(
        TransferV3TableContract table,
        TransferV3ColumnContract column,
        IReadOnlyDictionary<string, ValidatedCell> priorCells)
    {
        if (column.Kind is TransferV3ColumnKind.Uuid or TransferV3ColumnKind.LocalWallTimestamp) return true;
        if (column.MaxRunes is not null) return true;
        if (table.Keyset.Any(component => component.Column == column.Name)) return true;
        if (table.UniqueKeys.Any(key =>
                key.Columns.Contains(column.Name, StringComparer.Ordinal)
                && key.Columns.Any(name => table.Columns.Single(value => value.Name == name).Kind
                    == TransferV3ColumnKind.Uuid))) return true;
        if (table.Name == "ConfigItems" && column.Name == "ConfigValue"
            && priorCells.TryGetValue("ConfigName", out var name)
            && name.Text is "api.key" or "api.strm-key" or "database.import-state") return true;
        if (table.Name == "DavItems" && column.Name is "IdPrefix" or "Path"
            && priorCells.TryGetValue("Id", out var id)
            && id.Text?.StartsWith("00000000-0000-0000-0000-00000000000", StringComparison.Ordinal) == true)
            return true;
        return false;
    }

    private static async Task PersistUuidValuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        ValidatedRow row,
        CancellationToken cancellationToken)
    {
        foreach (var column in table.Columns.Where(column => column.Kind == TransferV3ColumnKind.Uuid))
        {
            var cell = row.Cells[column.Name];
            if (cell.IsNull) continue;
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO scratch.uuid_values(table_name, column_name, source_rowid, normalized_uuid) "
                + "VALUES ($table, $column, $rowid, $uuid);";
            insert.Parameters.AddWithValue("$table", table.Name);
            insert.Parameters.AddWithValue("$column", column.Name);
            insert.Parameters.AddWithValue("$rowid", row.RowId);
            insert.Parameters.Add("$uuid", SqliteType.Blob).Value = cell.UuidNetwork!;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PersistValidatedFieldsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        ValidatedRow row,
        CancellationToken cancellationToken)
    {
        foreach (var column in table.Columns.Where(column => column.RawStorageClass == "text"))
        {
            var cell = row.Cells[column.Name];
            if (cell.IsNull) continue;
            if (cell.ContentSha256 is not { Length: 32 })
                throw new InvalidDataException("A validated Transfer-v3 text cell has no full content digest.");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO scratch.validated_fields("
                + "table_name, source_rowid, column_name, length_bytes, content_sha256) "
                + "VALUES ($table, $rowid, $column, $length, $digest);";
            insert.Parameters.Add("$table", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(table.Name);
            insert.Parameters.AddWithValue("$rowid", row.RowId);
            insert.Parameters.Add("$column", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(column.Name);
            insert.Parameters.AddWithValue("$length", cell.RawBytes);
            insert.Parameters.Add("$digest", SqliteType.Blob).Value = cell.ContentSha256;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PersistNormalizedUniqueValuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        ValidatedRow row,
        long rowOrdinal,
        CancellationToken cancellationToken)
    {
        foreach (var unique in table.UniqueKeys)
        {
            var columns = unique.Columns.Select(name => table.Columns.Single(column => column.Name == name)).ToArray();
            if (!columns.Any(column => column.Kind == TransferV3ColumnKind.Uuid)) continue;
            var cells = unique.Columns.Select(name => row.Cells[name]).ToArray();
            if (cells.Any(cell => cell.IsNull)) continue;
            var key = EncodeUniqueKey(columns, cells);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO scratch.unique_values(rule_name, normalized_key, source_rowid) "
                + "VALUES ($rule, $key, $rowid);";
            insert.Parameters.AddWithValue("$rule", $"{table.Name}.{unique.Name}");
            insert.Parameters.Add("$key", SqliteType.Blob).Value = key;
            insert.Parameters.AddWithValue("$rowid", row.RowId);
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw Failure(
                    "unique-normalized-collision", table.Name, string.Join("+", unique.Columns),
                    rowOrdinal, Digest(key));
            }
        }
    }

    private static byte[] EncodeUniqueKey(
        IReadOnlyList<TransferV3ColumnContract> columns,
        IReadOnlyList<ValidatedCell> cells)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[sizeof(long)];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var cell = cells[index];
            if (column.Kind == TransferV3ColumnKind.Uuid)
            {
                stream.WriteByte(1);
                stream.Write(cell.UuidNetwork!);
            }
            else if (column.Kind is TransferV3ColumnKind.Boolean or TransferV3ColumnKind.EnumInt32
                     or TransferV3ColumnKind.Int32 or TransferV3ColumnKind.Int64
                     or TransferV3ColumnKind.Instant)
            {
                stream.WriteByte(2);
                BinaryPrimitives.WriteInt64BigEndian(number, cell.Integer!.Value);
                stream.Write(number);
            }
            else
            {
                stream.WriteByte(3);
                var bytes = StrictUtf8.GetBytes(cell.Text!);
                BinaryPrimitives.WriteInt64BigEndian(number, bytes.Length);
                stream.Write(number);
                stream.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    private static TextReadResult ReadText(
        SqliteDataReader reader,
        int ordinal,
        bool capture,
        int? maxRunes,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var captured = capture ? new MemoryStream() : null;
        var charBuffer = ArrayPool<char>.Shared.Rent(IoBufferBytes);
        var decoder = StrictUtf8.GetDecoder();
        var invalid = false;
        var containsNul = false;
        var pendingHighSurrogate = false;
        long runes = 0;
        long rawBytes = 0;
        var captureTooLarge = false;
        try
        {
            var bytes = raw.sqlite3_column_blob(reader.Handle, ordinal);
            rawBytes = bytes.Length;
            hasher.AppendData(bytes);
            if (capture)
            {
                captureTooLarge = rawBytes > MaxCapturedTextBytes;
                if (!captureTooLarge)
                    captured!.Write(bytes);
            }
            var offset = 0;
            while (offset < bytes.Length && !invalid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(IoBufferBytes, bytes.Length - offset);
                try
                {
                    DecodeChunk(
                        decoder, bytes.Slice(offset, length), charBuffer, flush: false,
                        ref runes, ref containsNul, ref pendingHighSurrogate);
                }
                catch (DecoderFallbackException)
                {
                    invalid = true;
                }
                offset += length;
            }
            if (!invalid)
            {
                try
                {
                    DecodeChunk(
                        decoder, ReadOnlySpan<byte>.Empty, charBuffer, flush: true,
                        ref runes, ref containsNul, ref pendingHighSurrogate);
                }
                catch (DecoderFallbackException)
                {
                    invalid = true;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer, clearArray: true);
        }

        var contentSha256 = hasher.GetHashAndReset();
        var digest = Convert.ToHexString(contentSha256).ToLowerInvariant()[..12];
        var text = capture && !captureTooLarge && !invalid
            ? StrictUtf8.GetString(captured!.ToArray())
            : null;
        return new TextReadResult(
            invalid, containsNul, captureTooLarge, runes, rawBytes, digest, text,
            maxRunes is { } maximum && runes > maximum,
            contentSha256);
    }

    private static void DecodeChunk(
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        char[] chars,
        bool flush,
        ref long runes,
        ref bool containsNul,
        ref bool pendingHighSurrogate)
    {
        var offset = 0;
        do
        {
            decoder.Convert(
                bytes[offset..], chars, flush,
                out var bytesUsed, out var charsUsed, out var completed);
            offset += bytesUsed;
            for (var index = 0; index < charsUsed; index++)
            {
                var value = chars[index];
                if (value == '\0') containsNul = true;
                if (pendingHighSurrogate)
                {
                    if (!char.IsLowSurrogate(value))
                        throw new DecoderFallbackException("Invalid UTF-16 sequence from strict UTF-8 decoder.");
                    pendingHighSurrogate = false;
                    runes++;
                }
                else if (char.IsHighSurrogate(value))
                {
                    pendingHighSurrogate = true;
                }
                else if (char.IsLowSurrogate(value))
                {
                    throw new DecoderFallbackException("Invalid UTF-16 sequence from strict UTF-8 decoder.");
                }
                else
                {
                    runes++;
                }
            }
            if (completed) break;
            if (bytesUsed == 0 && charsUsed == 0)
                throw new DecoderFallbackException("The strict UTF-8 decoder made no progress.");
        } while (offset < bytes.Length || flush);
        if (flush && pendingHighSurrogate)
            throw new DecoderFallbackException("Incomplete Unicode scalar.");
    }

    private static Guid ParseUuid(
        string? text,
        string table,
        string column,
        long rowOrdinal,
        string digest)
    {
        if (text is null
            || text.Length != 36
            || text[8] != '-' || text[13] != '-' || text[18] != '-' || text[23] != '-'
            || !Guid.TryParseExact(text, "D", out var value))
        {
            throw Failure("uuid-format", table, column, rowOrdinal, digest);
        }
        return value;
    }

    private static byte[] GuidNetworkBytes(Guid value)
    {
        var bytes = new byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
            throw new InvalidOperationException("Could not encode canonical UUID network bytes.");
        return bytes;
    }

    private static string BuildOrder(IReadOnlyList<TransferV3KeyComponentContract> keyset, bool sqlite) =>
        string.Join(", ", keyset.Select(component =>
            $"{QuoteIdentifier(component.Column)}"
            + ((sqlite ? component.SqliteCollation : component.PostgreSqlCollation) is { } collation
               && collation != "none"
                ? $" COLLATE {QuoteIdentifier(collation)}"
                : "")));

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static TransferV3SourceValidationException Failure(
        string code, string table, string column, long row, string digest) =>
        TransferV3SourceValidationException.Create(code, table, column, row, digest);

    private static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));
    private static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant()[..12];

    private sealed record TableScanMetrics(long RowCount, int MaxRowsPerBatch, long MaxBytesPerBatch);
    private sealed record ValidatedRow(
        long RowId,
        IReadOnlyDictionary<string, ValidatedCell> Cells,
        long RawBytes);
    private sealed record ValidatedCell(
        bool IsNull,
        long? Integer,
        string? Text,
        Guid? Uuid,
        byte[]? UuidNetwork,
        long RawBytes,
        string DigestPrefix,
        byte[]? ContentSha256)
    {
        internal static readonly ValidatedCell Null =
            new(true, null, null, null, null, 0, Digest("null"), null);
    }
    private sealed record TextReadResult(
        bool InvalidUtf8,
        bool ContainsNul,
        bool CaptureTooLarge,
        long RuneCount,
        long RawBytes,
        string DigestPrefix,
        string? Text,
        bool ExceedsMaxRunes,
        byte[] ContentSha256);
}

internal sealed record TransferV3RawScanResult(
    IReadOnlyList<TransferV3ValidatedTable> Tables,
    int MaxRowsPerBatch,
    long MaxBytesPerBatch,
    int MaxIoBufferBytes);

using System.Buffers;
using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Npgsql;
using NzbWebDAV.Database.Transfer;

namespace NzbWebDAV.Database;

internal readonly record struct PostgreSqlApplicationRelationCount(
    string RelationName,
    long Count);

internal sealed class PostgreSqlFreshBootstrapSnapshot : IDisposable
{
    private byte[] _canonicalDavItemsUtf8;
    private byte[] _canonicalConfigItemsUtf8;

    internal PostgreSqlFreshBootstrapSnapshot(
        ReadOnlyMemory<byte> canonicalDavItemsUtf8,
        ReadOnlyMemory<byte> canonicalConfigItemsUtf8,
        IReadOnlyList<PostgreSqlApplicationRelationCount> otherRelationCounts)
    {
        ArgumentNullException.ThrowIfNull(otherRelationCounts);
        _canonicalDavItemsUtf8 = canonicalDavItemsUtf8.ToArray();
        _canonicalConfigItemsUtf8 = canonicalConfigItemsUtf8.ToArray();
        OtherRelationCounts = Array.AsReadOnly(otherRelationCounts.ToArray());
    }

    internal ReadOnlyMemory<byte> CanonicalDavItemsUtf8 => _canonicalDavItemsUtf8;

    internal ReadOnlyMemory<byte> CanonicalConfigItemsUtf8 => _canonicalConfigItemsUtf8;

    internal IReadOnlyList<PostgreSqlApplicationRelationCount> OtherRelationCounts { get; }

    public void Dispose()
    {
        var config = Interlocked.Exchange(
            ref _canonicalConfigItemsUtf8,
            Array.Empty<byte>());
        CryptographicOperations.ZeroMemory(config);
        _canonicalDavItemsUtf8 = Array.Empty<byte>();
    }
}

internal static class PostgreSqlFreshBootstrapContract
{
    private const int ExpectedDavRowCount = 5;
    private const int ExpectedConfigRowCount = 3;
    private const int SecretLength = 32;
    private const int MaximumCreatedAtUtf8Bytes = 26;
    private const int MaximumIdPrefixUtf8Bytes = 5;
    private const int MaximumRootNameUtf8Bytes = 18;
    private const int MaximumRootPathUtf8Bytes = 19;
    private const int MaximumConfigNameUtf8Bytes = 21;
    private const int MaximumConfigValueUtf8Bytes = 35;
    private const int MaximumJsonEscapeBytesPerInputByte = 6;
    private const int ConfigJsonStructuralReserveUtf8Bytes = 256;
    private const int MaximumConfigDocumentUtf8Bytes =
        ConfigJsonStructuralReserveUtf8Bytes
        + ExpectedConfigRowCount
        * (MaximumConfigNameUtf8Bytes + MaximumConfigValueUtf8Bytes)
        * MaximumJsonEscapeBytesPerInputByte;
    private const int SensitiveConfigBufferCapacity = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonWriterOptions CanonicalWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };
    private static readonly Lazy<FreshContractEvidence> ContractEvidence =
        new(CreateEvidence, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void Validate(PostgreSqlFreshBootstrapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evidence = ContractEvidence.Value;
        var dav = snapshot.CanonicalDavItemsUtf8.Span;
        if (dav.Length != evidence.CanonicalDavItemsUtf8.Length
            || !CryptographicOperations.FixedTimeEquals(
                dav,
                evidence.CanonicalDavItemsUtf8))
        {
            throw BootstrapFailure();
        }

        ValidateCanonicalConfig(snapshot.CanonicalConfigItemsUtf8.Span, evidence);
        ValidateOtherRelationCounts(snapshot.OtherRelationCounts, evidence);
    }

    internal static async Task<PostgreSqlFreshBootstrapSnapshot> CaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateTransactionContext(connection, transaction, commandTimeoutSeconds);
        byte[]? dav = null;
        byte[]? config = null;
        try
        {
            var evidence = ContractEvidence.Value;
            dav = await CaptureDavItemsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken);
            config = await CaptureConfigItemsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken);
            var counts = await CaptureOtherRelationCountsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                evidence,
                cancellationToken);
            return new PostgreSqlFreshBootstrapSnapshot(dav, config, counts);
        }
        finally
        {
            if (dav is not null) CryptographicOperations.ZeroMemory(dav);
            if (config is not null) CryptographicOperations.ZeroMemory(config);
        }
    }

    internal static async Task ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var snapshot = await CaptureAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            cancellationToken);
        Validate(snapshot);
    }

    private static FreshContractEvidence CreateEvidence()
    {
        var bootstrap = TransferV3SourceContract.LoadEmbedded().Bootstrap;
        var target = TransferV3PostgreSqlTargetContract.LoadEmbedded();
        var davTable = target.Tables.Single(table =>
            string.Equals(table.Name, "DavItems", StringComparison.Ordinal));
        var expectedDavColumns = new[]
        {
            "Id",
            "CreatedAt",
            "FileBlobId",
            "FileSize",
            "HistoryItemId",
            "IdPrefix",
            "LastHealthCheck",
            "Name",
            "NextHealthCheck",
            "ParentId",
            "Path",
            "ReleaseDate",
            "SubType",
            "Type",
            "NzbBlobId"
        };
        if (bootstrap.Roots.Count != ExpectedDavRowCount
            || !davTable.PreserveBootstrapRoots
            || !davTable.Columns.Select(column => column.Name)
                .SequenceEqual(expectedDavColumns, StringComparer.Ordinal)
            || bootstrap.Config.Count != 2
            || !string.Equals(bootstrap.Config[0].Name, "api.key", StringComparison.Ordinal)
            || !string.Equals(bootstrap.Config[1].Name, "api.strm-key", StringComparison.Ordinal)
            || bootstrap.Config.Any(config =>
                !string.Equals(config.Pattern, "^[0-9a-f]{32}$", StringComparison.Ordinal)
                || !config.DistinctFromOtherSecrets))
        {
            throw BootstrapFailure();
        }

        var otherTables = target.Tables
            .Where(table => table.Name is not ("ConfigItems" or "DavItems"))
            .Concat([target.DerivedHealthCheckStats])
            .ToArray();
        if (otherTables.Length != 26
            || !string.Equals(
                otherTables[^1].Name,
                "HealthCheckStats",
                StringComparison.Ordinal))
        {
            throw BootstrapFailure();
        }

        var canonicalDavItemsUtf8 = BuildExpectedDavItems(bootstrap.Roots);
        Span<byte> firstPlaceholder = stackalloc byte[SecretLength];
        Span<byte> secondPlaceholder = stackalloc byte[SecretLength];
        firstPlaceholder.Fill((byte)'0');
        secondPlaceholder.Fill((byte)'1');
        var placeholderConfig = BuildCanonicalConfig(
            firstPlaceholder,
            secondPlaceholder,
            bootstrap.Config[0].Name,
            bootstrap.Config[1].Name);
        var canonicalConfigLength = placeholderConfig.Length;
        CryptographicOperations.ZeroMemory(firstPlaceholder);
        CryptographicOperations.ZeroMemory(secondPlaceholder);
        CryptographicOperations.ZeroMemory(placeholderConfig);

        return new FreshContractEvidence(
            canonicalDavItemsUtf8,
            canonicalConfigLength,
            bootstrap.Config[0].Name,
            bootstrap.Config[1].Name,
            target,
            Array.AsReadOnly(otherTables));
    }

    private static byte[] BuildExpectedDavItems(
        IReadOnlyList<TransferV3BootstrapRootContract> roots)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalWriterOptions))
        {
            writer.WriteStartArray();
            foreach (var root in roots)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", root.Id);
                writer.WriteString("CreatedAt", root.CreatedAt + ".000000");
                writer.WriteNull("FileBlobId");
                writer.WriteNull("FileSize");
                writer.WriteNull("HistoryItemId");
                writer.WriteString("IdPrefix", root.IdPrefix);
                writer.WriteNull("LastHealthCheck");
                writer.WriteString("Name", root.Name);
                writer.WriteNull("NextHealthCheck");
                if (root.ParentId is null)
                    writer.WriteNull("ParentId");
                else
                    writer.WriteString("ParentId", root.ParentId);
                writer.WriteString("Path", root.Path);
                writer.WriteNull("ReleaseDate");
                writer.WriteNumber("SubType", root.SubType);
                writer.WriteNumber("Type", root.Type);
                writer.WriteNull("NzbBlobId");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static void ValidateCanonicalConfig(
        ReadOnlySpan<byte> utf8,
        FreshContractEvidence evidence)
    {
        if (utf8.Length != evidence.CanonicalConfigUtf8Length)
            throw BootstrapFailure();

        Span<byte> apiKey = stackalloc byte[SecretLength];
        Span<byte> strmKey = stackalloc byte[SecretLength];
        byte[]? canonical = null;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            RequireToken(ref reader, JsonTokenType.StartArray);
            ReadConfigSecret(ref reader, evidence.ApiKeyName, apiKey);
            ReadConfigSecret(ref reader, evidence.StrmKeyName, strmKey);
            if (CryptographicOperations.FixedTimeEquals(apiKey, strmKey))
                throw BootstrapFailure();

            RequireToken(ref reader, JsonTokenType.StartObject);
            RequireProperty(ref reader, "ConfigName");
            RequireUnescapedString(ref reader, TransferV3ReservedConfigPolicy.ImportStateKey);
            RequireProperty(ref reader, "ConfigValue");
            RequireString(ref reader, TransferV3ImportStateCodec.FreshCanonicalJson);
            RequireToken(ref reader, JsonTokenType.EndObject);
            RequireToken(ref reader, JsonTokenType.EndArray);
            if (reader.Read()) throw BootstrapFailure();

            canonical = BuildCanonicalConfig(
                apiKey,
                strmKey,
                evidence.ApiKeyName,
                evidence.StrmKeyName);
            if (!CryptographicOperations.FixedTimeEquals(utf8, canonical))
                throw BootstrapFailure();
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException)
        {
            throw BootstrapFailure();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(apiKey);
            CryptographicOperations.ZeroMemory(strmKey);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static void ReadConfigSecret(
        ref Utf8JsonReader reader,
        string expectedName,
        scoped Span<byte> destination)
    {
        RequireToken(ref reader, JsonTokenType.StartObject);
        RequireProperty(ref reader, "ConfigName");
        RequireUnescapedString(ref reader, expectedName);
        RequireProperty(ref reader, "ConfigValue");
        if (!reader.Read()
            || reader.TokenType != JsonTokenType.String
            || reader.ValueIsEscaped
            || reader.ValueSpan.Length != SecretLength
            || !IsLowerHex(reader.ValueSpan))
        {
            throw BootstrapFailure();
        }

        reader.ValueSpan.CopyTo(destination);
        RequireToken(ref reader, JsonTokenType.EndObject);
    }

    private static bool IsLowerHex(ReadOnlySpan<byte> value)
    {
        if (value.Length != SecretLength) return false;
        foreach (var item in value)
        {
            if (item is not (>= (byte)'0' and <= (byte)'9')
                and not (>= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireToken(
        ref Utf8JsonReader reader,
        JsonTokenType tokenType)
    {
        if (!reader.Read() || reader.TokenType != tokenType)
            throw BootstrapFailure();
    }

    private static void RequireProperty(
        ref Utf8JsonReader reader,
        string propertyName)
    {
        if (!reader.Read()
            || reader.TokenType != JsonTokenType.PropertyName
            || reader.ValueIsEscaped
            || !reader.ValueTextEquals(propertyName))
        {
            throw BootstrapFailure();
        }
    }

    private static void RequireUnescapedString(
        ref Utf8JsonReader reader,
        string expectedValue)
    {
        if (!reader.Read()
            || reader.TokenType != JsonTokenType.String
            || reader.ValueIsEscaped
            || !reader.ValueTextEquals(expectedValue))
        {
            throw BootstrapFailure();
        }
    }

    private static void RequireString(
        ref Utf8JsonReader reader,
        string expectedValue)
    {
        if (!reader.Read()
            || reader.TokenType != JsonTokenType.String
            || !reader.ValueTextEquals(expectedValue))
        {
            throw BootstrapFailure();
        }
    }

    private static byte[] BuildCanonicalConfig(
        ReadOnlySpan<byte> apiKey,
        ReadOnlySpan<byte> strmKey,
        string apiKeyName,
        string strmKeyName)
    {
        using var buffer = new SensitiveConfigBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer, CanonicalWriterOptions))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("ConfigName", apiKeyName);
            writer.WriteString("ConfigValue"u8, apiKey);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("ConfigName", strmKeyName);
            writer.WriteString("ConfigValue"u8, strmKey);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString(
                "ConfigName",
                TransferV3ReservedConfigPolicy.ImportStateKey);
            writer.WriteString(
                "ConfigValue",
                TransferV3ImportStateCodec.FreshCanonicalJson);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return buffer.ToArray();
    }

    private static void ValidateOtherRelationCounts(
        IReadOnlyList<PostgreSqlApplicationRelationCount> captured,
        FreshContractEvidence evidence)
    {
        if (captured is null || captured.Count != evidence.OtherTables.Count)
            throw BootstrapFailure();
        for (var index = 0; index < captured.Count; index++)
        {
            if (!string.Equals(
                    captured[index].RelationName,
                    evidence.OtherTables[index].Name,
                    StringComparison.Ordinal)
                || captured[index].Count != 0)
            {
                throw BootstrapFailure();
            }
        }
    }

    private static async Task<byte[]> CaptureDavItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        byte[]? result = null;
        using var stream = new MemoryStream();
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText =
                $"""
                SELECT
                    "Id",
                    CASE WHEN isfinite("CreatedAt")
                        THEN "CreatedAt" >= TIMESTAMP '0001-01-01 00:00:00'
                        ELSE false END,
                    octet_length(CASE WHEN isfinite("CreatedAt")
                        THEN to_char("CreatedAt", 'YYYY-MM-DD HH24:MI:SS.US')
                        ELSE "CreatedAt"::text END),
                    CASE WHEN isfinite("CreatedAt")
                              AND octet_length(to_char(
                                  "CreatedAt", 'YYYY-MM-DD HH24:MI:SS.US'))
                                  <= {MaximumCreatedAtUtf8Bytes}
                        THEN convert_to(
                            to_char("CreatedAt", 'YYYY-MM-DD HH24:MI:SS.US'),
                            'UTF8')
                        ELSE ''::bytea END,
                    "FileBlobId", "FileSize", "HistoryItemId",
                    octet_length("IdPrefix"),
                    CASE WHEN octet_length("IdPrefix") <= {MaximumIdPrefixUtf8Bytes}
                        THEN convert_to("IdPrefix", 'UTF8')
                        ELSE ''::bytea END,
                    "LastHealthCheck",
                    octet_length("Name"),
                    CASE WHEN octet_length("Name") <= {MaximumRootNameUtf8Bytes}
                        THEN convert_to("Name", 'UTF8')
                        ELSE ''::bytea END,
                    "NextHealthCheck", "ParentId",
                    octet_length("Path"),
                    CASE WHEN octet_length("Path") <= {MaximumRootPathUtf8Bytes}
                        THEN convert_to("Path", 'UTF8')
                        ELSE ''::bytea END,
                    "ReleaseDate", "SubType", "Type", "NzbBlobId"
                FROM "DavItems"
                ORDER BY "Id"
                LIMIT 6
                """;
            reader = await command.ExecuteReaderAsync(cancellationToken);
            using (var writer = new Utf8JsonWriter(stream, CanonicalWriterOptions))
            {
                writer.WriteStartArray();
                var rowCount = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rowCount == ExpectedDavRowCount) throw BootstrapFailure();
                    WriteDavRow(writer, reader);
                    rowCount++;
                }

                writer.WriteEndArray();
            }

            result = stream.ToArray();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            ZeroMemoryStream(stream);
        }

        try
        {
            await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                    reader,
                    command,
                    primaryFailure)
                .ConfigureAwait(false);
        }
        catch
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
            throw;
        }

        return result!;
    }

    private static void WriteDavRow(Utf8JsonWriter writer, NpgsqlDataReader reader)
    {
        if (!reader.GetBoolean(1)) throw BootstrapFailure();

        var createdAt = ReadBoundedUtf8(
            reader,
            2,
            3,
            MaximumCreatedAtUtf8Bytes);
        var idPrefix = ReadBoundedUtf8(
            reader,
            7,
            8,
            MaximumIdPrefixUtf8Bytes);
        var name = ReadBoundedUtf8(
            reader,
            10,
            11,
            MaximumRootNameUtf8Bytes);
        var path = ReadBoundedUtf8(
            reader,
            14,
            15,
            MaximumRootPathUtf8Bytes);
        try
        {
            writer.WriteStartObject();
            writer.WriteString("Id", reader.GetGuid(0));
            writer.WriteString("CreatedAt"u8, createdAt);
            WriteNullableGuid(writer, "FileBlobId", reader, 4);
            WriteNullableInt64(writer, "FileSize", reader, 5);
            WriteNullableGuid(writer, "HistoryItemId", reader, 6);
            writer.WriteString("IdPrefix"u8, idPrefix);
            WriteNullableInt64(writer, "LastHealthCheck", reader, 9);
            writer.WriteString("Name"u8, name);
            WriteNullableInt64(writer, "NextHealthCheck", reader, 12);
            WriteNullableGuid(writer, "ParentId", reader, 13);
            writer.WriteString("Path"u8, path);
            WriteNullableInt64(writer, "ReleaseDate", reader, 16);
            writer.WriteNumber("SubType", reader.GetInt32(17));
            writer.WriteNumber("Type", reader.GetInt32(18));
            WriteNullableGuid(writer, "NzbBlobId", reader, 19);
            writer.WriteEndObject();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(createdAt);
            CryptographicOperations.ZeroMemory(idPrefix);
            CryptographicOperations.ZeroMemory(name);
            CryptographicOperations.ZeroMemory(path);
        }
    }

    private static void WriteNullableGuid(
        Utf8JsonWriter writer,
        string propertyName,
        NpgsqlDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal)) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, reader.GetGuid(ordinal));
    }

    private static void WriteNullableInt64(
        Utf8JsonWriter writer,
        string propertyName,
        NpgsqlDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal)) writer.WriteNull(propertyName);
        else writer.WriteNumber(propertyName, reader.GetInt64(ordinal));
    }

    private static async Task<byte[]> CaptureConfigItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        byte[]? result = null;
        using var buffer = new SensitiveConfigBufferWriter();
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText =
                $"""
                SELECT
                    octet_length("ConfigName"),
                    CASE WHEN octet_length("ConfigName") <= {MaximumConfigNameUtf8Bytes}
                        THEN convert_to("ConfigName", 'UTF8')
                        ELSE ''::bytea END,
                    octet_length("ConfigValue"),
                    CASE WHEN octet_length("ConfigValue") <= {MaximumConfigValueUtf8Bytes}
                        THEN convert_to("ConfigValue", 'UTF8')
                        ELSE ''::bytea END
                FROM "ConfigItems"
                ORDER BY "ConfigName" COLLATE "C"
                LIMIT 4
                """;
            reader = await command.ExecuteReaderAsync(cancellationToken);
            using (var writer = new Utf8JsonWriter(buffer, CanonicalWriterOptions))
            {
                writer.WriteStartArray();
                var rowCount = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rowCount == ExpectedConfigRowCount) throw BootstrapFailure();
                    var name = ReadBoundedUtf8(
                        reader,
                        0,
                        1,
                        MaximumConfigNameUtf8Bytes);
                    var value = ReadBoundedUtf8(
                        reader,
                        2,
                        3,
                        MaximumConfigValueUtf8Bytes);
                    try
                    {
                        writer.WriteStartObject();
                        writer.WriteString("ConfigName"u8, name);
                        writer.WriteString("ConfigValue"u8, value);
                        writer.WriteEndObject();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(name);
                        CryptographicOperations.ZeroMemory(value);
                    }

                    rowCount++;
                }

                writer.WriteEndArray();
            }

            result = buffer.ToArray();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        try
        {
            await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                    reader,
                    command,
                    primaryFailure)
                .ConfigureAwait(false);
        }
        catch
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
            throw;
        }

        return result!;
    }

    private static byte[] ReadBoundedUtf8(
        NpgsqlDataReader reader,
        int lengthOrdinal,
        int bytesOrdinal,
        int maximumBytes)
    {
        var length = reader.GetInt32(lengthOrdinal);
        if (length < 0 || length > maximumBytes) throw BootstrapFailure();
        var bytes = reader.GetFieldValue<byte[]>(bytesOrdinal);
        try
        {
            if (bytes.Length != length) throw BootstrapFailure();
            _ = StrictUtf8.GetCharCount(bytes);
            return bytes;
        }
        catch (DecoderFallbackException)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw BootstrapFailure();
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static async Task<IReadOnlyList<PostgreSqlApplicationRelationCount>>
        CaptureOtherRelationCountsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int commandTimeoutSeconds,
            FreshContractEvidence evidence,
            CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        IReadOnlyList<PostgreSqlApplicationRelationCount>? result = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = "SELECT\n    " + string.Join(
                ",\n    ",
                evidence.OtherTables.Select(table =>
                    $"(SELECT count(*) FROM {evidence.Target.GetQuotedTableName(table)})"));
            reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.FieldCount != evidence.OtherTables.Count)
            {
                throw BootstrapFailure();
            }

            var counts = new PostgreSqlApplicationRelationCount[evidence.OtherTables.Count];
            for (var index = 0; index < counts.Length; index++)
            {
                counts[index] = new PostgreSqlApplicationRelationCount(
                    evidence.OtherTables[index].Name,
                    reader.GetInt64(index));
            }

            if (await reader.ReadAsync(cancellationToken)) throw BootstrapFailure();
            result = Array.AsReadOnly(counts);
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
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "PostgreSQL fresh-bootstrap capture requires an open connection.");
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
            "PostgreSQL fresh-bootstrap capture requires an active transaction owned by the supplied connection.",
            inner);

    private static void ZeroMemoryStream(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer) && buffer.Array is not null)
            CryptographicOperations.ZeroMemory(buffer.Array);
    }

    private static InvalidOperationException BootstrapFailure() =>
        new("PostgreSQL fresh-bootstrap validation failed.");

    private sealed class SensitiveConfigBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = new byte[SensitiveConfigBufferCapacity];
        private int _writtenCount;

        internal byte[] ToArray()
        {
            if (_writtenCount > MaximumConfigDocumentUtf8Bytes)
                throw BootstrapFailure();
            return _buffer.AsSpan(0, _writtenCount).ToArray();
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _writtenCount)
                throw BootstrapFailure();
            _writtenCount += count;
            if (_writtenCount > MaximumConfigDocumentUtf8Bytes)
                throw BootstrapFailure();
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return _buffer.AsMemory(_writtenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return _buffer.AsSpan(_writtenCount);
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
            CryptographicOperations.ZeroMemory(buffer);
            _writtenCount = 0;
        }

        private void ValidateSizeHint(int sizeHint)
        {
            if (sizeHint < 0 || sizeHint > _buffer.Length - _writtenCount)
                throw BootstrapFailure();
        }
    }

    private sealed record FreshContractEvidence(
        byte[] CanonicalDavItemsUtf8,
        int CanonicalConfigUtf8Length,
        string ApiKeyName,
        string StrmKeyName,
        TransferV3PostgreSqlTargetContract Target,
        ReadOnlyCollection<TransferV3PostgreSqlTableContract> OtherTables);
}

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NzbWebDAV.Database.Transfer;

internal readonly record struct TransferV3FrameSerializationMetrics(
    int Base64Utf16Bytes,
    int ArrayBufferWriterInitialCapacityBytes,
    int ArrayBufferWriterCapacityBytes,
    int SerializedBytes)
{
    internal int MaxManagedBufferBytesObserved => Math.Max(
        Base64Utf16Bytes,
        Math.Max(ArrayBufferWriterCapacityBytes, SerializedBytes));
}

internal readonly record struct TransferV3FrameSerializationResult(
    byte[] Bytes,
    TransferV3FrameSerializationMetrics Metrics);

internal static class TransferV3FrameCodec
{
    internal const int FormatVersion = 3;
    internal const int MaxTableNameLength = 128;

    internal static byte[] Serialize(TransferV3Frame frame) => SerializeMeasured(frame).Bytes;

    internal static TransferV3FrameSerializationResult SerializeMeasured(TransferV3Frame frame)
    {
        var initialCapacity = GetSerializationCapacityUpperBound(frame);
        var buffer = new ArrayBufferWriter<byte>(initialCapacity);
        var base64Utf16Bytes = 0;
        try
        {
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });

            writer.WriteStartObject();
            switch (frame)
            {
                case TransferV3TableHeaderFrame header:
                    writer.WriteString("frame", "table");
                    writer.WriteNumber("version", header.Version);
                    writer.WriteString("table", header.Table);
                    break;
                case TransferV3BatchStartFrame batchStart:
                    writer.WriteString("frame", "batch-start");
                    writer.WriteString("table", batchStart.Table);
                    writer.WriteNumber("batch", batchStart.Batch);
                    if (batchStart.After is null)
                    {
                        writer.WriteNull("after");
                    }
                    else
                    {
                        writer.WriteString("after", batchStart.After);
                    }

                    break;
                case TransferV3RowFrame row:
                    writer.WriteString("frame", "row");
                    writer.WriteString("table", row.Table);
                    writer.WriteString("cursor", row.Cursor);
                    base64Utf16Bytes = PaddedBase64Utf16Bytes(row.Data.Length);
                    writer.WriteString("data", TransferV3CursorCodec.EncodeBase64Url(row.Data.Span));
                    break;
                case TransferV3ChunkedRowStartFrame rowStart:
                    writer.WriteString("frame", "row-start");
                    writer.WriteString("table", rowStart.Table);
                    writer.WriteString("cursor", rowStart.Cursor);
                    writer.WriteNumber("fields", rowStart.Fields);
                    break;
                case TransferV3FieldChunkFrame chunk:
                    writer.WriteString("frame", "field-chunk");
                    writer.WriteString("table", chunk.Table);
                    writer.WriteString("cursor", chunk.Cursor);
                    writer.WriteNumber("field", chunk.Field);
                    writer.WriteNumber("chunk", chunk.Chunk);
                    base64Utf16Bytes = PaddedBase64Utf16Bytes(chunk.Data.Length);
                    writer.WriteString("data", TransferV3CursorCodec.EncodeBase64Url(chunk.Data.Span));
                    break;
                case TransferV3ChunkedRowEndFrame rowEnd:
                    writer.WriteString("frame", "row-end");
                    writer.WriteString("table", rowEnd.Table);
                    writer.WriteString("cursor", rowEnd.Cursor);
                    writer.WriteNumber("fields", rowEnd.Fields);
                    writer.WriteNumber("bytes", rowEnd.Bytes);
                    writer.WriteString("sha256", rowEnd.Sha256);
                    break;
                case TransferV3BatchEndFrame batchEnd:
                    writer.WriteString("frame", "batch-end");
                    writer.WriteString("table", batchEnd.Table);
                    writer.WriteNumber("batch", batchEnd.Batch);
                    writer.WriteNumber("rows", batchEnd.Rows);
                    writer.WriteNumber("bytes", batchEnd.Bytes);
                    writer.WriteString("cursor", batchEnd.Cursor);
                    writer.WriteString("sha256", batchEnd.Sha256);
                    break;
                case TransferV3TableEndFrame tableEnd:
                    writer.WriteString("frame", "table-end");
                    writer.WriteString("table", tableEnd.Table);
                    writer.WriteNumber("batches", tableEnd.Batches);
                    writer.WriteNumber("rows", tableEnd.Rows);
                    writer.WriteNumber("bytes", tableEnd.Bytes);
                    writer.WriteString("sha256", tableEnd.Sha256);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(frame));
            }

            writer.WriteEndObject();
            writer.Flush();
            var bytes = buffer.WrittenSpan.ToArray();
            return new TransferV3FrameSerializationResult(
                bytes,
                new TransferV3FrameSerializationMetrics(
                    base64Utf16Bytes,
                    initialCapacity,
                    buffer.Capacity,
                    bytes.Length));
        }
        finally
        {
            if (MemoryMarshal.TryGetArray(buffer.WrittenMemory, out var segment))
                CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }

    private static int PaddedBase64Utf16Bytes(int decodedBytes) =>
        checked(((decodedBytes + 2) / 3) * 4 * sizeof(char));

    private static int GetSerializationCapacityUpperBound(TransferV3Frame frame)
    {
        // Utf8JsonWriter's default encoder can emit a six-byte JSON escape for
        // each UTF-16 metadata code unit. Its string-writing path requests the
        // UTF-8 worst case (three bytes per UTF-16 code unit) even though payload
        // Base64URL is ASCII and the final representation uses one byte per char.
        const int maxJsonBytesPerMetadataCharacter = 6;
        const int maxRequestedBytesPerPayloadCharacter = 3;
        const int fixedJsonAndNumericBytes = 1024;

        var metadataCharacters = frame.Table?.Length ?? 0;
        var payloadCharacters = 0;
        switch (frame)
        {
            case TransferV3BatchStartFrame batchStart:
                metadataCharacters = checked(
                    metadataCharacters + (batchStart.After?.Length ?? 0));
                break;
            case TransferV3RowFrame row:
                metadataCharacters = checked(
                    metadataCharacters + (row.Cursor?.Length ?? 0));
                payloadCharacters = TransferV3Limits.Base64UrlEncodedLength(row.Data.Length);
                break;
            case TransferV3ChunkedRowStartFrame rowStart:
                metadataCharacters = checked(
                    metadataCharacters + (rowStart.Cursor?.Length ?? 0));
                break;
            case TransferV3FieldChunkFrame chunk:
                metadataCharacters = checked(
                    metadataCharacters + (chunk.Cursor?.Length ?? 0));
                payloadCharacters = TransferV3Limits.Base64UrlEncodedLength(chunk.Data.Length);
                break;
            case TransferV3ChunkedRowEndFrame rowEnd:
                metadataCharacters = checked(
                    metadataCharacters
                    + (rowEnd.Cursor?.Length ?? 0)
                    + (rowEnd.Sha256?.Length ?? 0));
                break;
            case TransferV3BatchEndFrame batchEnd:
                metadataCharacters = checked(
                    metadataCharacters
                    + (batchEnd.Cursor?.Length ?? 0)
                    + (batchEnd.Sha256?.Length ?? 0));
                break;
            case TransferV3TableEndFrame tableEnd:
                metadataCharacters = checked(
                    metadataCharacters + (tableEnd.Sha256?.Length ?? 0));
                break;
        }

        return checked(
            fixedJsonAndNumericBytes
            + checked(metadataCharacters * maxJsonBytesPerMetadataCharacter)
            + checked(payloadCharacters * maxRequestedBytesPerPayloadCharacter));
    }

    internal static TransferV3Frame ParseCanonical(ReadOnlyMemory<byte> line)
    {
        TransferV3Frame? frame = null;
        var accepted = false;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidFrame("A frame must be a JSON object.");
            }

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length == 0
                || properties[0].Name != "frame"
                || properties[0].Value.ValueKind != JsonValueKind.String)
            {
                throw InvalidFrame("The frame discriminator must be first.");
            }

            frame = properties[0].Value.GetString() switch
            {
                "table" => ParseTableHeader(properties),
                "batch-start" => ParseBatchStart(properties),
                "row" => ParseRow(properties),
                "row-start" => ParseRowStart(properties),
                "field-chunk" => ParseFieldChunk(properties),
                "row-end" => ParseRowEnd(properties),
                "batch-end" => ParseBatchEnd(properties),
                "table-end" => ParseTableEnd(properties),
                _ => throw InvalidFrame("The frame discriminator is unknown."),
            };

            var canonical = Serialize(frame);
            try
            {
                if (!line.Span.SequenceEqual(canonical))
                    throw InvalidFrame("The frame is not canonical compact JSON.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            accepted = true;
            return frame;
        }
        catch (JsonException exception)
        {
            throw InvalidFrame("The frame is not valid JSON.", exception);
        }
        finally
        {
            if (!accepted)
            {
                ClearDecodedPayload(frame);
            }
        }
    }

    internal static void ClearDecodedPayload(TransferV3Frame? frame)
    {
        switch (frame)
        {
            case TransferV3RowFrame row:
                ZeroMemory(row.Data);
                break;
            case TransferV3FieldChunkFrame chunk:
                ZeroMemory(chunk.Data);
                break;
        }
    }

    internal static void ValidateTableName(string table)
    {
        if (string.IsNullOrEmpty(table)
            || table.Length > MaxTableNameLength
            || !(table[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            || table.Skip(1).Any(character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_')))
        {
            throw new ArgumentException("The transfer table name is invalid.", nameof(table));
        }
    }

    internal static void ValidateDigest(string digest)
    {
        if (digest.Length != 64
            || digest.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw InvalidFrame("The SHA-256 digest is not canonical lowercase hexadecimal.");
        }
    }

    private static TransferV3TableHeaderFrame ParseTableHeader(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "version", "table");
        var version = GetInt32(properties[1], "version");
        var table = GetString(properties[2], "table");
        ValidateParsedTable(table);
        return new TransferV3TableHeaderFrame(version, table);
    }

    private static TransferV3BatchStartFrame ParseBatchStart(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "batch", "after");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var batch = GetNonnegativeInt32(properties[2], "batch");
        string? after = properties[3].Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => properties[3].Value.GetString(),
            _ => throw InvalidFrame("The after cursor must be a string or null."),
        };
        if (after is not null)
        {
            ValidateParsedCursor(after);
        }

        return new TransferV3BatchStartFrame(table, batch, after);
    }

    private static TransferV3RowFrame ParseRow(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "cursor", "data");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var cursor = GetString(properties[2], "cursor");
        ValidateParsedCursor(cursor);
        var data = DecodePayload(properties[3], "data");
        try
        {
            return new TransferV3RowFrame(table, cursor, data);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(data);
            throw;
        }
    }

    private static TransferV3ChunkedRowStartFrame ParseRowStart(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "cursor", "fields");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var cursor = GetString(properties[2], "cursor");
        ValidateParsedCursor(cursor);
        return new TransferV3ChunkedRowStartFrame(
            table,
            cursor,
            GetNonnegativeInt32(properties[3], "fields"));
    }

    private static TransferV3FieldChunkFrame ParseFieldChunk(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "cursor", "field", "chunk", "data");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var cursor = GetString(properties[2], "cursor");
        ValidateParsedCursor(cursor);
        var field = GetNonnegativeInt32(properties[3], "field");
        var chunk = GetNonnegativeInt32(properties[4], "chunk");
        var data = DecodePayload(properties[5], "data");
        try
        {
            return new TransferV3FieldChunkFrame(
                table,
                cursor,
                field,
                chunk,
                data);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(data);
            throw;
        }
    }

    private static TransferV3ChunkedRowEndFrame ParseRowEnd(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "cursor", "fields", "bytes", "sha256");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var cursor = GetString(properties[2], "cursor");
        ValidateParsedCursor(cursor);
        var digest = GetString(properties[5], "sha256");
        ValidateDigest(digest);
        return new TransferV3ChunkedRowEndFrame(
            table,
            cursor,
            GetNonnegativeInt32(properties[3], "fields"),
            GetNonnegativeInt64(properties[4], "bytes"),
            digest);
    }

    private static TransferV3BatchEndFrame ParseBatchEnd(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "batch", "rows", "bytes", "cursor", "sha256");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var cursor = GetString(properties[5], "cursor");
        ValidateParsedCursor(cursor);
        var digest = GetString(properties[6], "sha256");
        ValidateDigest(digest);
        return new TransferV3BatchEndFrame(
            table,
            GetNonnegativeInt32(properties[2], "batch"),
            GetNonnegativeInt32(properties[3], "rows"),
            GetNonnegativeInt64(properties[4], "bytes"),
            cursor,
            digest);
    }

    private static TransferV3TableEndFrame ParseTableEnd(JsonProperty[] properties)
    {
        RequireNames(properties, "frame", "table", "batches", "rows", "bytes", "sha256");
        var table = GetString(properties[1], "table");
        ValidateParsedTable(table);
        var digest = GetString(properties[5], "sha256");
        ValidateDigest(digest);
        return new TransferV3TableEndFrame(
            table,
            GetNonnegativeInt32(properties[2], "batches"),
            GetNonnegativeInt64(properties[3], "rows"),
            GetNonnegativeInt64(properties[4], "bytes"),
            digest);
    }

    private static byte[] DecodePayload(JsonProperty property, string expectedName)
    {
        var value = GetString(property, expectedName);
        if (value.Length > TransferV3Limits.Base64UrlEncodedLength(
                TransferV3Limits.MaxDecodedChunkBytes))
        {
            throw InvalidFrame("The decoded frame payload would exceed one MiB.");
        }

        if (!IsCanonicalBase64Url(value))
        {
            throw InvalidFrame("The frame payload is not canonical Base64URL.");
        }

        byte[] decoded;
        try
        {
            decoded = TransferV3CursorCodec.DecodeBase64Url(value);
        }
        catch (FormatException exception)
        {
            throw InvalidFrame("The frame payload is not canonical Base64URL.", exception);
        }

        if (decoded.Length > TransferV3Limits.MaxDecodedChunkBytes)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw InvalidFrame("The decoded frame payload exceeds one MiB.");
        }

        return decoded;
    }

    private static bool IsCanonicalBase64Url(string value)
    {
        if (value.Length % 4 == 1)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (Base64UrlValue(character) < 0)
            {
                return false;
            }
        }

        return (value.Length % 4) switch
        {
            2 => (Base64UrlValue(value[^1]) & 0x0f) == 0,
            3 => (Base64UrlValue(value[^1]) & 0x03) == 0,
            _ => true,
        };
    }

    private static int Base64UrlValue(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= 'a' and <= 'z' => character - 'a' + 26,
        >= '0' and <= '9' => character - '0' + 52,
        '-' => 62,
        '_' => 63,
        _ => -1,
    };

    private static void ZeroMemory(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment)
            && segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }

    private static void RequireNames(JsonProperty[] properties, params string[] expected)
    {
        if (properties.Length != expected.Length)
        {
            throw InvalidFrame("The frame has missing, duplicate, or unknown properties.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(properties[index].Name, expected[index], StringComparison.Ordinal))
            {
                throw InvalidFrame("Frame properties are missing, unknown, or out of order.");
            }
        }
    }

    private static string GetString(JsonProperty property, string expectedName)
    {
        if (property.Name != expectedName || property.Value.ValueKind != JsonValueKind.String)
        {
            throw InvalidFrame($"The {expectedName} property must be a string.");
        }

        return property.Value.GetString()!;
    }

    private static int GetInt32(JsonProperty property, string expectedName)
    {
        if (property.Name != expectedName
            || property.Value.ValueKind != JsonValueKind.Number
            || !property.Value.TryGetInt32(out var value))
        {
            throw InvalidFrame($"The {expectedName} property must be an Int32.");
        }

        return value;
    }

    private static int GetNonnegativeInt32(JsonProperty property, string expectedName)
    {
        var value = GetInt32(property, expectedName);
        if (value < 0)
        {
            throw InvalidFrame($"The {expectedName} property may not be negative.");
        }

        return value;
    }

    private static long GetNonnegativeInt64(JsonProperty property, string expectedName)
    {
        if (property.Name != expectedName
            || property.Value.ValueKind != JsonValueKind.Number
            || !property.Value.TryGetInt64(out var value)
            || value < 0)
        {
            throw InvalidFrame($"The {expectedName} property must be a nonnegative Int64.");
        }

        return value;
    }

    private static void ValidateParsedTable(string table)
    {
        try
        {
            ValidateTableName(table);
        }
        catch (ArgumentException exception)
        {
            throw InvalidFrame("The frame table name is invalid.", exception);
        }
    }

    private static void ValidateParsedCursor(string cursor)
    {
        try
        {
            _ = TransferV3CursorCodec.Decode(cursor);
        }
        catch (FormatException exception)
        {
            throw InvalidFrame("The frame cursor is invalid.", exception);
        }
    }

    private static FormatException InvalidFrame(string message, Exception? inner = null) =>
        new(message, inner);
}

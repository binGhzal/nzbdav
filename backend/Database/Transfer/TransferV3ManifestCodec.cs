using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3ManifestCodec
{
    internal const int MaxManifestBytes = 256 * 1024;

    private static readonly string[] RootProperties =
    [
        "formatVersion",
        "sourceProvider",
        "sourceContractSha256",
        "sourceSchemaSha256",
        "migrationContractSha256",
        "sourceTimeZoneId",
        "limits",
        "tables",
        "derivedTables",
        "informationalReferences",
        "blobs",
    ];

    internal static byte[] Serialize(
        TransferV3Manifest manifest,
        TransferV3SourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(contract);
        try
        {
            Validate(manifest, contract);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                   }))
            {
                Write(writer, manifest);
            }

            if (output.Length > MaxManifestBytes)
            {
                throw Failure("manifest-size");
            }

            return output.ToArray();
        }
        catch (TransferV3ManifestFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or OverflowException
                                           or TimeZoneNotFoundException
                                           or InvalidTimeZoneException)
        {
            throw Failure("manifest-shape");
        }
    }

    internal static TransferV3Manifest Parse(
        ReadOnlyMemory<byte> bytes,
        TransferV3SourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (bytes.IsEmpty || bytes.Length > MaxManifestBytes)
        {
            throw Failure("manifest-size");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var manifest = ReadManifest(document.RootElement);
            Validate(manifest, contract);
            var canonical = Serialize(manifest, contract);
            if (!bytes.Span.SequenceEqual(canonical))
            {
                throw Failure("manifest-noncanonical");
            }

            return manifest;
        }
        catch (TransferV3ManifestFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                           or ArgumentException
                                           or InvalidOperationException
                                           or OverflowException
                                           or TimeZoneNotFoundException
                                           or InvalidTimeZoneException)
        {
            throw Failure("manifest-format");
        }
    }

    internal static string ComputeSha256(ReadOnlySpan<byte> canonicalManifest) =>
        Convert.ToHexString(SHA256.HashData(canonicalManifest)).ToLowerInvariant();

    private static TransferV3Manifest ReadManifest(JsonElement root)
    {
        var properties = RequireProperties(root, RootProperties);
        return new TransferV3Manifest(
            ReadInt32(properties[0].Value),
            ReadString(properties[1].Value),
            ReadString(properties[2].Value),
            ReadString(properties[3].Value),
            ReadString(properties[4].Value),
            ReadString(properties[5].Value),
            ReadLimits(properties[6].Value),
            ReadTables(properties[7].Value),
            ReadDerivedTables(properties[8].Value),
            ReadInformationalReferences(properties[9].Value),
            ReadBlobs(properties[10].Value));
    }

    private static TransferV3ManifestLimits ReadLimits(JsonElement element)
    {
        var properties = RequireProperties(
            element,
            ["maxFieldBytes", "maxBatchRows", "maxBatchBytes"]);
        return new TransferV3ManifestLimits(
            ReadInt64(properties[0].Value),
            ReadInt32(properties[1].Value),
            ReadInt64(properties[2].Value));
    }

    private static IReadOnlyList<TransferV3ManifestTable> ReadTables(JsonElement element)
    {
        RequireArray(element);
        var values = new List<TransferV3ManifestTable>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            var properties = RequireProperties(
                item,
                ["name", "file", "batches", "rows", "decodedBytes", "sha256"]);
            values.Add(new TransferV3ManifestTable(
                ReadString(properties[0].Value),
                ReadString(properties[1].Value),
                ReadInt32(properties[2].Value),
                ReadInt64(properties[3].Value),
                ReadInt64(properties[4].Value),
                ReadString(properties[5].Value)));
        }

        return values;
    }

    private static IReadOnlyList<TransferV3ManifestDerivedTable> ReadDerivedTables(
        JsonElement element)
    {
        RequireArray(element);
        var values = new List<TransferV3ManifestDerivedTable>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            var properties = RequireProperties(item, ["name", "rows", "logicalSha256"]);
            values.Add(new TransferV3ManifestDerivedTable(
                ReadString(properties[0].Value),
                ReadInt64(properties[1].Value),
                ReadString(properties[2].Value)));
        }

        return values;
    }

    private static IReadOnlyList<TransferV3ManifestInformationalReference>
        ReadInformationalReferences(JsonElement element)
    {
        RequireArray(element);
        var values = new List<TransferV3ManifestInformationalReference>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            var properties = RequireProperties(
                item,
                ["name", "unresolvedCount", "unresolvedSha256"]);
            values.Add(new TransferV3ManifestInformationalReference(
                ReadString(properties[0].Value),
                ReadInt64(properties[1].Value),
                ReadString(properties[2].Value)));
        }

        return values;
    }

    private static TransferV3ManifestBlobs ReadBlobs(JsonElement element)
    {
        var properties = RequireProperties(
            element,
            [
                "name",
                "file",
                "batches",
                "rows",
                "decodedBytes",
                "sha256",
                "count",
                "totalBytes",
                "inventorySha256",
            ]);
        return new TransferV3ManifestBlobs(
            ReadString(properties[0].Value),
            ReadString(properties[1].Value),
            ReadInt32(properties[2].Value),
            ReadInt64(properties[3].Value),
            ReadInt64(properties[4].Value),
            ReadString(properties[5].Value),
            ReadInt64(properties[6].Value),
            ReadInt64(properties[7].Value),
            ReadString(properties[8].Value));
    }

    private static JsonProperty[] RequireProperties(
        JsonElement element,
        IReadOnlyList<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Failure("manifest-shape");
        }

        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != expected.Count)
        {
            throw Failure("manifest-shape");
        }

        for (var index = 0; index < properties.Length; index++)
        {
            if (!string.Equals(properties[index].Name, expected[index], StringComparison.Ordinal))
            {
                throw Failure("manifest-shape");
            }
        }

        return properties;
    }

    private static void RequireArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Failure("manifest-shape");
        }
    }

    private static string ReadString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Failure("manifest-shape");
        }

        return element.GetString() ?? throw Failure("manifest-shape");
    }

    private static int ReadInt32(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw Failure("manifest-number");
        }

        return value;
    }

    private static long ReadInt64(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw Failure("manifest-number");
        }

        return value;
    }

    private static void Validate(
        TransferV3Manifest manifest,
        TransferV3SourceContract contract)
    {
        if (manifest.FormatVersion != 3
            || !string.Equals(manifest.SourceProvider, contract.Provider, StringComparison.Ordinal)
            || !string.Equals(
                manifest.SourceContractSha256,
                contract.ComputeSha256(),
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.SourceSchemaSha256,
                contract.SourceSchemaSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.MigrationContractSha256,
                contract.MigrationSourceContractSha256,
                StringComparison.Ordinal)
            || !IsCanonicalDigest(manifest.SourceContractSha256)
            || !IsCanonicalDigest(manifest.SourceSchemaSha256)
            || !IsCanonicalDigest(manifest.MigrationContractSha256))
        {
            throw Failure("manifest-contract");
        }

        ValidateTimeZone(manifest.SourceTimeZoneId);
        ValidateLimits(manifest.Limits);
        ValidateTables(
            manifest.Tables,
            contract,
            manifest.Limits.MaxBatchRows,
            manifest.Limits.MaxFieldBytes,
            manifest.Limits.MaxBatchBytes);
        ValidateDerivedTables(manifest.DerivedTables, contract);
        ValidateInformationalReferences(manifest.InformationalReferences, contract);
        ValidateBlobs(
            manifest.Blobs,
            manifest.Limits.MaxBatchRows,
            manifest.Limits.MaxFieldBytes,
            manifest.Limits.MaxBatchBytes);
    }

    private static void ValidateTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 255)
        {
            throw Failure("manifest-time-zone");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException
                                           or InvalidTimeZoneException
                                           or ArgumentException)
        {
            throw Failure("manifest-time-zone");
        }
    }

    private static void ValidateLimits(TransferV3ManifestLimits limits)
    {
        if (limits is null)
        {
            throw Failure("manifest-limits");
        }

        if (limits.MaxFieldBytes < 40)
        {
            throw Failure("manifest-limits");
        }

        try
        {
            _ = new TransferV3Limits(
                limits.MaxFieldBytes,
                limits.MaxBatchRows,
                limits.MaxBatchBytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure("manifest-limits");
        }
    }

    private static void ValidateTables(
        ImmutableArray<TransferV3ManifestTable> tables,
        TransferV3SourceContract contract,
        int maxBatchRows,
        long maxFieldBytes,
        long maxBatchBytes)
    {
        if (tables.IsDefault || tables.Length != contract.Tables.Count)
        {
            throw Failure("manifest-tables");
        }

        for (var index = 0; index < tables.Length; index++)
        {
            var value = tables[index];
            var expected = contract.Tables[index];
            var expectedFile = $"table-{index + 1:000}-{expected.Name}.jsonl";
            if (value is null
                || !string.Equals(value.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(value.File, expectedFile, StringComparison.Ordinal)
                || !ValidFramedCounters(
                    value.Batches,
                    value.Rows,
                    value.DecodedBytes,
                    value.Sha256,
                    maxBatchRows)
                || !ValidTableDecodedBytes(
                    value.Rows,
                    value.DecodedBytes,
                    expected,
                    maxFieldBytes)
                || !ValidTableBatching(
                    value.Batches,
                    value.Rows,
                    value.DecodedBytes,
                    expected,
                    maxFieldBytes,
                    maxBatchRows,
                    maxBatchBytes))
            {
                throw Failure("manifest-tables");
            }
        }
    }

    private static bool ValidTableDecodedBytes(
        long rows,
        long decodedBytes,
        TransferV3TableContract table,
        long maxFieldBytes)
    {
        try
        {
            var minimumRowBytes = table.Columns.Aggregate(
                0L,
                (total, column) => checked(total + MinimumFieldBytes(column)));
            var maximumRowBytes = table.Columns.Aggregate(
                0L,
                (total, column) => checked(total + MaximumFieldBytes(column, maxFieldBytes)));
            var minimumDecodedBytes = checked(rows * minimumRowBytes);
            var maximumDecodedBytes = checked(rows * maximumRowBytes);
            return decodedBytes >= minimumDecodedBytes
                && decodedBytes <= maximumDecodedBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long MinimumFieldBytes(TransferV3ColumnContract column)
    {
        if (column.Nullable)
        {
            return 1;
        }

        return column.Kind switch
        {
            TransferV3ColumnKind.Uuid => 17,
            TransferV3ColumnKind.Boolean => 2,
            TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 => 5,
            TransferV3ColumnKind.Int64
                or TransferV3ColumnKind.LocalWallTimestamp
                or TransferV3ColumnKind.Instant => 9,
            TransferV3ColumnKind.Text => 1,
            _ => throw Failure("manifest-tables"),
        };
    }

    private static long MaximumFieldBytes(
        TransferV3ColumnContract column,
        long maxFieldBytes) =>
        column.Kind switch
        {
            TransferV3ColumnKind.Uuid => 17,
            TransferV3ColumnKind.Boolean => 2,
            TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 => 5,
            TransferV3ColumnKind.Int64
                or TransferV3ColumnKind.LocalWallTimestamp
                or TransferV3ColumnKind.Instant => 9,
            TransferV3ColumnKind.Text when column.MaxRunes is { } maximumRunes =>
                Math.Min(maxFieldBytes, checked(1L + checked(4L * maximumRunes))),
            TransferV3ColumnKind.Text => maxFieldBytes,
            _ => throw Failure("manifest-tables"),
        };

    private static bool ValidTableBatching(
        int batches,
        long rows,
        long decodedBytes,
        TransferV3TableContract table,
        long maxFieldBytes,
        int maxBatchRows,
        long maxBatchBytes)
    {
        try
        {
            var minimumRowBytes = table.Columns.Aggregate(
                0L,
                (total, column) => checked(total + MinimumFieldBytes(column)));
            var maximumRowBytes = table.Columns.Aggregate(
                0L,
                (total, column) => checked(total + MaximumFieldBytes(column, maxFieldBytes)));
            return ValidBatchByteFeasibility(
                batches,
                rows,
                decodedBytes,
                minimumRowBytes,
                maximumRowBytes,
                maxBatchRows,
                maxBatchBytes);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidBatchByteFeasibility(
        int batches,
        long rows,
        long decodedBytes,
        long minimumRowBytes,
        long maximumRowBytes,
        int maxBatchRows,
        long maxBatchBytes)
    {
        if (rows == 0)
        {
            return batches == 0 && decodedBytes == 0;
        }

        if (batches <= 0
            || rows < batches
            || minimumRowBytes <= 0
            || maximumRowBytes < minimumRowBytes
            || maxBatchRows <= 0
            || maxBatchBytes <= 0)
        {
            return false;
        }

        try
        {
            var rowsPerBudgetedBatch = Math.Max(1L, maxBatchBytes / minimumRowBytes);
            var effectiveMaxBatchRows = Math.Min((long)maxBatchRows, rowsPerBudgetedBatch);
            if (rows > checked((long)batches * effectiveMaxBatchRows))
            {
                return false;
            }

            var minimumDecodedBytes = checked(rows * minimumRowBytes);
            var maximumDecodedBytes = MaximumFeasibleDecodedBytes(
                batches,
                rows,
                maximumRowBytes,
                effectiveMaxBatchRows,
                maxBatchBytes);
            return decodedBytes >= minimumDecodedBytes
                && decodedBytes <= maximumDecodedBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long MaximumFeasibleDecodedBytes(
        int batches,
        long rows,
        long maximumRowBytes,
        long effectiveMaxBatchRows,
        long maxBatchBytes)
    {
        if (maximumRowBytes >= maxBatchBytes)
        {
            var extraRows = checked(rows - batches);
            if (extraRows == 0)
            {
                return checked((long)batches * maximumRowBytes);
            }

            var extraRowsPerMultiRowBatch = checked(effectiveMaxBatchRows - 1);
            var multiRowBatches = checked(
                ((extraRows - 1) / extraRowsPerMultiRowBatch) + 1);
            var singletonBatches = checked((long)batches - multiRowBatches);
            return checked(
                checked(singletonBatches * maximumRowBytes)
                + checked(multiRowBatches * maxBatchBytes));
        }

        var smallerBatchRows = rows / batches;
        var largerBatchCount = rows % batches;
        var smallerBatchCount = checked((long)batches - largerBatchCount);
        var smallerBatchBytes = CappedBatchBytes(
            smallerBatchRows,
            maximumRowBytes,
            maxBatchBytes);
        var largerBatchBytes = CappedBatchBytes(
            checked(smallerBatchRows + 1),
            maximumRowBytes,
            maxBatchBytes);
        return checked(
            checked(smallerBatchCount * smallerBatchBytes)
            + checked(largerBatchCount * largerBatchBytes));
    }

    private static long CappedBatchBytes(
        long rows,
        long maximumRowBytes,
        long maxBatchBytes) =>
        rows > maxBatchBytes / maximumRowBytes
            ? maxBatchBytes
            : checked(rows * maximumRowBytes);

    private static void ValidateDerivedTables(
        ImmutableArray<TransferV3ManifestDerivedTable> tables,
        TransferV3SourceContract contract)
    {
        if (tables.IsDefault || tables.Length != contract.DerivedTables.Count)
        {
            throw Failure("manifest-derived");
        }

        for (var index = 0; index < tables.Length; index++)
        {
            var value = tables[index];
            if (value is null
                || !string.Equals(
                    value.Name,
                    contract.DerivedTables[index].Name,
                    StringComparison.Ordinal)
                || value.Rows < 0
                || !IsCanonicalDigest(value.LogicalSha256))
            {
                throw Failure("manifest-derived");
            }
        }
    }

    private static void ValidateInformationalReferences(
        ImmutableArray<TransferV3ManifestInformationalReference> references,
        TransferV3SourceContract contract)
    {
        var expectedNames = contract.Tables
            .SelectMany(table => table.References)
            .Where(reference => reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
            .Select(reference => reference.Name)
            .ToArray();
        if (references.IsDefault || references.Length != expectedNames.Length)
        {
            throw Failure("manifest-references");
        }

        for (var index = 0; index < references.Length; index++)
        {
            var value = references[index];
            if (value is null
                || !string.Equals(value.Name, expectedNames[index], StringComparison.Ordinal)
                || value.UnresolvedCount < 0
                || !IsCanonicalDigest(value.UnresolvedSha256))
            {
                throw Failure("manifest-references");
            }
        }
    }

    private static void ValidateBlobs(
        TransferV3ManifestBlobs blobs,
        int maxBatchRows,
        long maxFieldBytes,
        long maxBatchBytes)
    {
        if (blobs is null || blobs.Count < 0 || blobs.TotalBytes < 0)
        {
            throw Failure("manifest-blobs");
        }

        long expectedDecodedBytes;
        long maximumTotalBytes;
        long maximumRowBytes;
        try
        {
            expectedDecodedBytes = checked(checked(40L * blobs.Count) + blobs.TotalBytes);
            var maximumBlobContentBytes = checked(1023L * maxFieldBytes);
            maximumTotalBytes = checked(blobs.Count * maximumBlobContentBytes);
            maximumRowBytes = checked(40L + maximumBlobContentBytes);
        }
        catch (OverflowException)
        {
            throw Failure("manifest-blobs");
        }

        if (!string.Equals(blobs.Name, "Blobs", StringComparison.Ordinal)
            || !string.Equals(blobs.File, "Blobs.jsonl", StringComparison.Ordinal)
            || !ValidFramedCounters(
                blobs.Batches,
                blobs.Rows,
                blobs.DecodedBytes,
                blobs.Sha256,
                maxBatchRows)
            || blobs.Rows != blobs.Count
            || (blobs.Count == 0 && blobs.TotalBytes != 0)
            || blobs.TotalBytes > maximumTotalBytes
            || blobs.DecodedBytes != expectedDecodedBytes
            || !ValidBatchByteFeasibility(
                blobs.Batches,
                blobs.Rows,
                blobs.DecodedBytes,
                minimumRowBytes: 40,
                maximumRowBytes,
                maxBatchRows,
                maxBatchBytes)
            || !IsCanonicalDigest(blobs.InventorySha256))
        {
            throw Failure("manifest-blobs");
        }
    }

    private static bool ValidFramedCounters(
        int batches,
        long rows,
        long decodedBytes,
        string digest,
        int maxBatchRows)
    {
        if (batches < 0
            || rows < 0
            || decodedBytes < 0
            || maxBatchRows <= 0
            || !IsCanonicalDigest(digest))
        {
            return false;
        }

        if (rows == 0)
        {
            return batches == 0 && decodedBytes == 0;
        }

        var minimumBatches = ((rows - 1) / maxBatchRows) + 1;
        return batches >= minimumBatches
            && batches <= rows
            && decodedBytes > 0;
    }

    private static bool IsCanonicalDigest(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void Write(Utf8JsonWriter writer, TransferV3Manifest manifest)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", manifest.FormatVersion);
        writer.WriteString("sourceProvider", manifest.SourceProvider);
        writer.WriteString("sourceContractSha256", manifest.SourceContractSha256);
        writer.WriteString("sourceSchemaSha256", manifest.SourceSchemaSha256);
        writer.WriteString("migrationContractSha256", manifest.MigrationContractSha256);
        writer.WriteString("sourceTimeZoneId", manifest.SourceTimeZoneId);

        writer.WritePropertyName("limits");
        writer.WriteStartObject();
        writer.WriteNumber("maxFieldBytes", manifest.Limits.MaxFieldBytes);
        writer.WriteNumber("maxBatchRows", manifest.Limits.MaxBatchRows);
        writer.WriteNumber("maxBatchBytes", manifest.Limits.MaxBatchBytes);
        writer.WriteEndObject();

        writer.WritePropertyName("tables");
        writer.WriteStartArray();
        foreach (var table in manifest.Tables)
        {
            writer.WriteStartObject();
            writer.WriteString("name", table.Name);
            writer.WriteString("file", table.File);
            writer.WriteNumber("batches", table.Batches);
            writer.WriteNumber("rows", table.Rows);
            writer.WriteNumber("decodedBytes", table.DecodedBytes);
            writer.WriteString("sha256", table.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("derivedTables");
        writer.WriteStartArray();
        foreach (var table in manifest.DerivedTables)
        {
            writer.WriteStartObject();
            writer.WriteString("name", table.Name);
            writer.WriteNumber("rows", table.Rows);
            writer.WriteString("logicalSha256", table.LogicalSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("informationalReferences");
        writer.WriteStartArray();
        foreach (var reference in manifest.InformationalReferences)
        {
            writer.WriteStartObject();
            writer.WriteString("name", reference.Name);
            writer.WriteNumber("unresolvedCount", reference.UnresolvedCount);
            writer.WriteString("unresolvedSha256", reference.UnresolvedSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("blobs");
        writer.WriteStartObject();
        writer.WriteString("name", manifest.Blobs.Name);
        writer.WriteString("file", manifest.Blobs.File);
        writer.WriteNumber("batches", manifest.Blobs.Batches);
        writer.WriteNumber("rows", manifest.Blobs.Rows);
        writer.WriteNumber("decodedBytes", manifest.Blobs.DecodedBytes);
        writer.WriteString("sha256", manifest.Blobs.Sha256);
        writer.WriteNumber("count", manifest.Blobs.Count);
        writer.WriteNumber("totalBytes", manifest.Blobs.TotalBytes);
        writer.WriteString("inventorySha256", manifest.Blobs.InventorySha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static TransferV3ManifestFormatException Failure(string code) => new(code);
}

internal sealed class TransferV3ManifestFormatException : FormatException
{
    internal TransferV3ManifestFormatException(string code)
        : base($"Transfer-v3 manifest rejected ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }
}

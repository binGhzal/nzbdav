using System.Text;
using System.Reflection;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ManifestCodecTests
{
    private const string DigestA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DigestC =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string DigestD =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public void Serialize_ProducesTheExactCompleteCanonicalManifestBytes()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var manifest = CompleteManifest(contract);

        var bytes = TransferV3ManifestCodec.Serialize(manifest, contract);
        var expected = ExpectedCanonicalJson(manifest);

        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'}', bytes[^1]);
        Assert.DoesNotContain((byte)'\n', bytes);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.True(bytes.Length <= TransferV3ManifestCodec.MaxManifestBytes);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant(),
            TransferV3ManifestCodec.ComputeSha256(bytes));

        var parsed = TransferV3ManifestCodec.Parse(bytes, contract);
        Assert.Equal(bytes, TransferV3ManifestCodec.Serialize(parsed, contract));
        Assert.Equal(27, parsed.Tables.Length);
        Assert.Equal(ExpectedInformationalNames(contract),
            parsed.InformationalReferences.Select(item => item.Name));
    }

    [Fact]
    public void Parse_RejectsEveryStructuralAndCanonicalMutation()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = Encoding.UTF8.GetString(
            TransferV3ManifestCodec.Serialize(CompleteManifest(contract), contract));
        var firstTable = CompleteManifest(contract).Tables[0];
        var secondTable = CompleteManifest(contract).Tables[1];
        var firstReference = CompleteManifest(contract).InformationalReferences[0];
        var secondReference = CompleteManifest(contract).InformationalReferences[1];
        var mutations = new Func<string, string>[]
        {
            value => " " + value,
            value => value + "\n",
            value => value + "{}",
            value => value.Replace(
                "{\"formatVersion\":3,\"sourceProvider\"",
                "{\"sourceProvider\":\"Microsoft.EntityFrameworkCore.Sqlite\",\"formatVersion\":3,\"discarded\"",
                StringComparison.Ordinal),
            value => value.Replace(
                "\"formatVersion\":3",
                "\"formatVersion\":3,\"formatVersion\":3",
                StringComparison.Ordinal),
            value => value.Replace(
                "\"formatVersion\":3",
                "\"unknown\":0,\"formatVersion\":3",
                StringComparison.Ordinal),
            value => value.Replace(
                "\"formatVersion\":3,",
                string.Empty,
                StringComparison.Ordinal),
            value => value.Replace(
                "\"formatVersion\":3",
                "\"formatVersion\":3.0",
                StringComparison.Ordinal),
            value => value.Replace(
                "\"formatVersion\"",
                "\"\\u0066ormatVersion\"",
                StringComparison.Ordinal),
            value => value.Replace(DigestA, DigestA.ToUpperInvariant(), StringComparison.Ordinal),
            value => value.Replace("\"rows\":1", "\"rows\":-1", StringComparison.Ordinal),
            value => value.Replace("\"maxBatchRows\":256", "\"maxBatchRows\":0", StringComparison.Ordinal),
            value => value.Replace("\"sourceTimeZoneId\":\"UTC\"", "\"sourceTimeZoneId\":\"secret/not-a-time-zone\"", StringComparison.Ordinal),
            value => value.Replace(
                TableJson(firstTable) + "," + TableJson(secondTable),
                TableJson(secondTable) + "," + TableJson(firstTable),
                StringComparison.Ordinal),
            value => value.Replace(
                "\"file\":\"table-001-Accounts.jsonl\"",
                "\"file\":\"secret/Accounts.jsonl\"",
                StringComparison.Ordinal),
            value => value.Replace("," + TableJson(secondTable), string.Empty, StringComparison.Ordinal),
            value => value.Replace(
                InformationalJson(firstReference) + "," + InformationalJson(secondReference),
                InformationalJson(secondReference) + "," + InformationalJson(firstReference),
                StringComparison.Ordinal),
            value => value.Replace("," + InformationalJson(secondReference), string.Empty, StringComparison.Ordinal),
            value => value.Replace("\"name\":\"Blobs\"", "\"name\":\"blobs\"", StringComparison.Ordinal),
            value => value.Replace(
                "\"inventorySha256\":\"" + DigestD + "\"",
                "\"inventorySha256\":\"" + DigestD + "\",\"unknown\":0",
                StringComparison.Ordinal),
        };

        foreach (var mutate in mutations)
        {
            var changed = mutate(valid);
            Assert.NotEqual(valid, changed);
            AssertManifestRejected(Encoding.UTF8.GetBytes(changed));
        }

        void AssertManifestRejected(byte[] bytes)
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Parse(bytes, contract));
            Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Accounts", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(DigestA[..12], exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parse_RejectsPurePropertyReorderingCommentsAndTrailingCommas()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = Encoding.UTF8.GetString(
            TransferV3ManifestCodec.Serialize(CompleteManifest(contract), contract));
        const string canonicalPrefix =
            "{\"formatVersion\":3,\"sourceProvider\":\"Microsoft.EntityFrameworkCore.Sqlite\"";
        const string reorderedPrefix =
            "{\"sourceProvider\":\"Microsoft.EntityFrameworkCore.Sqlite\",\"formatVersion\":3";
        var reordered = valid.Replace(canonicalPrefix, reorderedPrefix, StringComparison.Ordinal);
        var withComment = valid.Replace(
            "\"formatVersion\":3,",
            "\"formatVersion\":3/*comment*/,",
            StringComparison.Ordinal);
        var withTrailingComma = valid[..^1] + ",}";

        Assert.NotEqual(valid, reordered);
        Assert.NotEqual(valid, withComment);
        Assert.NotEqual(valid, withTrailingComma);
        foreach (var invalid in new[] { reordered, withComment, withTrailingComma })
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Parse(Encoding.UTF8.GetBytes(invalid), contract));
            Assert.DoesNotContain("comment", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Microsoft", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parse_RejectsBomInvalidUtf8AndInputOverTheFixedMaximumBeforeJsonUse()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = TransferV3ManifestCodec.Serialize(CompleteManifest(contract), contract);
        var bom = new byte[] { 0xef, 0xbb, 0xbf }.Concat(valid).ToArray();
        var invalidUtf8 = valid.ToArray();
        invalidUtf8[1] = 0xff;
        var oversized = new byte[TransferV3ManifestCodec.MaxManifestBytes + 1];

        Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Parse(bom, contract));
        Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Parse(invalidUtf8, contract));
        var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Parse(oversized, contract));
        Assert.Equal("manifest-size", exception.Code);
    }

    [Fact]
    public void Serialize_RequiresTheEmbeddedContractCardinalitiesAndRedactsInvalidValues()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var missingTable = valid with { Tables = valid.Tables.RemoveAt(0) };
        var wrongProvider = valid with { SourceProvider = "secret-provider" };
        var badDigest = valid with { SourceContractSha256 = "secret-digest" };

        foreach (var invalid in new[] { missingTable, wrongProvider, badDigest })
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(invalid, contract));
            Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Accounts", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Manifest_DefensivelySnapshotsCallerCollectionsBeforeValidationAndWriting()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var complete = CompleteManifest(contract);
        var mutableTables = complete.Tables.ToList();
        var mutatingTables = new MutatesAfterIndexedValidationList<TransferV3ManifestTable>(
            mutableTables);
        var manifest = new TransferV3Manifest(
            complete.FormatVersion,
            complete.SourceProvider,
            complete.SourceContractSha256,
            complete.SourceSchemaSha256,
            complete.MigrationContractSha256,
            complete.SourceTimeZoneId,
            complete.Limits,
            mutatingTables,
            complete.DerivedTables,
            complete.InformationalReferences,
            complete.Blobs);
        var expected = TransferV3ManifestCodec.Serialize(complete, contract);

        var duringValidationAndWrite = TransferV3ManifestCodec.Serialize(manifest, contract);
        mutableTables.Clear();
        var afterCallerMutation = TransferV3ManifestCodec.Serialize(manifest, contract);

        Assert.Equal(expected, duringValidationAndWrite);
        Assert.Equal(expected, afterCallerMutation);
        Assert.Equal(contract.Tables.Count, manifest.Tables.Length);
    }

    [Fact]
    public void Parse_ReturnsCollectionsWhoseBackingCannotBeMutated()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var bytes = TransferV3ManifestCodec.Serialize(CompleteManifest(contract), contract);
        var parsed = TransferV3ManifestCodec.Parse(bytes, contract);
        var exposed = Assert.IsAssignableFrom<IList<TransferV3ManifestTable>>(parsed.Tables);
        var original = parsed.Tables[0];

        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed[0] = original with { Rows = 99 });
        Assert.Equal(original, parsed.Tables[0]);
        Assert.Equal(bytes, TransferV3ManifestCodec.Serialize(parsed, contract));
    }

    [Fact]
    public void Serialize_RejectsIllegalLimitsAndFramedTableCountersWithStableCodes()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var first = valid.Tables[0];
        var invalid = new[]
        {
            (Manifest: valid with
             {
                 Limits = valid.Limits with { MaxFieldBytes = 39 },
             }, Code: "manifest-limits"),
            (Manifest: ReplaceFirstTable(valid, first with { Batches = 2, Rows = 1 }),
                Code: "manifest-tables"),
            (Manifest: ReplaceFirstTable(valid, first with { Batches = 1, Rows = 257 }),
                Code: "manifest-tables"),
            (Manifest: ReplaceFirstTable(valid, first with
             {
                 Batches = 0,
                 Rows = 0,
                 DecodedBytes = 1,
             }), Code: "manifest-tables"),
        };

        foreach (var item in invalid)
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(item.Manifest, contract));
            Assert.Equal(item.Code, exception.Code);
            Assert.Equal($"Transfer-v3 manifest rejected ({item.Code}).", exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void Serialize_RejectsMathematicallyImpossibleTableDecodedBytesAndOverflow()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var first = valid.Tables[0];
        var firstContract = contract.Tables[0];
        var minimumRowBytes = MinimumDecodedRowBytes(firstContract);
        var maximumRowBytes = checked(
            firstContract.Columns.Count * valid.Limits.MaxFieldBytes);
        var overflowLimits = valid.Limits with
        {
            MaxFieldBytes = TransferV3Limits.MaxAllowedFieldBytes,
            MaxBatchRows = int.MaxValue,
        };
        var invalid = new[]
        {
            ReplaceFirstTable(valid, first with
            {
                DecodedBytes = minimumRowBytes - 1,
            }),
            ReplaceFirstTable(valid, first with
            {
                DecodedBytes = maximumRowBytes + 1,
            }),
            ReplaceFirstTable(valid with { Limits = overflowLimits }, first with
            {
                Batches = 1,
                Rows = int.MaxValue,
                DecodedBytes = long.MaxValue,
            }),
        };

        foreach (var manifest in invalid)
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(manifest, contract));
            Assert.Equal("manifest-tables", exception.Code);
            Assert.Equal(
                "Transfer-v3 manifest rejected (manifest-tables).",
                exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void Serialize_RejectsBytesAboveExactFixedAndRuneCappedTableMaxima()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var cleanupIndex = FindTableIndex(contract, "BlobCleanupItems");
        var cleanup = valid.Tables[cleanupIndex] with
        {
            Batches = 1,
            Rows = 1,
            DecodedBytes = 18,
        };
        var accountsIndex = FindTableIndex(contract, "Accounts");
        var accountsContract = contract.Tables[accountsIndex];
        var accountsMaximum = MaximumDecodedRowBytes(
            accountsContract,
            valid.Limits.MaxFieldBytes);
        var accounts = valid.Tables[accountsIndex] with
        {
            Batches = 1,
            Rows = 1,
            DecodedBytes = checked(accountsMaximum + 1),
        };

        Assert.Equal(17, MaximumDecodedRowBytes(
            contract.Tables[cleanupIndex],
            valid.Limits.MaxFieldBytes));
        Assert.Equal(
            5L + (1 + 4L * 255) + 2L * valid.Limits.MaxFieldBytes,
            accountsMaximum);
        foreach (var manifest in new[]
                 {
                     ReplaceTable(valid, cleanupIndex, cleanup),
                     ReplaceTable(valid, accountsIndex, accounts),
                 })
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(manifest, contract));
            Assert.Equal("manifest-tables", exception.Code);
            Assert.Equal(
                "Transfer-v3 manifest rejected (manifest-tables).",
                exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void ExactTableMaximum_RejectsCheckedSumOverflowWithoutUsingCoarseColumnCount()
    {
        var columns = new ThrowsOnCountReadOnlyList<TransferV3ColumnContract>(
        [
            TextColumn("First"),
            TextColumn("Second"),
        ]);
        var table = new TransferV3TableContract(
            "Synthetic",
            columns,
            [],
            [],
            [],
            null);
        var method = typeof(TransferV3ManifestCodec).GetMethod(
            "ValidTableDecodedBytes",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method.Invoke(null, [1L, 1L, table, long.MaxValue]);
        Assert.False(Assert.IsType<bool>(result));
    }

    [Fact]
    public void Serialize_RejectsMultiRowBatchesThatRequireTheSingletonOverBudgetException()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var overBudgetBytes = checked(valid.Limits.MaxBatchBytes + 1);
        var historyIndex = FindTableIndex(contract, "HistoryItems");
        var history = valid.Tables[historyIndex] with
        {
            Batches = 1,
            Rows = 2,
            DecodedBytes = overBudgetBytes,
        };
        var impossibleTable = ReplaceTable(valid, historyIndex, history);
        var impossibleBlobs = valid with
        {
            Blobs = valid.Blobs with
            {
                Batches = 1,
                Rows = 2,
                Count = 2,
                TotalBytes = checked(overBudgetBytes - 80),
                DecodedBytes = overBudgetBytes,
            },
        };

        var tableException = Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Serialize(impossibleTable, contract));
        Assert.Equal("manifest-tables", tableException.Code);
        Assert.Equal(
            "Transfer-v3 manifest rejected (manifest-tables).",
            tableException.Message);
        Assert.Null(tableException.InnerException);

        var blobException = Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Serialize(impossibleBlobs, contract));
        Assert.Equal("manifest-blobs", blobException.Code);
        Assert.Equal(
            "Transfer-v3 manifest rejected (manifest-blobs).",
            blobException.Message);
        Assert.Null(blobException.InnerException);
    }

    [Fact]
    public void Serialize_DerivesMinimumBatchCountFromMinimumRowBytesAndBatchBytes()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var historyIndex = FindTableIndex(contract, "HistoryItems");
        var minimumRowBytes = MinimumDecodedRowBytes(contract.Tables[historyIndex]);
        var constrained = valid with
        {
            Limits = valid.Limits with
            {
                MaxBatchBytes = checked(2 * minimumRowBytes - 1),
            },
        };
        var history = constrained.Tables[historyIndex] with
        {
            Batches = 2,
            Rows = 3,
            DecodedBytes = checked(3 * minimumRowBytes),
        };
        var impossible = ReplaceTable(constrained, historyIndex, history);

        var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
            TransferV3ManifestCodec.Serialize(impossible, contract));
        Assert.Equal("manifest-tables", exception.Code);
        Assert.Equal(
            "Transfer-v3 manifest rejected (manifest-tables).",
            exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Serialize_AcceptsOverBudgetSingletonTableAndBlobRows()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var giantBytes = checked(valid.Limits.MaxBatchBytes + 1);
        var historyIndex = FindTableIndex(contract, "HistoryItems");
        var history = valid.Tables[historyIndex] with
        {
            Batches = 1,
            Rows = 1,
            DecodedBytes = giantBytes,
        };
        var giantSingletons = ReplaceTable(valid, historyIndex, history) with
        {
            Blobs = valid.Blobs with
            {
                Batches = 1,
                Rows = 1,
                Count = 1,
                TotalBytes = checked(giantBytes - 40),
                DecodedBytes = giantBytes,
            },
        };

        _ = TransferV3ManifestCodec.Serialize(giantSingletons, contract);
    }

    [Fact]
    public void Serialize_RejectsInconsistentOrOverflowingBlobCountersWithStableCode()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var invalid = new[]
        {
            valid with { Blobs = valid.Blobs with { Batches = 2, Rows = 1 } },
            valid with { Blobs = valid.Blobs with { Batches = 1, Rows = 257, Count = 257 } },
            valid with { Blobs = valid.Blobs with { Rows = 2 } },
            valid with { Blobs = valid.Blobs with { DecodedBytes = 41 } },
            valid with
            {
                Blobs = valid.Blobs with
                {
                    Batches = 0,
                    Rows = 0,
                    Count = 0,
                    TotalBytes = 1,
                    DecodedBytes = 1,
                },
            },
            valid with
            {
                Blobs = valid.Blobs with
                {
                    Batches = 1,
                    Rows = long.MaxValue,
                    Count = long.MaxValue,
                    TotalBytes = long.MaxValue,
                    DecodedBytes = long.MaxValue,
                },
            },
        };

        foreach (var manifest in invalid)
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(manifest, contract));
            Assert.Equal("manifest-blobs", exception.Code);
            Assert.Equal(
                "Transfer-v3 manifest rejected (manifest-blobs).",
                exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void Serialize_EnforcesBlobContentFieldCeilingAndCheckedAggregateBound()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var maximumOneBlobBytes = checked(1023L * valid.Limits.MaxFieldBytes);
        var oversizedOneBlobBytes = checked(maximumOneBlobBytes + 1);
        var oneBlobTooLarge = valid with
        {
            Blobs = valid.Blobs with
            {
                TotalBytes = oversizedOneBlobBytes,
                DecodedBytes = checked(40 + oversizedOneBlobBytes),
            },
        };
        var overflowingCount = (long)int.MaxValue;
        var aggregateOverflow = valid with
        {
            Limits = valid.Limits with
            {
                MaxFieldBytes = TransferV3Limits.MaxAllowedFieldBytes,
                MaxBatchRows = int.MaxValue,
            },
            Blobs = valid.Blobs with
            {
                Batches = 1,
                Rows = overflowingCount,
                Count = overflowingCount,
                TotalBytes = 0,
                DecodedBytes = checked(40 * overflowingCount),
            },
        };

        foreach (var manifest in new[] { oneBlobTooLarge, aggregateOverflow })
        {
            var exception = Assert.Throws<TransferV3ManifestFormatException>(() =>
                TransferV3ManifestCodec.Serialize(manifest, contract));
            Assert.Equal("manifest-blobs", exception.Code);
            Assert.Equal(
                "Transfer-v3 manifest rejected (manifest-blobs).",
                exception.Message);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void Serialize_AcceptsExactMaximumAndMultipleEmptyBlobAccounting()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var valid = CompleteManifest(contract);
        var maximumOneBlobBytes = checked(1023L * valid.Limits.MaxFieldBytes);
        var maximum = valid with
        {
            Blobs = valid.Blobs with
            {
                TotalBytes = maximumOneBlobBytes,
                DecodedBytes = checked(40 + maximumOneBlobBytes),
            },
        };
        var empty = valid with
        {
            Blobs = valid.Blobs with
            {
                Batches = 1,
                Rows = 2,
                Count = 2,
                TotalBytes = 0,
                DecodedBytes = 80,
            },
        };

        _ = TransferV3ManifestCodec.Serialize(maximum, contract);
        _ = TransferV3ManifestCodec.Serialize(empty, contract);
    }

    private static TransferV3Manifest CompleteManifest(TransferV3SourceContract contract)
    {
        var tables = contract.Tables.Select((table, index) =>
            new TransferV3ManifestTable(
                table.Name,
                $"table-{index + 1:000}-{table.Name}.jsonl",
                Batches: index + 1,
                Rows: index + 1,
                DecodedBytes: checked((index + 1) * MinimumDecodedRowBytes(table)),
                Sha256: DigestA)).ToArray();
        var derived = contract.DerivedTables.Select(table =>
            new TransferV3ManifestDerivedTable(table.Name, Rows: 2, LogicalSha256: DigestB))
            .ToArray();
        var informational = ExpectedInformationalNames(contract).Select(name =>
            new TransferV3ManifestInformationalReference(
                name,
                UnresolvedCount: 0,
                UnresolvedSha256: DigestC)).ToArray();

        return new TransferV3Manifest(
            FormatVersion: 3,
            SourceProvider: "Microsoft.EntityFrameworkCore.Sqlite",
            SourceContractSha256: contract.ComputeSha256(),
            SourceSchemaSha256: contract.SourceSchemaSha256,
            MigrationContractSha256: contract.MigrationSourceContractSha256,
            SourceTimeZoneId: "UTC",
            Limits: new TransferV3ManifestLimits(
                MaxFieldBytes: 1_048_576,
                MaxBatchRows: 256,
                MaxBatchBytes: 4_194_304),
            Tables: tables,
            DerivedTables: derived,
            InformationalReferences: informational,
            Blobs: new TransferV3ManifestBlobs(
                Name: "Blobs",
                File: "Blobs.jsonl",
                Batches: 1,
                Rows: 1,
                DecodedBytes: 40,
                Sha256: DigestA,
                Count: 1,
                TotalBytes: 0,
                InventorySha256: DigestD));
    }

    private static TransferV3Manifest ReplaceFirstTable(
        TransferV3Manifest manifest,
        TransferV3ManifestTable first) =>
        ReplaceTable(manifest, 0, first);

    private static TransferV3Manifest ReplaceTable(
        TransferV3Manifest manifest,
        int index,
        TransferV3ManifestTable table) =>
        manifest with { Tables = manifest.Tables.SetItem(index, table) };

    private static int FindTableIndex(TransferV3SourceContract contract, string name) =>
        contract.Tables
            .Select((table, index) => (table.Name, Index: index))
            .Single(item => item.Name == name)
            .Index;

    private static long MinimumDecodedRowBytes(TransferV3TableContract table) =>
        table.Columns.Sum(column => column.Nullable
            ? 1L
            : column.Kind switch
            {
                TransferV3ColumnKind.Uuid => 17,
                TransferV3ColumnKind.Boolean => 2,
                TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 => 5,
                TransferV3ColumnKind.Int64
                    or TransferV3ColumnKind.LocalWallTimestamp
                    or TransferV3ColumnKind.Instant => 9,
                TransferV3ColumnKind.Text => 1,
                _ => throw new InvalidOperationException(),
            });

    private static long MaximumDecodedRowBytes(
        TransferV3TableContract table,
        long maxFieldBytes) =>
        table.Columns.Aggregate(
            0L,
            (total, column) => checked(total + MaximumFieldBytes(column, maxFieldBytes)));

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
            TransferV3ColumnKind.Text when column.MaxRunes is { } maximum =>
                Math.Min(maxFieldBytes, checked(1L + 4L * maximum)),
            TransferV3ColumnKind.Text => maxFieldBytes,
            _ => throw new InvalidOperationException(),
        };

    private static TransferV3ColumnContract TextColumn(string name) =>
        new(
            name,
            "TEXT",
            "text",
            false,
            TransferV3ColumnKind.Text,
            TransferV3InstantEncoding.None,
            TransferV3UuidRole.None,
            null,
            []);

    private static string ExpectedCanonicalJson(TransferV3Manifest manifest) =>
        "{\"formatVersion\":3"
        + ",\"sourceProvider\":\"Microsoft.EntityFrameworkCore.Sqlite\""
        + $",\"sourceContractSha256\":\"{manifest.SourceContractSha256}\""
        + $",\"sourceSchemaSha256\":\"{manifest.SourceSchemaSha256}\""
        + $",\"migrationContractSha256\":\"{manifest.MigrationContractSha256}\""
        + ",\"sourceTimeZoneId\":\"UTC\""
        + ",\"limits\":{\"maxFieldBytes\":1048576,\"maxBatchRows\":256,\"maxBatchBytes\":4194304}"
        + ",\"tables\":[" + string.Join(',', manifest.Tables.Select(TableJson)) + "]"
        + ",\"derivedTables\":[" + string.Join(',', manifest.DerivedTables.Select(DerivedJson)) + "]"
        + ",\"informationalReferences\":["
        + string.Join(',', manifest.InformationalReferences.Select(InformationalJson)) + "]"
        + ",\"blobs\":{\"name\":\"Blobs\",\"file\":\"Blobs.jsonl\",\"batches\":1,\"rows\":1,\"decodedBytes\":40"
        + $",\"sha256\":\"{DigestA}\",\"count\":1,\"totalBytes\":0,\"inventorySha256\":\"{DigestD}\"}}}}";

    private static string TableJson(TransferV3ManifestTable table) =>
        $"{{\"name\":\"{table.Name}\",\"file\":\"{table.File}\",\"batches\":{table.Batches},\"rows\":{table.Rows},\"decodedBytes\":{table.DecodedBytes},\"sha256\":\"{table.Sha256}\"}}";

    private static string DerivedJson(TransferV3ManifestDerivedTable table) =>
        $"{{\"name\":\"{table.Name}\",\"rows\":{table.Rows},\"logicalSha256\":\"{table.LogicalSha256}\"}}";

    private static string InformationalJson(TransferV3ManifestInformationalReference reference) =>
        $"{{\"name\":\"{reference.Name}\",\"unresolvedCount\":{reference.UnresolvedCount},\"unresolvedSha256\":\"{reference.UnresolvedSha256}\"}}";

    private static string[] ExpectedInformationalNames(TransferV3SourceContract contract) =>
        contract.Tables
            .SelectMany(table => table.References)
            .Where(reference => reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
            .Select(reference => reference.Name)
            .ToArray();

    private sealed class MutatesAfterIndexedValidationList<T>(IReadOnlyList<T> items)
        : IReadOnlyList<T>
    {
        private bool _indexedThroughEnd;

        public int Count => items.Count;

        public T this[int index]
        {
            get
            {
                var value = items[index];
                if (index == items.Count - 1)
                {
                    _indexedThroughEnd = true;
                }

                return value;
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            (_indexedThroughEnd ? Enumerable.Empty<T>() : items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ThrowsOnCountReadOnlyList<T>(IReadOnlyList<T> items)
        : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException("Count must not be used.");

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}

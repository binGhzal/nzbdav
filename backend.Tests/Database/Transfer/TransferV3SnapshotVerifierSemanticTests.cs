using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotVerifierSemanticTests
{
    [Fact]
    public async Task InformationalDigests_MatchNonzeroOrdinaryAndPolymorphicEvidence_InEitherInsertionOrder()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var ordinary = FindInformationalReference(contract, "HistoryItems_DownloadDirId");
        var worker = FindInformationalReference(contract, "WorkerJobs_TargetId");
        Assert.Equal(TransferV3ReferencePolicy.InformationalDigest, ordinary.Reference.Policy);
        Assert.Equal(
            TransferV3ReferencePolicy.PolymorphicInformationalDigest,
            worker.Reference.Policy);

        InfoFact[] ordinaryFacts =
        [
            new(
                Guid.Parse("f0000000-0000-0000-0000-000000000001"),
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                null),
            new(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                null),
        ];
        InfoFact[] workerFacts =
        [
            new(
                Guid.Parse("e0000000-0000-0000-0000-000000000001"),
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                3),
            new(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Guid.Parse("60000000-0000-0000-0000-000000000001"),
                1),
            new(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Guid.Parse("50000000-0000-0000-0000-000000000001"),
                2),
        ];

        var ordinaryDigest = ComputeInformationalDigest(
            ordinary.Reference.Name,
            ordinaryFacts,
            polymorphic: false,
            includeDiscriminator: false);
        var workerDigest = ComputeInformationalDigest(
            worker.Reference.Name,
            workerFacts,
            polymorphic: true,
            includeDiscriminator: true);
        var discriminatorOmitted = ComputeInformationalDigest(
            worker.Reference.Name,
            workerFacts,
            polymorphic: true,
            includeDiscriminator: false);
        Assert.NotEqual(workerDigest, discriminatorOmitted);

        var evidence = new Dictionary<string, ManifestEvidence>(StringComparer.Ordinal)
        {
            [ordinary.Reference.Name] = new(ordinaryFacts.Length, ordinaryDigest),
            [worker.Reference.Name] = new(workerFacts.Length, workerDigest),
        };
        var manifest = CreateSemanticManifest(contract, evidence);
        var discriminatorOmittedManifest = CreateSemanticManifest(
            contract,
            new Dictionary<string, ManifestEvidence>(evidence, StringComparer.Ordinal)
            {
                [worker.Reference.Name] = new(workerFacts.Length, discriminatorOmitted),
            });

        await using (var forward = await CreateSemanticBaselineAsync(contract))
        {
            AddInformationalFacts(forward, ordinary, ordinaryFacts);
            AddInformationalFacts(forward, worker, workerFacts);

            var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
                forward.ValidateSemanticClosure(contract, discriminatorOmittedManifest));
            Assert.Equal("informational-reference", failure.Code);
            forward.ValidateSemanticClosure(contract, manifest);
        }

        await using var reverse = await CreateSemanticBaselineAsync(contract);
        AddInformationalFacts(reverse, ordinary, ordinaryFacts.Reverse());
        AddInformationalFacts(reverse, worker, workerFacts.Reverse());
        reverse.ValidateSemanticClosure(contract, manifest);
    }

    [Fact]
    public async Task DerivedHealthDigest_AggregatesRowsSortsBucketsAndFloorsNegativeEpochDays()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var davTableOrdinal = FindTableOrdinal(contract, "DavItems");
        var davIdColumn = FindColumnOrdinal(contract.Tables[davTableOrdinal], "Id");
        var healthTableOrdinal = FindTableOrdinal(contract, "HealthCheckResults");
        var davId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        await using var index = await CreateSemanticBaselineAsync(contract);
        index.BeginBatch(davTableOrdinal, 0);
        index.AddUuidValue(davIdColumn, 0, Network(davId));
        index.CommitBatch();

        var sink = new TransferV3IndexFactSink(contract, index, CancellationToken.None);
        sink.BeginBatch(healthTableOrdinal, 0);
        sink.AddVerifiedRow(CreateHealthFact(contract, 0, Guid.Parse(
            "10000000-0000-0000-0000-000000000001"), -1, davId, 1, 3));
        sink.AddVerifiedRow(CreateHealthFact(contract, 1, Guid.Parse(
            "20000000-0000-0000-0000-000000000001"), 0, davId, 0, 2));
        sink.AddVerifiedRow(CreateHealthFact(contract, 2, Guid.Parse(
            "30000000-0000-0000-0000-000000000001"), 86_399, davId, 0, 2));
        sink.AddVerifiedRow(CreateHealthFact(contract, 3, Guid.Parse(
            "40000000-0000-0000-0000-000000000001"), 86_400, davId, 1, 0));
        sink.CommitBatch();

        DerivedBucket[] buckets =
        [
            new(-86_400, 0, 1, 3, 1),
            new(0, 86_400, 0, 2, 2),
            new(86_400, 172_800, 1, 0, 1),
        ];
        var manifest = CreateSemanticManifest(
            contract,
            derived: new ManifestEvidence(
                buckets.Length,
                ComputeDerivedDigest(contract.DerivedTables.Single(), buckets)));

        index.ValidateSemanticClosure(contract, manifest);

        var counts = index.GetFactCounts();
        Assert.Equal(3, counts.HealthBuckets);
        Assert.Equal(4, counts.InformationalFacts);
    }

    [Fact]
    public async Task HealthFact_Year9999FinalDayRejectsBeforeCreatingAnOutOfRangeDerivedBucket()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = FindTableOrdinal(contract, "HealthCheckResults");
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        var sink = new TransferV3IndexFactSink(contract, index, CancellationToken.None);
        sink.BeginBatch(tableOrdinal, 0);

        var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            sink.AddVerifiedRow(CreateHealthFact(
                contract,
                0,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                DateTimeOffset.MaxValue.ToUnixTimeSeconds(),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                0,
                0)));

        Assert.Equal("derived-state", failure.Code);
        sink.AbortBatchNoThrow();
    }

    [Fact]
    public async Task SecondaryUniqueCollision_AcrossCommittedBatchesMapsToNormalizedCollision()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = FindTableOrdinal(contract, "QueueItems");
        var timestamp = new DateTime(2026, 7, 14, 1, 2, 3, DateTimeKind.Unspecified);
        var fixture = await WriteTableAsync(
            contract,
            tableOrdinal,
            [
                [
                    Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    "same-category",
                    timestamp,
                    "same-file.nzb",
                    "first-job",
                    123L,
                    null,
                    0,
                    0,
                    123L,
                    null,
                ],
                [
                    Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    "same-category",
                    timestamp,
                    "same-file.nzb",
                    "second-job",
                    456L,
                    null,
                    0,
                    0,
                    456L,
                    null,
                ],
            ]);
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        var sink = new TransferV3IndexFactSink(contract, index, CancellationToken.None);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                source,
                contract,
                tableOrdinal,
                fixture.Manifest,
                fixture.Limits,
                sink));

        Assert.Equal("unique-normalized-collision", failure.Code);
        var counts = index.GetFactCounts();
        Assert.Equal(1, counts.RowKeys);
        Assert.Equal(2, counts.UniqueValues);
    }

    [Theory]
    [InlineData("unique", "BlobCleanupItems", 3)]
    [InlineData("bootstrap", "ConfigItems", 4)]
    public async Task InjectedWrites_DuringUniqueOrBootstrapInsertionRemainIndexWrite(
        string stage,
        string tableName,
        int injectedWrite)
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = FindTableOrdinal(contract, tableName);
        var fixture = stage == "unique"
            ? await WriteTableAsync(
                contract,
                tableOrdinal,
                [[Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]])
            : await WriteTableAsync(
                contract,
                tableOrdinal,
                [["api.key", new string('a', 32)]]);
        var writes = 0;
        var hooks = new TransferV3BlobReferenceIndexHooks(point =>
        {
            if (!string.Equals(point, "before-write", StringComparison.Ordinal)) return;
            writes++;
            if (writes == injectedWrite)
                throw new InvalidOperationException("synthetic-index-write-fault");
        });
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync(hooks);
        var sink = new TransferV3IndexFactSink(contract, index, CancellationToken.None);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                source,
                contract,
                tableOrdinal,
                fixture.Manifest,
                fixture.Limits,
                sink));

        Assert.Equal("index-write", failure.Code);
        Assert.Equal(injectedWrite, writes);
        Assert.DoesNotContain("synthetic-index-write-fault", failure.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticQueryCancellation_InterruptsLargeUnsortedInformationalSortWithExactToken()
    {
        const int factCount = 100_000;
        var contract = TransferV3SourceContract.LoadEmbedded();
        var ordinary = FindInformationalReference(contract, "HistoryItems_DownloadDirId");
        using var queryProgressEntered = new ManualResetEventSlim();
        using var releaseQueryProgress = new ManualResetEventSlim();
        using var sqliteInterruptObserved = new ManualResetEventSlim();
        var hooks = new TransferV3BlobReferenceIndexHooks(point =>
        {
            if (string.Equals(point, "query-progress", StringComparison.Ordinal))
            {
                queryProgressEntered.Set();
                _ = releaseQueryProgress.Wait(TimeSpan.FromSeconds(5));
            }
            else if (string.Equals(point, "sqlite-interrupt", StringComparison.Ordinal))
            {
                sqliteInterruptObserved.Set();
            }
        });
        await using var index = await CreateSemanticBaselineAsync(contract, hooks);
        index.BeginBatch(ordinary.TableOrdinal, 0);
        var owner = new byte[16];
        var target = new byte[16];
        for (var row = 0; row < factCount; row++)
        {
            BinaryPrimitives.WriteInt32BigEndian(owner.AsSpan(12), factCount - row);
            BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(12), row + 1);
            index.AddInformationalFact(
                ordinary.GlobalReferenceOrdinal,
                row,
                owner,
                target,
                null);
        }
        index.CommitBatch();

        var manifest = CreateSemanticManifest(
            contract,
            new Dictionary<string, ManifestEvidence>(StringComparer.Ordinal)
            {
                [ordinary.Reference.Name] = new(
                    factCount,
                    ComputeLargeOrdinaryDigest(ordinary.Reference.Name, factCount)),
            });
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var validation = Task.Run<Exception?>(() =>
        {
            try
            {
                index.ValidateSemanticClosure(contract, manifest, cancellation.Token);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        var enteredNativeQuery = false;
        var invokedNativeInterrupt = false;
        var queryWasPending = false;
        try
        {
            enteredNativeQuery = queryProgressEntered.Wait(TimeSpan.FromSeconds(5));
            if (enteredNativeQuery)
            {
                queryWasPending = !validation.IsCompleted;
                cancellation.Cancel();
                invokedNativeInterrupt = sqliteInterruptObserved.Wait(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (!cancellation.IsCancellationRequested) cancellation.Cancel();
            releaseQueryProgress.Set();
        }
        var observed = await validation.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.True(enteredNativeQuery, "The test never entered an active SQLite VM query.");
        Assert.True(queryWasPending, "The SQLite query completed before cancellation.");
        Assert.True(invokedNativeInterrupt, "Cancellation never invoked sqlite3_interrupt.");
        var failure = Assert.IsAssignableFrom<OperationCanceledException>(observed);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Semantic cancellation took {stopwatch.Elapsed}.");
    }

    private static async Task<TransferV3BlobReferenceIndex> CreateSemanticBaselineAsync(
        TransferV3SourceContract contract,
        TransferV3BlobReferenceIndexHooks? hooks = null)
    {
        var index = await TransferV3BlobReferenceIndex.CreateAsync(hooks);
        index.BeginBatch(0, 0);
        for (var config = 0; config < contract.Bootstrap.Config.Count; config++)
        {
            index.AddBootstrapConfigSecret(
                config,
                Enumerable.Repeat((byte)(config + 1), SHA256.HashSizeInBytes).ToArray());
        }
        for (var root = 0; root < contract.Bootstrap.Roots.Count; root++)
        {
            index.AddBootstrapRootMarker(
                root,
                Enumerable.Repeat((byte)(root + 17), SHA256.HashSizeInBytes).ToArray());
        }
        index.CommitBatch();
        return index;
    }

    private static void AddInformationalFacts(
        TransferV3BlobReferenceIndex index,
        IndexedReference indexed,
        IEnumerable<InfoFact> facts)
    {
        index.BeginBatch(indexed.TableOrdinal, 0);
        long row = 0;
        foreach (var fact in facts)
        {
            index.AddInformationalFact(
                indexed.GlobalReferenceOrdinal,
                row++,
                Network(fact.Owner),
                Network(fact.Target),
                fact.Discriminator);
        }
        index.CommitBatch();
    }

    private static TransferV3VerifiedRowFacts CreateHealthFact(
        TransferV3SourceContract contract,
        long rowOrdinal,
        Guid id,
        long createdAt,
        Guid davItemId,
        int result,
        int repairStatus)
    {
        var tableOrdinal = FindTableOrdinal(contract, "HealthCheckResults");
        var table = contract.Tables[tableOrdinal];
        object?[] values =
        [
            id,
            createdAt,
            davItemId,
            "/synthetic/health-check",
            result,
            repairStatus,
            null,
        ];
        var fields = values
            .Select((value, ordinal) => CreateFieldFact(table.Columns[ordinal], ordinal, value))
            .ToImmutableArray();
        var key = SHA256.HashData(Network(id));
        var uniqueOrdinal = contract.Tables
            .Take(tableOrdinal)
            .Sum(candidate => candidate.UniqueKeys.Count);
        return new TransferV3VerifiedRowFacts(
            tableOrdinal,
            rowOrdinal,
            [TransferV3CursorComponent.FromGuid(id)],
            key,
            [new TransferV3VerifiedUniqueKeyFact(uniqueOrdinal, key.ToArray())],
            fields);
    }

    private static TransferV3VerifiedFieldFact CreateFieldFact(
        TransferV3ColumnContract column,
        int ordinal,
        object? value)
    {
        var encoded = TransferV3RowCodec.EncodeField(column, value);
        try
        {
            return new TransferV3VerifiedFieldFact(
                ordinal,
                value is null,
                encoded.Length,
                SHA256.HashData(encoded),
                value is Guid uuid ? uuid : null,
                value switch
                {
                    int integer => integer,
                    long integer => integer,
                    bool boolean => boolean ? 1 : 0,
                    _ => null,
                },
                value is DateTime local ? local : null,
                value is bool booleanValue ? booleanValue : null,
                false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static TransferV3Manifest CreateSemanticManifest(
        TransferV3SourceContract contract,
        IReadOnlyDictionary<string, ManifestEvidence>? informational = null,
        ManifestEvidence? derived = null)
    {
        var emptyDigest = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty))
            .ToLowerInvariant();
        var tables = contract.Tables.Select((table, index) =>
            new TransferV3ManifestTable(
                table.Name,
                $"table-{index + 1:000}-{table.Name}.jsonl",
                0,
                0,
                0,
                emptyDigest));
        var references = EnumerateInformationalReferences(contract).Select(indexed =>
        {
            var evidence = informational is not null
                           && informational.TryGetValue(indexed.Reference.Name, out var value)
                ? value
                : new ManifestEvidence(0, HashUtf8(indexed.Reference.Name));
            return new TransferV3ManifestInformationalReference(
                indexed.Reference.Name,
                evidence.Count,
                evidence.Digest);
        });
        var derivedEvidence = derived ?? new ManifestEvidence(0, emptyDigest);
        return new TransferV3Manifest(
            3,
            contract.Provider,
            contract.ComputeSha256(),
            contract.SourceSchemaSha256,
            contract.MigrationSourceContractSha256,
            TimeZoneInfo.Utc.Id,
            new TransferV3ManifestLimits(1024 * 1024, 1000, 16 * 1024 * 1024),
            tables,
            [new TransferV3ManifestDerivedTable(
                "HealthCheckStats",
                derivedEvidence.Count,
                derivedEvidence.Digest)],
            references,
            new TransferV3ManifestBlobs(
                "Blobs",
                "Blobs.jsonl",
                0,
                0,
                0,
                emptyDigest,
                0,
                0,
                emptyDigest));
    }

    private static string ComputeInformationalDigest(
        string name,
        IEnumerable<InfoFact> source,
        bool polymorphic,
        bool includeDiscriminator)
    {
        var facts = source.ToList();
        facts.Sort((left, right) =>
        {
            var comparison = CompareNetwork(left.Owner, right.Owner);
            if (comparison != 0) return comparison;
            if (polymorphic)
            {
                comparison = Nullable.Compare(left.Discriminator, right.Discriminator);
                if (comparison != 0) return comparison;
            }
            return CompareNetwork(left.Target, right.Target);
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        Span<byte> discriminator = stackalloc byte[sizeof(int)];
        foreach (var fact in facts)
        {
            hash.AppendData(Network(fact.Owner));
            if (polymorphic && includeDiscriminator)
            {
                BinaryPrimitives.WriteInt32BigEndian(
                    discriminator,
                    checked((int)(fact.Discriminator
                                  ?? throw new InvalidOperationException(
                                      "A polymorphic test fact needs a discriminator."))));
                hash.AppendData(discriminator);
            }
            hash.AppendData(Network(fact.Target));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeLargeOrdinaryDigest(string name, int factCount)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        var owner = new byte[16];
        var target = new byte[16];
        for (var ownerValue = 1; ownerValue <= factCount; ownerValue++)
        {
            BinaryPrimitives.WriteInt32BigEndian(owner.AsSpan(12), ownerValue);
            BinaryPrimitives.WriteInt32BigEndian(
                target.AsSpan(12),
                factCount - ownerValue + 1);
            hash.AppendData(owner);
            hash.AppendData(target);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeDerivedDigest(
        TransferV3TableContract table,
        IEnumerable<DerivedBucket> source)
    {
        var buckets = source
            .OrderBy(value => value.Start)
            .ThenBy(value => value.End)
            .ThenBy(value => value.Result)
            .ThenBy(value => value.RepairStatus)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> int32 = stackalloc byte[sizeof(int)];
        Span<byte> int64 = stackalloc byte[sizeof(long)];
        foreach (var bucket in buckets)
        {
            var cursor = TransferV3CursorCodec.Encode(
                TransferV3CursorComponent.FromInt64(bucket.Start),
                TransferV3CursorComponent.FromInt64(bucket.End),
                TransferV3CursorComponent.FromInt64(bucket.Result),
                TransferV3CursorComponent.FromInt64(bucket.RepairStatus));
            var cursorBytes = Encoding.ASCII.GetBytes(cursor);
            object[] values =
            [
                bucket.Start,
                bucket.End,
                bucket.Result,
                bucket.RepairStatus,
                bucket.Count,
            ];
            var fields = values
                .Select((value, ordinal) =>
                    TransferV3RowCodec.EncodeField(table.Columns[ordinal], value))
                .ToArray();
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(int32, cursorBytes.Length);
                hash.AppendData(int32);
                hash.AppendData(cursorBytes);
                BinaryPrimitives.WriteInt32BigEndian(int32, fields.Length);
                hash.AppendData(int32);
                foreach (var field in fields)
                {
                    BinaryPrimitives.WriteInt64BigEndian(int64, field.Length);
                    hash.AppendData(int64);
                    hash.AppendData(field);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cursorBytes);
                foreach (var field in fields) CryptographicOperations.ZeroMemory(field);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<TableFixture> WriteTableAsync(
        TransferV3SourceContract contract,
        int tableOrdinal,
        IReadOnlyList<object?[]> rows)
    {
        var table = contract.Tables[tableOrdinal];
        var limits = new TransferV3Limits(1024 * 1024);
        await using var bytes = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(bytes, table.Name, limits))
        {
            await writer.WriteTableHeaderAsync();
            string? after = null;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                Assert.Equal(table.Columns.Count, row.Length);
                var cursor = EncodeCursor(table, row);
                await writer.StartBatchAsync(rowIndex, after);
                await writer.StartChunkedRowAsync(cursor, table.Columns.Count);
                for (var column = 0; column < table.Columns.Count; column++)
                {
                    var encoded = TransferV3RowCodec.EncodeField(table.Columns[column], row[column]);
                    try
                    {
                        await writer.WriteFieldChunkAsync(column, encoded);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encoded);
                    }
                }
                await writer.EndChunkedRowAsync();
                await writer.EndBatchAsync();
                after = cursor;
            }
            var end = await writer.EndTableAsync();
            return new TableFixture(
                bytes.ToArray(),
                new TransferV3ManifestTable(
                    table.Name,
                    $"table-{tableOrdinal + 1:000}-{table.Name}.jsonl",
                    end.Batches,
                    end.Rows,
                    end.Bytes,
                    end.Sha256),
                limits);
        }
    }

    private static string EncodeCursor(TransferV3TableContract table, object?[] row)
    {
        var components = table.Keyset.Select(key =>
        {
            var value = row[FindColumnOrdinal(table, key.Column)];
            return value switch
            {
                Guid uuid => TransferV3CursorComponent.FromGuid(uuid),
                int integer => TransferV3CursorComponent.FromInt64(integer),
                long integer => TransferV3CursorComponent.FromInt64(integer),
                string text => TransferV3CursorComponent.FromText(text),
                _ => throw new InvalidOperationException("Unsupported synthetic cursor value."),
            };
        }).ToArray();
        return TransferV3CursorCodec.Encode(components);
    }

    private static IndexedReference FindInformationalReference(
        TransferV3SourceContract contract,
        string name) => EnumerateInformationalReferences(contract)
        .Single(value => string.Equals(value.Reference.Name, name, StringComparison.Ordinal));

    private static IEnumerable<IndexedReference> EnumerateInformationalReferences(
        TransferV3SourceContract contract)
    {
        var globalReferenceOrdinal = 0;
        for (var tableOrdinal = 0; tableOrdinal < contract.Tables.Count; tableOrdinal++)
        {
            foreach (var reference in contract.Tables[tableOrdinal].References)
            {
                if (reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                    or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
                {
                    yield return new IndexedReference(
                        tableOrdinal,
                        globalReferenceOrdinal,
                        reference);
                }
                globalReferenceOrdinal++;
            }
        }
    }

    private static int FindTableOrdinal(TransferV3SourceContract contract, string name) =>
        contract.Tables
            .Select((table, ordinal) => (table, ordinal))
            .Single(value => string.Equals(value.table.Name, name, StringComparison.Ordinal))
            .ordinal;

    private static int FindColumnOrdinal(TransferV3TableContract table, string name) =>
        table.Columns
            .Select((column, ordinal) => (column, ordinal))
            .Single(value => string.Equals(value.column.Name, name, StringComparison.Ordinal))
            .ordinal;

    private static int CompareNetwork(Guid left, Guid right) =>
        Network(left).AsSpan().SequenceCompareTo(Network(right));

    private static byte[] Network(Guid value)
    {
        var bytes = new byte[16];
        Assert.True(value.TryWriteBytes(bytes, bigEndian: true, out var written));
        Assert.Equal(bytes.Length, written);
        return bytes;
    }

    private static string HashUtf8(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    private sealed record IndexedReference(
        int TableOrdinal,
        int GlobalReferenceOrdinal,
        TransferV3ReferenceContract Reference);

    private sealed record InfoFact(Guid Owner, Guid Target, long? Discriminator);

    private sealed record DerivedBucket(
        long Start,
        long End,
        int Result,
        int RepairStatus,
        int Count);

    private sealed record ManifestEvidence(long Count, string Digest);

    private sealed record TableFixture(
        byte[] Bytes,
        TransferV3ManifestTable Manifest,
        TransferV3Limits Limits);
}

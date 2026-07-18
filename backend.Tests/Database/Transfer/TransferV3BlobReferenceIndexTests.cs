using System.Security.Cryptography;
using System.Buffers.Binary;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3BlobReferenceIndexTests
{
    [Fact]
    public async Task CreateAsync_UsesOneUnnamedPrivateSpillDatabaseWithHardenedPragmas()
    {
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();

        var diagnostics = index.GetDiagnostics();

        Assert.Equal(string.Empty, diagnostics.VerificationFile);
        Assert.Equal(0, diagnostics.MainTableCount);
        Assert.Equal(
            [
                "bootstrap_config", "bootstrap_roots", "dav_metadata", "hard_blob_refs",
                "health_buckets", "informational_facts", "legacy_metadata", "physical_blobs",
                "row_keys", "unique_values", "uuid_values",
            ],
            diagnostics.TableNames);
        Assert.Equal(1, diagnostics.TempStore);
        Assert.Equal(1, diagnostics.ForeignKeys);
        Assert.Equal(0, diagnostics.TrustedSchema);
        Assert.Equal(-8192, diagnostics.CacheSize);
        Assert.True(diagnostics.CacheSpill);
        Assert.Equal(0, diagnostics.MmapSize);
        Assert.Equal(1, diagnostics.SecureDelete);
        Assert.Equal(0, diagnostics.Synchronous);
        // Unnamed attached databases use SQLite's in-memory rollback journal. It is still
        // rollback-journal mode (never WAL), while the database pages themselves spill to FILE.
        Assert.Equal("memory", diagnostics.JournalMode);
        Assert.True(diagnostics.IsWritable);
    }

    [Fact]
    public async Task Batches_RetainOnlyFixedSizeTypedFactsAndExposeClosureQueries()
    {
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        var rowKey = Hash("row");
        var unique = Hash("unique");
        var uuid = Uuid(1);
        var missingBlob = Uuid(2);
        var physicalBlob = Uuid(3);

        index.BeginBatch(4, 7);
        index.AddRowKey(11, rowKey);
        index.AddUniqueValue(2, 11, unique);
        index.AddUuidValue(3, 11, uuid);
        index.AddHardBlobReference(5, 11, missingBlob);
        index.AddHardBlobReference(6, 12, physicalBlob);
        index.AddInformationalFact(8, 11, uuid, missingBlob, discriminator: 9);
        index.AddPhysicalBlob(physicalBlob, 1234, Hash("blob"));
        index.AddDavMetadata(11, uuid, parentId: null, type: 2, subType: 4, fileBlobId: physicalBlob);
        index.AddLegacyMetadata(9, 11, uuid);
        index.AddHealthBucket(100, 200, 3, 4, 5);
        index.AddBootstrapConfigSecret(1, Hash("secret-value"));
        index.AddBootstrapRootMarker(2, Hash("canonical-root"));
        index.CommitBatch();

        Assert.Equal(
            new TransferV3BlobReferenceFactCounts(1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1),
            index.GetFactCounts());
        Assert.True(index.ContainsRowKey(4, 11, rowKey));
        Assert.True(index.ContainsUuidValue(4, 3, 11, uuid));
        Assert.True(index.ContainsPhysicalBlob(physicalBlob, 1234, Hash("blob")));
        var unresolved = Assert.IsType<TransferV3UnresolvedBlobReference>(
            index.FindFirstUnresolvedHardBlobReference());
        Assert.Equal(4, unresolved.TableOrdinal);
        Assert.Equal(5, unresolved.ReferenceOrdinal);
        Assert.Equal(11, unresolved.RowOrdinal);
    }

    [Fact]
    public async Task AbortBatchNoThrow_RollsBackOnlyTheCurrentBatch()
    {
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();

        index.BeginBatch(1, 1);
        index.AddRowKey(1, Hash("committed"));
        index.CommitBatch();

        index.BeginBatch(1, 2);
        index.AddRowKey(2, Hash("aborted"));
        index.AbortBatchNoThrow();

        Assert.Equal(1, index.GetFactCounts().RowKeys);
        Assert.True(index.ContainsRowKey(1, 1, Hash("committed")));
        Assert.False(index.ContainsRowKey(1, 2, Hash("aborted")));
    }

    [Fact]
    public async Task FactShapesAndDuplicateEvidence_FailClosedWithSanitizedStableCodes()
    {
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        index.BeginBatch(1, 1);

        var shape = Assert.Throws<TransferV3BlobReferenceIndexException>(
            () => index.AddRowKey(1, new byte[31]));
        Assert.Equal("index-fact-shape", shape.Code);
        Assert.Equal("Transfer-v3 verification index failed: code=index-fact-shape.", shape.Message);
        Assert.Null(shape.InnerException);

        index.AddRowKey(1, Hash("same"));
        var duplicate = Assert.Throws<TransferV3BlobReferenceIndexException>(
            () => index.AddRowKey(1, Hash("artifact-private-value")));
        Assert.Equal("index-constraint", duplicate.Code);
        Assert.DoesNotContain("artifact-private-value", duplicate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE", duplicate.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(duplicate.InnerException);
        index.AbortBatchNoThrow();
    }

    [Fact]
    public async Task Instances_AreParallelAndHaveNoSharedFactsOrNamedDatabaseFile()
    {
        var instances = await Task.WhenAll(Enumerable.Range(0, 4).Select(async ordinal =>
        {
            var index = await TransferV3BlobReferenceIndex.CreateAsync();
            index.BeginBatch(ordinal, 1);
            index.AddRowKey(1, Hash($"row-{ordinal}"));
            index.CommitBatch();
            return index;
        }));

        try
        {
            Assert.All(instances, index =>
            {
                Assert.Equal(string.Empty, index.GetDiagnostics().VerificationFile);
                Assert.Equal(1, index.GetFactCounts().RowKeys);
            });
            for (var ordinal = 0; ordinal < instances.Length; ordinal++)
            {
                Assert.True(instances[ordinal].ContainsRowKey(ordinal, 1, Hash($"row-{ordinal}")));
                Assert.False(instances[ordinal].ContainsRowKey(
                    (ordinal + 1) % instances.Length,
                    1,
                    Hash($"row-{(ordinal + 1) % instances.Length}")));
            }
        }
        finally
        {
            foreach (var index in instances)
                await index.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnnamedStore_SpillsPastTheEightMiBCacheBudgetWithoutChangingFactBounds()
    {
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        var key = new byte[32];
        const int rowCount = 100_000;
        const int rowsPerBatch = 500;

        for (var first = 0; first < rowCount; first += rowsPerBatch)
        {
            index.BeginBatch(0, first / rowsPerBatch);
            for (var row = first; row < first + rowsPerBatch; row++)
            {
                BinaryPrimitives.WriteInt32BigEndian(key, row);
                index.AddRowKey(row, key);
            }
            index.CommitBatch();
        }

        var diagnostics = index.GetDiagnostics();
        Assert.True(diagnostics.PageCount * diagnostics.PageSize > 8L * 1024 * 1024);
        Assert.Equal(rowCount, index.GetFactCounts().RowKeys);
        Assert.Equal(string.Empty, diagnostics.VerificationFile);
    }

    [Fact]
    public async Task Cancellation_PreservesTheExactCallerToken()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var open = await Assert.ThrowsAsync<OperationCanceledException>(
            () => TransferV3BlobReferenceIndex.CreateAsync(cancellationToken: source.Token));
        Assert.Equal(source.Token, open.CancellationToken);

        await using var index = await TransferV3BlobReferenceIndex.CreateAsync();
        var begin = Assert.Throws<OperationCanceledException>(
            () => index.BeginBatch(1, 1, source.Token));
        Assert.Equal(source.Token, begin.CancellationToken);
    }

    [Theory]
    [InlineData("before-open", "index-open")]
    [InlineData("before-schema-write", "index-write")]
    public async Task CreateFaults_AreSanitizedAndCleanupDoesNotReplaceThePrimary(
        string primaryPoint,
        string expectedCode)
    {
        var hooks = new TransferV3BlobReferenceIndexHooks(point =>
        {
            if (point == primaryPoint)
                throw new InjectedIndexException("artifact-secret-path");
            if (point == "before-dispose")
                throw new InjectedIndexException("cleanup-secret-path");
        });

        var exception = await Assert.ThrowsAsync<TransferV3BlobReferenceIndexException>(
            () => TransferV3BlobReferenceIndex.CreateAsync(hooks, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("Transfer-v3 verification index failed: code=" + expectedCode + ".", exception.Message);
        Assert.DoesNotContain("artifact-secret-path", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup-secret-path", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        if (primaryPoint == "before-schema-write")
            Assert.Contains("index-dispose", exception.CleanupCodes);
    }

    [Fact]
    public async Task WriteQueryAndDisposeFaults_AreSanitizedAndDisposalStillClosesResources()
    {
        string? faultPoint = null;
        var hooks = new TransferV3BlobReferenceIndexHooks(point =>
        {
            if (point == faultPoint)
                throw new InjectedIndexException("do-not-leak-this");
        });
        var index = await TransferV3BlobReferenceIndex.CreateAsync(hooks);

        faultPoint = "before-write";
        var write = Assert.Throws<TransferV3BlobReferenceIndexException>(() => index.BeginBatch(1, 1));
        Assert.Equal("index-write", write.Code);

        faultPoint = null;
        index.BeginBatch(1, 1);
        index.AddRowKey(1, Hash("retained"));
        index.CommitBatch();

        faultPoint = "before-query";
        var query = Assert.Throws<TransferV3BlobReferenceIndexException>(() => index.GetFactCounts());
        Assert.Equal("index-query", query.Code);

        faultPoint = "before-dispose";
        var dispose = await Assert.ThrowsAsync<TransferV3BlobReferenceIndexException>(
            async () => await index.DisposeAsync());
        Assert.Equal("index-dispose", dispose.Code);
        Assert.DoesNotContain("do-not-leak-this", dispose.ToString(), StringComparison.Ordinal);
        await index.DisposeAsync();
    }

    [Fact]
    public void Source_HasNoArtifactControlledSqlIdentifiersPathsOrPlaintextSecretStorage()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3BlobReferenceIndex.cs"));

        Assert.Contains("DataSource = \":memory:\"", source, StringComparison.Ordinal);
        Assert.Contains("Pooling = false", source, StringComparison.Ordinal);
        Assert.Contains("ATTACH DATABASE '' AS verification", source, StringComparison.Ordinal);
        Assert.Contains("raw.sqlite3_interrupt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source=$", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_value", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret TEXT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("byte[] Uuid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("string column", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", source, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            source, "BeginTransactionAsync\\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.Contains("_transaction.Save(SavepointName)", source, StringComparison.Ordinal);
        Assert.Contains("_transaction.Release(SavepointName)", source, StringComparison.Ordinal);
        Assert.Contains("command.Prepare()", source, StringComparison.Ordinal);
    }

    private static byte[] Hash(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] Uuid(byte seed) => Enumerable.Range(0, 16).Select(value => (byte)(seed + value)).ToArray();

    private static string RepositoryPath(
        string relative,
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "")
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                     Path.GetDirectoryName(callerPath) ?? string.Empty,
                 })
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException(relative);
    }

    private sealed class InjectedIndexException(string message) : Exception(message);
}

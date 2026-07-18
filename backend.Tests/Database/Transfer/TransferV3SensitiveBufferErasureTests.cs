using System.Security.Cryptography;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SensitiveBufferErasureTests
{
    [Fact]
    public void FrameState_ZeroesEveryIncrementalDigestAfterConvertingIt()
    {
        var captured = new List<byte[]>();
        Action<string, byte[]> observer = (_, digest) =>
        {
            Assert.Contains(digest, current => current != 0);
            captured.Add(digest);
        };
        using var state = new TransferV3FrameState(
            new TransferV3Limits(1024),
            "Erasure",
            observer);
        var cursor = TransferV3CursorCodec.Encode(TransferV3CursorComponent.FromInt64(1));

        state.AcceptHeader(new TransferV3TableHeaderFrame(3, "Erasure"));
        state.AcceptBatchStart(new TransferV3BatchStartFrame("Erasure", 0, null));
        state.AcceptChunkedRowStart(new TransferV3ChunkedRowStartFrame(
            "Erasure", cursor, 1));
        state.AcceptFieldChunk(new TransferV3FieldChunkFrame(
            "Erasure", cursor, 0, 0, new byte[] { 0x2a }));
        _ = state.FinishChunkedRow();
        _ = state.FinishBatch();
        _ = state.FinishTable();

        Assert.Equal(3, captured.Count);
        Assert.All(captured, AssertZeroed);
    }

    [Fact]
    public void FrameState_ZeroesFinalizedDigestWhenTheObservationHookFails()
    {
        byte[]? captured = null;
        Action<string, byte[]> observer = (_, digest) =>
        {
            Assert.Contains(digest, current => current != 0);
            captured = digest;
            throw new InvalidOperationException("synthetic-observer-failure");
        };
        using var state = new TransferV3FrameState(
            new TransferV3Limits(1024),
            "Erasure",
            observer);
        var cursor = TransferV3CursorCodec.Encode(TransferV3CursorComponent.FromInt64(1));
        state.AcceptHeader(new TransferV3TableHeaderFrame(3, "Erasure"));
        state.AcceptBatchStart(new TransferV3BatchStartFrame("Erasure", 0, null));
        state.AcceptChunkedRowStart(new TransferV3ChunkedRowStartFrame(
            "Erasure", cursor, 1));
        state.AcceptFieldChunk(new TransferV3FieldChunkFrame(
            "Erasure", cursor, 0, 0, new byte[] { 0x2a }));

        var failure = Assert.Throws<InvalidOperationException>(() => state.FinishChunkedRow());

        Assert.Equal("synthetic-observer-failure", failure.Message);
        AssertZeroed(Assert.IsType<byte[]>(captured));
    }

    [Fact]
    public async Task ReferenceIndex_ZeroesEveryOwnedParameterCopyOnSuccessAndFailure()
    {
        var captured = new List<(string Kind, byte[] Buffer)>();
        string? faultPoint = null;
        Action<string> faultHook = point =>
        {
            if (string.Equals(point, faultPoint, StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic-index-failure");
        };
        Action<string, byte[]> bufferHook = (kind, buffer) =>
        {
            Assert.Contains(buffer, current => current != 0);
            captured.Add((kind, buffer));
        };
        var hooks = new TransferV3BlobReferenceIndexHooks(faultHook, bufferHook);
        await using var index = await TransferV3BlobReferenceIndex.CreateAsync(hooks);
        var hash = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var uuid = Enumerable.Repeat((byte)0x6b, 16).ToArray();
        var secondUuid = Enumerable.Repeat((byte)0x7c, 16).ToArray();

        index.BeginBatch(3, 0);
        index.AddRowKey(0, hash);
        index.AddUniqueValue(1, 0, hash);
        index.AddUuidValue(2, 0, uuid);
        index.AddHardBlobReference(3, 0, uuid);
        index.AddInformationalFact(4, 0, uuid, secondUuid, 5);
        index.AddPhysicalBlob(uuid, 6, hash);
        index.AddDavMetadata(0, uuid, secondUuid, 7, 8, secondUuid);
        index.AddLegacyMetadata(9, 0, uuid);
        index.AddBootstrapConfigSecret(0, hash);
        index.AddBootstrapRootMarker(0, hash);

        faultPoint = "before-write";
        var writeFailure = Assert.Throws<TransferV3BlobReferenceIndexException>(() =>
            index.AddRowKey(1, hash));
        Assert.Equal("index-write", writeFailure.Code);
        faultPoint = null;
        var constraintFailure = Assert.Throws<TransferV3BlobReferenceIndexException>(() =>
            index.AddRowKey(0, hash));
        Assert.Equal("index-constraint", constraintFailure.Code);
        index.CommitBatch();

        Assert.True(index.ContainsRowKey(3, 0, hash));
        Assert.True(index.ContainsPhysicalBlob(uuid, 6, hash));
        faultPoint = "before-query";
        var queryFailure = Assert.Throws<TransferV3BlobReferenceIndexException>(() =>
            index.ContainsUuidValue(3, 2, 0, uuid));
        Assert.Equal("index-query", queryFailure.Code);

        Assert.Equal(16, captured.Count(entry => entry.Kind == "insert-parameter"));
        Assert.Equal(4, captured.Count(entry => entry.Kind == "query-parameter"));
        Assert.All(captured, entry => AssertZeroed(entry.Buffer));
        Assert.All(hash, current => Assert.Equal((byte)0x5a, current));
        Assert.All(uuid, current => Assert.Equal((byte)0x6b, current));
        Assert.All(secondUuid, current => Assert.Equal((byte)0x7c, current));
    }

    [Fact]
    public async Task TableVerifier_ZeroesRowDigestsAfterSuccessfulSinkDispatch()
    {
        var fixture = await WriteSingleRowTableAsync();
        var sink = new DigestCapturingSink(throwAfterCapture: false);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var result = await TransferV3SnapshotFrameVerifier.VerifyTableAsync(
            source,
            fixture.Contract,
            fixture.TableOrdinal,
            fixture.Manifest,
            fixture.Limits,
            sink);

        Assert.Equal(1, result.Rows);
        Assert.NotEmpty(sink.Captured);
        Assert.All(sink.Captured, AssertZeroed);
        CryptographicOperations.ZeroMemory(result.RawSha256);
    }

    [Fact]
    public async Task TableVerifier_ZeroesRowDigestsWhenSinkDispatchFails()
    {
        var fixture = await WriteSingleRowTableAsync();
        var sink = new DigestCapturingSink(throwAfterCapture: true);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                source,
                fixture.Contract,
                fixture.TableOrdinal,
                fixture.Manifest,
                fixture.Limits,
                sink));

        Assert.Equal("table-verify", failure.Code);
        Assert.NotEmpty(sink.Captured);
        Assert.All(sink.Captured, AssertZeroed);
    }

    [Fact]
    public async Task BlobVerifier_ClassifiesSixtyFourCharacterNonHexInventoryAsBlobManifest()
    {
        var limits = new TransferV3Limits(1024 * 1024);
        await using var bytes = new MemoryStream();
        TransferV3TableEndFrame end;
        await using (var writer = new TransferV3JsonlWriter(bytes, "Blobs", limits))
        {
            await writer.WriteTableHeaderAsync();
            end = await writer.EndTableAsync();
        }
        var manifest = new TransferV3ManifestBlobs(
            "Blobs",
            "Blobs.jsonl",
            end.Batches,
            end.Rows,
            end.Bytes,
            end.Sha256,
            0,
            0,
            new string('g', 64));
        await using var source = new MemoryStream(bytes.ToArray(), writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                source,
                manifest,
                limits,
                new NoOpFactSink()));

        Assert.Equal("blob-manifest", failure.Code);
    }

    private static async Task<TableFixture> WriteSingleRowTableAsync()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = contract.Tables
            .Select((table, ordinal) => (table, ordinal))
            .Single(candidate => string.Equals(
                candidate.table.Name,
                "BlobCleanupItems",
                StringComparison.Ordinal))
            .ordinal;
        var table = contract.Tables[tableOrdinal];
        var limits = new TransferV3Limits(1024 * 1024);
        var id = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
        var cursor = TransferV3CursorCodec.Encode(TransferV3CursorComponent.FromGuid(id));
        var encoded = TransferV3RowCodec.EncodeField(table.Columns[0], id);
        await using var bytes = new MemoryStream();
        TransferV3TableEndFrame end;
        try
        {
            await using var writer = new TransferV3JsonlWriter(bytes, table.Name, limits);
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            await writer.StartChunkedRowAsync(cursor, table.Columns.Count);
            await writer.WriteFieldChunkAsync(0, encoded);
            await writer.EndChunkedRowAsync();
            await writer.EndBatchAsync();
            end = await writer.EndTableAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }

        return new TableFixture(
            bytes.ToArray(),
            contract,
            tableOrdinal,
            new TransferV3ManifestTable(
                table.Name,
                $"table-{tableOrdinal + 1:000}-{table.Name}.jsonl",
                end.Batches,
                end.Rows,
                end.Bytes,
                end.Sha256),
            limits);
    }

    private static void AssertZeroed(byte[] value) =>
        Assert.All(value, current => Assert.Equal((byte)0, current));

    private sealed class DigestCapturingSink(bool throwAfterCapture)
        : ITransferV3VerificationFactSink
    {
        internal List<byte[]> Captured { get; } = [];

        public void BeginBatch(int tableOrdinal, int batchOrdinal)
        {
        }

        public void AddVerifiedRow(TransferV3VerifiedRowFacts facts)
        {
            Capture(facts.KeySha256);
            foreach (var unique in facts.UniqueKeys) Capture(unique.Sha256);
            foreach (var field in facts.Fields) Capture(field.EncodedSha256);
            if (throwAfterCapture) throw new InvalidOperationException("synthetic-sink-failure");
        }

        public void AddPhysicalBlob(
            long rowOrdinal,
            ReadOnlySpan<byte> uuidNetworkBytes,
            long length,
            ReadOnlySpan<byte> contentSha256)
        {
        }

        public void CommitBatch()
        {
        }

        public void AbortBatchNoThrow()
        {
        }

        private void Capture(byte[] value)
        {
            Assert.Contains(value, current => current != 0);
            Captured.Add(value);
        }
    }

    private sealed class NoOpFactSink : ITransferV3VerificationFactSink
    {
        public void BeginBatch(int tableOrdinal, int batchOrdinal)
        {
        }

        public void AddVerifiedRow(TransferV3VerifiedRowFacts facts)
        {
        }

        public void AddPhysicalBlob(
            long rowOrdinal,
            ReadOnlySpan<byte> uuidNetworkBytes,
            long length,
            ReadOnlySpan<byte> contentSha256)
        {
        }

        public void CommitBatch()
        {
        }

        public void AbortBatchNoThrow()
        {
        }
    }

    private sealed record TableFixture(
        byte[] Bytes,
        TransferV3SourceContract Contract,
        int TableOrdinal,
        TransferV3ManifestTable Manifest,
        TransferV3Limits Limits);
}

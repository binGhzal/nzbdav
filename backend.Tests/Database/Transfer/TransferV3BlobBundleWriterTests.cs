using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3BlobBundleWriterTests
{
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task ExportAsync_EmptyInventoryWritesCanonicalEmptyBlobTableDurably()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(1024 * 1024, maxBatchRows: 2, maxBatchBytes: 4 * 1024 * 1024),
            outputs);

        Assert.Equal("Blobs.jsonl", Assert.Single(outputs.CreatedNames));
        var output = outputs.Outputs["Blobs.jsonl"];
        Assert.True(output.DurablyCompleted);
        Assert.True(output.Disposed);
        var frames = ParseFrames(output.Bytes);
        Assert.Collection(
            frames,
            frame =>
            {
                var header = Assert.IsType<TransferV3TableHeaderFrame>(frame);
                Assert.Equal(3, header.Version);
                Assert.Equal("Blobs", header.Table);
            },
            frame =>
            {
                var end = Assert.IsType<TransferV3TableEndFrame>(frame);
                Assert.Equal(0, end.Batches);
                Assert.Equal(0, end.Rows);
                Assert.Equal(0, end.Bytes);
            });
        Assert.Equal("Blobs", result.Blobs.Name);
        Assert.Equal("Blobs.jsonl", result.Blobs.File);
        Assert.Equal(0, result.Blobs.Count);
        Assert.Equal(0, result.Blobs.TotalBytes);
        Assert.Equal(EmptySha256, result.Blobs.InventorySha256);
        Assert.Equal(0, result.Metrics.MaxSourceBufferBytesObserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 + 1)]
    public async Task ExportAsync_WritesCanonicalDescriptorAndBoundedContentChunks(int length)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = Enumerable.Range(0, length)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await source.WriteBlobAsync(id, contents);
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(1024 * 1024, maxBatchRows: 2, maxBatchBytes: 4 * 1024 * 1024),
            outputs);

        var frames = ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes);
        var rowStart = Assert.Single(frames.OfType<TransferV3ChunkedRowStartFrame>());
        Assert.Equal(1 + Math.Max(1, (length + 1024 * 1024 - 1) / (1024 * 1024)), rowStart.Fields);
        var fieldChunks = frames.OfType<TransferV3FieldChunkFrame>().ToArray();
        var descriptor = Assert.Single(fieldChunks, frame => frame.Field == 0);
        Assert.Equal(40, descriptor.Data.Length);
        Assert.Equal(length, BinaryPrimitives.ReadInt64BigEndian(descriptor.Data.Span[..8]));
        Assert.True(SHA256.HashData(contents).AsSpan().SequenceEqual(descriptor.Data.Span[8..]));
        var contentFrames = fieldChunks.Where(frame => frame.Field > 0).ToArray();
        Assert.NotEmpty(contentFrames);
        Assert.All(contentFrames, frame => Assert.InRange(frame.Data.Length, 0, 1024 * 1024));
        Assert.Equal(contents, Concatenate(contentFrames.Select(frame => frame.Data)));
        Assert.Equal(40L + length, result.Blobs.DecodedBytes);
        Assert.Equal(length, result.Blobs.TotalBytes);
        Assert.InRange(result.Metrics.MaxSourceBufferBytesObserved, 0, 1024 * 1024);
        Assert.InRange(result.Metrics.MaxDecodedChunkBytesObserved, 0, 1024 * 1024);
    }

    [Fact]
    public async Task ExportAsync_SplitsContentAtLogicalFieldBoundary()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = Enumerable.Range(0, 81).Select(index => (byte)index).ToArray();
        await source.WriteBlobAsync(id, contents);
        var outputs = new RecordingOutputFactory();

        await ExportAsync(
            source,
            new TransferV3Limits(40, maxBatchRows: 2, maxBatchBytes: 4096),
            outputs);

        var frames = ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes);
        Assert.Equal(4, Assert.Single(frames.OfType<TransferV3ChunkedRowStartFrame>()).Fields);
        var fields = frames.OfType<TransferV3FieldChunkFrame>()
            .Where(frame => frame.Field > 0)
            .GroupBy(frame => frame.Field)
            .Select(group => Concatenate(group.Select(frame => frame.Data)).Length)
            .ToArray();
        Assert.Equal([40, 40, 1], fields);
    }

    [Fact]
    public async Task ExportAsync_RejectsDescriptorThatCannotFitBeforeCreatingOutput()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingOutputFactory();

        var failure = await Assert.ThrowsAsync<TransferV3BlobBundleExportException>(() =>
            ExportAsync(source, new TransferV3Limits(39), outputs));

        Assert.Equal("field-size", failure.Code);
        Assert.Empty(outputs.CreatedNames);
    }

    [Fact]
    public async Task ExportAsync_SplitsOneLargeLogicalFieldIntoBoundedContiguousChunks()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var length = 2 * 1024 * 1024 + 17;
        var contents = Enumerable.Range(0, length)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await source.WriteBlobAsync(id, contents);
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(length, maxBatchRows: 2, maxBatchBytes: length + 40L),
            outputs);

        var frames = ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes);
        Assert.Equal(2, Assert.Single(frames.OfType<TransferV3ChunkedRowStartFrame>()).Fields);
        var chunks = frames.OfType<TransferV3FieldChunkFrame>()
            .Where(frame => frame.Field == 1)
            .ToArray();
        Assert.Equal([0, 1, 2], chunks.Select(frame => frame.Chunk));
        Assert.Equal([1024 * 1024, 1024 * 1024, 17], chunks.Select(frame => frame.Data.Length));
        Assert.Equal(contents, Concatenate(chunks.Select(frame => frame.Data)));
        Assert.Equal(1024 * 1024, result.Metrics.MaxSourceBufferBytesObserved);
        Assert.Equal(1024 * 1024, result.Metrics.MaxDecodedChunkBytesObserved);
    }

    [Fact]
    public async Task ExportAsync_UsesUuidNetworkOrderRegardlessOfCreationOrder()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string first = "10000000-0000-0000-0000-000000000000";
        const string second = "f0000000-0000-0000-0000-000000000000";
        await source.WriteBlobAsync(second, "second"u8.ToArray());
        await source.WriteBlobAsync(first, "first"u8.ToArray());
        var outputs = new RecordingOutputFactory();

        await ExportAsync(
            source,
            new TransferV3Limits(1024, maxBatchRows: 8, maxBatchBytes: 4096),
            outputs);

        var cursors = ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes)
            .OfType<TransferV3ChunkedRowStartFrame>()
            .Select(frame => Assert.Single(TransferV3CursorCodec.Decode(frame.Cursor)).UuidValue)
            .ToArray();
        Assert.Equal([Guid.Parse(first), Guid.Parse(second)], cursors);
    }

    [Fact]
    public async Task ExportAsync_RollsBatchesWithDescriptorInclusiveBytesAndContinuationCursor()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        string[] ids =
        [
            "10000000-0000-0000-0000-000000000000",
            "20000000-0000-0000-0000-000000000000",
            "30000000-0000-0000-0000-000000000000",
        ];
        foreach (var id in ids.Reverse())
            await source.WriteBlobAsync(id, new byte[10]);
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(1024, maxBatchRows: 2, maxBatchBytes: 100),
            outputs);

        var frames = ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes);
        var starts = frames.OfType<TransferV3BatchStartFrame>().ToArray();
        var ends = frames.OfType<TransferV3BatchEndFrame>().ToArray();
        Assert.Equal([0, 1], starts.Select(frame => frame.Batch));
        Assert.Null(starts[0].After);
        Assert.Equal(ends[0].Cursor, starts[1].After);
        Assert.Equal([2, 1], ends.Select(frame => frame.Rows));
        Assert.Equal([100L, 50L], ends.Select(frame => frame.Bytes));
        Assert.Equal(2, result.Blobs.Batches);
        Assert.Equal(3, result.Blobs.Rows);
        Assert.Equal(150, result.Blobs.DecodedBytes);
    }

    [Fact]
    public async Task ExportAsync_AllowsOverBudgetChunkedRowOnlyAsSingleton()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, new byte[100]);
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(1024, maxBatchRows: 8, maxBatchBytes: 50),
            outputs);

        var end = Assert.Single(ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes)
            .OfType<TransferV3BatchEndFrame>());
        Assert.Equal(1, end.Rows);
        Assert.Equal(140, end.Bytes);
        Assert.Equal(1, result.Blobs.Batches);
    }

    [Fact]
    public async Task ExportAsync_DigestsMatchIndependentCanonicalRecomputation()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = "digest-probe"u8.ToArray();
        await source.WriteBlobAsync(id, contents);
        var outputs = new RecordingOutputFactory();

        var result = await ExportAsync(
            source,
            new TransferV3Limits(1024, maxBatchRows: 8, maxBatchBytes: 4096),
            outputs);

        var lines = ParseFrameLines(outputs.Outputs["Blobs.jsonl"].Bytes);
        var chunks = lines.Select(value => value.Frame)
            .OfType<TransferV3FieldChunkFrame>()
            .ToArray();
        using var rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[12];
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteInt32BigEndian(header, chunk.Field);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], chunk.Chunk);
            BinaryPrimitives.WriteInt32BigEndian(header[8..], chunk.Data.Length);
            rowHash.AppendData(header);
            rowHash.AppendData(chunk.Data.Span);
        }
        Assert.Equal(
            Convert.ToHexString(rowHash.GetHashAndReset()).ToLowerInvariant(),
            Assert.Single(lines.Select(value => value.Frame)
                .OfType<TransferV3ChunkedRowEndFrame>()).Sha256);

        var batchEndIndex = lines.FindIndex(value => value.Frame is TransferV3BatchEndFrame);
        var tableEndIndex = lines.FindIndex(value => value.Frame is TransferV3TableEndFrame);
        Assert.True(batchEndIndex > 0);
        Assert.True(tableEndIndex > batchEndIndex);
        Assert.Equal(
            CanonicalLineDigest(lines.Skip(1).Take(batchEndIndex - 1)),
            Assert.IsType<TransferV3BatchEndFrame>(lines[batchEndIndex].Frame).Sha256);
        Assert.Equal(
            CanonicalLineDigest(lines.Take(tableEndIndex)),
            result.Blobs.Sha256);

        Span<byte> networkId = stackalloc byte[16];
        Assert.True(Guid.Parse(id).TryWriteBytes(networkId, bigEndian: true, out var written));
        Assert.Equal(16, written);
        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        inventory.AppendData(networkId);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(lengthBytes, contents.Length);
        inventory.AppendData(lengthBytes);
        inventory.AppendData(SHA256.HashData(contents));
        Assert.Equal(
            Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant(),
            result.Blobs.InventorySha256);
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, false)]
    public async Task ExportAsync_EnforcesContentFieldCeilingBeforeRowStart(
        int contentFields,
        bool accepted)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, new byte[checked(contentFields * 40)]);
        var outputs = new RecordingOutputFactory();
        var limits = new TransferV3Limits(40, maxBatchRows: 2, maxBatchBytes: 128 * 1024);

        if (accepted)
        {
            await ExportAsync(source, limits, outputs);
            Assert.Equal(
                1024,
                Assert.Single(ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes)
                    .OfType<TransferV3ChunkedRowStartFrame>()).Fields);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<TransferV3BlobBundleExportException>(
                () => ExportAsync(source, limits, outputs));
            Assert.Equal("field-count", failure.Code);
            Assert.DoesNotContain(
                ParseFrames(outputs.Outputs["Blobs.jsonl"].Bytes),
                frame => frame is TransferV3ChunkedRowStartFrame);
        }
    }

    [Fact]
    public async Task ExportAsync_RejectsInPlaceMutationAfterContentRead()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var original = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        await source.WriteBlobAsync(id, original);
        var mutated = false;
        var hooks = new TransferV3BlobBundleWriterHooks(point =>
        {
            if (mutated || point != TransferV3BlobBundleFaultPoint.AfterFirstContentChunk)
                return;
            mutated = true;
            File.WriteAllBytes(source.BlobPath(id), Enumerable.Repeat((byte)'b', original.Length).ToArray());
        });

        var failure = await Assert.ThrowsAsync<TransferV3BlobBundleExportException>(() =>
            ExportAsync(
                source,
                new TransferV3Limits(1024 * 1024),
                new RecordingOutputFactory(),
                hooks));

        Assert.True(mutated);
        Assert.Equal("source-mutated", failure.Code);
        Assert.DoesNotContain(id, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_CancellationAfterFirstChunkPreservesCancellation()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
        using var cancellation = new CancellationTokenSource();
        var hooks = new TransferV3BlobBundleWriterHooks(point =>
        {
            if (point == TransferV3BlobBundleFaultPoint.AfterFirstContentChunk)
                cancellation.Cancel();
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExportAsync(
                source,
                new TransferV3Limits(1024 * 1024),
                new RecordingOutputFactory(),
                hooks,
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("second")]
    [InlineData("first")]
    public async Task ExportAsync_RejectsSameBytesAtReplacedFileOrShardIdentity(string replacement)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        await source.WriteBlobAsync(id, contents);
        var first = Path.Combine(source.BlobRootPath, "aa");
        var second = Path.Combine(first, "aa");
        var file = source.BlobPath(id);
        var outside = Path.Combine(Path.GetDirectoryName(source.BlobRootPath)!, $"moved-{replacement}");
        var mutated = false;
        var hooks = new TransferV3BlobBundleWriterHooks(point =>
        {
            if (mutated || point != TransferV3BlobBundleFaultPoint.AfterFirstContentChunk)
                return;
            mutated = true;
            switch (replacement)
            {
                case "file":
                    File.Move(file, outside);
                    File.WriteAllBytes(file, contents);
                    break;
                case "second":
                    Directory.Move(second, outside);
                    Directory.CreateDirectory(second);
                    File.WriteAllBytes(file, contents);
                    break;
                case "first":
                    Directory.Move(first, outside);
                    Directory.CreateDirectory(second);
                    File.WriteAllBytes(file, contents);
                    break;
            }
        });

        var failure = await Assert.ThrowsAsync<TransferV3BlobBundleExportException>(() =>
            ExportAsync(
                source,
                new TransferV3Limits(1024 * 1024),
                new RecordingOutputFactory(),
                hooks));

        Assert.True(mutated);
        Assert.Equal("source-mutated", failure.Code);
        Assert.DoesNotContain(id, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("write", "export")]
    [InlineData("durable", "output-durable-close")]
    [InlineData("dispose", "output-dispose")]
    public async Task ExportAsync_OutputFailuresAreSanitizedAndAlwaysDispose(
        string stage,
        string expectedCode)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.WriteBlobAsync(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "output-failure"u8.ToArray());
        var outputs = new FaultingOutputFactory(stage);

        var failure = await Assert.ThrowsAsync<TransferV3BlobBundleExportException>(() =>
            ExportAsync(source, new TransferV3Limits(1024), outputs));

        Assert.Equal(expectedCode, failure.Code);
        Assert.DoesNotContain("private-output-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.True(outputs.Output.DisposeCalls > 0);
        if (stage == "dispose")
            Assert.Contains("output-dispose-failed", failure.CleanupCodes);
    }

    [Fact]
    public void WriterSource_DoesNotUseRuntimeBlobStoreOrWholeFileHelpers()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3BlobBundleWriter.cs"));

        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEnd", source, StringComparison.Ordinal);
    }

    private static async Task<TransferV3BlobBundleExportResult> ExportAsync(
        TransferV3ValidationSource source,
        TransferV3Limits limits,
        ITransferV3TableOutputFactory outputs,
        TransferV3BlobBundleWriterHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance,
                cancellationToken);
        return await session.RunExportAsync(
            (context, token) => new TransferV3BlobBundleWriter(hooks)
                .ExportAsync(context, limits, outputs, token),
            cancellationToken);
    }

    private static TransferV3SourceProvenance CaptureProvenance(
        TransferV3ValidationSource source)
    {
        using var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        return new TransferV3SourceProvenance(
            database.Identity,
            blobs.Identity,
            TimeZoneInfo.Utc.Id);
    }

    private static IReadOnlyList<TransferV3Frame> ParseFrames(byte[] bytes)
    {
        var frames = new List<TransferV3Frame>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            frames.Add(TransferV3FrameCodec.ParseCanonical(bytes.AsMemory(start, index - start)));
            start = index + 1;
        }
        Assert.Equal(bytes.Length, start);
        return frames;
    }

    private static List<FrameLine> ParseFrameLines(byte[] bytes)
    {
        var lines = new List<FrameLine>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            var line = bytes.AsSpan(start, index - start).ToArray();
            lines.Add(new FrameLine(TransferV3FrameCodec.ParseCanonical(line), line));
            start = index + 1;
        }
        Assert.Equal(bytes.Length, start);
        return lines;
    }

    private static string CanonicalLineDigest(IEnumerable<FrameLine> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
        {
            hash.AppendData(line.Bytes);
            hash.AppendData([(byte)'\n']);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] Concatenate(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        using var output = new MemoryStream();
        foreach (var chunk in chunks) output.Write(chunk.Span);
        return output.ToArray();
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }

    private sealed class RecordingOutputFactory : ITransferV3TableOutputFactory
    {
        private readonly List<string> _createdNames = [];
        private readonly Dictionary<string, RecordingOutput> _outputs =
            new(StringComparer.Ordinal);

        internal ImmutableArray<string> CreatedNames => [.. _createdNames];
        internal IReadOnlyDictionary<string, RecordingOutput> Outputs => _outputs;

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = new RecordingOutput();
            if (!_outputs.TryAdd(fileName, output)) throw new IOException("duplicate-output");
            _createdNames.Add(fileName);
            return ValueTask.FromResult<ITransferV3TableOutput>(output);
        }
    }

    private sealed class RecordingOutput : ITransferV3TableOutput
    {
        private readonly MemoryStream _stream = new();

        internal byte[] Bytes => _stream.ToArray();
        internal bool DurablyCompleted { get; private set; }
        internal bool Disposed { get; private set; }
        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurablyCompleted = true;
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingOutputFactory(string stage) : ITransferV3TableOutputFactory
    {
        internal FaultingOutput Output { get; } = new(stage);

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ITransferV3TableOutput>(Output);
        }
    }

    private sealed class FaultingOutput(string stage) : ITransferV3TableOutput
    {
        private readonly Stream _stream = stage == "write"
            ? new FaultingWriteStream()
            : new MemoryStream();

        internal int DisposeCalls { get; private set; }
        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == "durable") throw new IOException("private-output-secret");
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (stage == "dispose") throw new IOException("private-output-secret");
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("private-output-secret"));
    }

    private sealed record FrameLine(TransferV3Frame Frame, byte[] Bytes);
}

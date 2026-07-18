using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3JsonlWriterHardeningTests
{
    [Fact]
    public async Task ConcurrentCalls_AreSerializedAcrossStateAndPhysicalWrites()
    {
        await using var destination = new GatedMemoryStream();
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));

        var header = writer.WriteTableHeaderAsync().AsTask();
        await destination.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var batch = writer.StartBatchAsync(0, null).AsTask();
        await Task.Delay(25);

        Assert.Equal(1, destination.MaxConcurrentWrites);
        destination.ReleaseFirstWrite.TrySetResult();
        await Task.WhenAll(header, batch);
        Assert.Equal(1, destination.MaxConcurrentWrites);
    }

    [Fact]
    public async Task PreCanceledWrite_FaultsWriterBeforeAnyContinuation()
    {
        await using var destination = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteTableHeaderAsync(cancellation.Token).AsTask());
        var lengthAfterFailure = destination.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.StartBatchAsync(0, null).AsTask());
        Assert.Equal(lengthAfterFailure, destination.Length);
    }

    [Fact]
    public async Task PartialThrowingWrite_FaultsStateBeforeWriteAndCannotContinue()
    {
        await using var destination = new PartialThenThrowStream();
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));

        await Assert.ThrowsAsync<IOException>(() => writer.WriteTableHeaderAsync().AsTask());
        Assert.True(destination.Length > 0);
        var partialLength = destination.Length;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.StartBatchAsync(0, null).AsTask());
        Assert.Equal(partialLength, destination.Length);
    }

    [Fact]
    public async Task ValidationFailure_IsTerminalBecauseStateMutatesBeforePhysicalWrite()
    {
        await using var destination = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            writer.WriteRowAsync("not-a-cursor", new byte[] { 1 }).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteRowAsync(Cursor(1), new byte[] { 1 }).AsTask());
    }

    [Fact]
    public async Task ZeroLengthChunk_IsAllowedOnlyAsChunkZeroOfAnEmptyField()
    {
        await using var accepted = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(
                         accepted,
                         "Items",
                         new TransferV3Limits(2 * 1024 * 1024)))
        {
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            await writer.StartChunkedRowAsync(Cursor(1), 1);
            await writer.WriteFieldChunkAsync(0, ReadOnlyMemory<byte>.Empty);
            await writer.EndChunkedRowAsync();
            await writer.EndBatchAsync();
            await writer.EndTableAsync();
        }

        await using var rejected = new MemoryStream();
        await using var rejectedWriter = new TransferV3JsonlWriter(
            rejected,
            "Items",
            new TransferV3Limits(2 * 1024 * 1024));
        await rejectedWriter.WriteTableHeaderAsync();
        await rejectedWriter.StartBatchAsync(0, null);
        await rejectedWriter.StartChunkedRowAsync(Cursor(1), 1);
        await rejectedWriter.WriteFieldChunkAsync(
            0,
            new byte[TransferV3Limits.MaxDecodedChunkBytes]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rejectedWriter.WriteFieldChunkAsync(0, ReadOnlyMemory<byte>.Empty).AsTask());
    }

    [Fact]
    public async Task Disposal_DoesNotCloseDestinationAndRejectsFurtherCalls()
    {
        await using var destination = new MemoryStream();
        var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));
        await writer.WriteTableHeaderAsync();

        await writer.DisposeAsync();

        Assert.True(destination.CanWrite);
        destination.WriteByte(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            writer.StartBatchAsync(0, null).AsTask());
    }

    [Fact]
    public void FrameCodec_ReportsEveryLargeSerializationBuffer()
    {
        var payload = new byte[TransferV3Limits.MaxDecodedChunkBytes];
        var frame = new TransferV3FieldChunkFrame(
            "QueueNzbContents",
            Cursor(1),
            Field: 1,
            Chunk: 0,
            payload);

        var serialized = TransferV3FrameCodec.SerializeMeasured(frame);
        try
        {
            var paddedBase64Characters = checked(((payload.Length + 2) / 3) * 4);
            Assert.Equal(
                checked(paddedBase64Characters * sizeof(char)),
                serialized.Metrics.Base64Utf16Bytes);
            Assert.True(
                serialized.Metrics.ArrayBufferWriterCapacityBytes
                >= serialized.Metrics.SerializedBytes);
            Assert.Equal(
                serialized.Metrics.ArrayBufferWriterInitialCapacityBytes,
                serialized.Metrics.ArrayBufferWriterCapacityBytes);
            Assert.Equal(serialized.Bytes.Length, serialized.Metrics.SerializedBytes);
            Assert.Equal(
                Math.Max(
                    serialized.Metrics.Base64Utf16Bytes,
                    Math.Max(
                        serialized.Metrics.ArrayBufferWriterCapacityBytes,
                        serialized.Metrics.SerializedBytes)),
                serialized.Metrics.MaxManagedBufferBytesObserved);
            Assert.True(
                serialized.Metrics.MaxManagedBufferBytesObserved
                > TransferV3Limits.MaxDecodedChunkBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized.Bytes);
        }
    }

    [Fact]
    public void FrameCodec_DoesNotGrowSerializationBufferForAnyFrameShape()
    {
        var cursor = Cursor(1);
        var digest = new string('0', 64);
        TransferV3Frame[] frames =
        [
            new TransferV3TableHeaderFrame(3, "Items"),
            new TransferV3BatchStartFrame("Items", 0, cursor),
            new TransferV3RowFrame("Items", cursor, new byte[] { 1 }),
            new TransferV3ChunkedRowStartFrame("Items", cursor, 1),
            new TransferV3FieldChunkFrame("Items", cursor, 0, 0, new byte[] { 1 }),
            new TransferV3ChunkedRowEndFrame("Items", cursor, 1, 1, digest),
            new TransferV3BatchEndFrame("Items", 0, 1, 1, cursor, digest),
            new TransferV3TableEndFrame("Items", 1, 1, 1, digest),
        ];

        foreach (var frame in frames)
        {
            var serialized = TransferV3FrameCodec.SerializeMeasured(frame);
            try
            {
                Assert.Equal(
                    serialized.Metrics.ArrayBufferWriterInitialCapacityBytes,
                    serialized.Metrics.ArrayBufferWriterCapacityBytes);
                Assert.True(
                    serialized.Metrics.ArrayBufferWriterCapacityBytes
                    >= serialized.Metrics.SerializedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized.Bytes);
            }
        }
    }

    [Fact]
    public async Task Writer_ReportsAllThreeSerializationBuffersForEachFrame()
    {
        await using var destination = new MemoryStream();
        var observed = new List<int>();
        var limits = new TransferV3Limits(2 * 1024 * 1024);
        var payload = new byte[TransferV3Limits.MaxDecodedChunkBytes];
        var cursor = Cursor(1);
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "Items",
            limits,
            observed.Add);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.StartChunkedRowAsync(cursor, 1);
        observed.Clear();

        await writer.WriteFieldChunkAsync(0, payload);

        var serialized = TransferV3FrameCodec.SerializeMeasured(
            new TransferV3FieldChunkFrame("Items", cursor, 0, 0, payload));
        try
        {
            Assert.Equal(
                [
                    serialized.Metrics.Base64Utf16Bytes,
                    serialized.Metrics.ArrayBufferWriterCapacityBytes,
                    serialized.Metrics.SerializedBytes,
                ],
                observed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized.Bytes);
        }
    }

    [Fact]
    public async Task SuccessfulWrite_ZeroesSerializedFrameArrayAfterDestinationConsumesIt()
    {
        await using var destination = new RetainingWriteStream();
        await using var writer = new TransferV3JsonlWriter(
            destination,
            "DavItems",
            new TransferV3Limits(1024));

        await writer.WriteTableHeaderAsync();

        Assert.NotNull(destination.RetainedFrameArray);
        Assert.All(destination.RetainedFrameArray, value => Assert.Equal(0, value));
        Assert.Contains(
            "\"frame\":\"table\"",
            Encoding.UTF8.GetString(destination.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FrameCodecSource_ZeroesParserCanonicalSerialization()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3FrameCodec.cs"));

        Assert.Contains(
            "CryptographicOperations.ZeroMemory(canonical)",
            source,
            StringComparison.Ordinal);
    }

    private static string Cursor(long value) => TransferV3CursorCodec.Encode(
        TransferV3CursorComponent.FromInt64(value));

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

    private sealed class GatedMemoryStream : MemoryStream
    {
        private int _activeWrites;
        private int _writeCalls;

        internal TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int MaxConcurrentWrites { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeWrites);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, active);
            var call = Interlocked.Increment(ref _writeCalls);
            try
            {
                if (call == 1)
                {
                    FirstWriteStarted.TrySetResult();
                    await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
                }

                await base.WriteAsync(buffer, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }
    }

    private sealed class PartialThenThrowStream : MemoryStream
    {
        private bool _failed;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                await base.WriteAsync(buffer[..Math.Max(1, buffer.Length / 2)], cancellationToken);
                throw new IOException("Injected partial write failure.");
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class RetainingWriteStream : MemoryStream
    {
        internal byte[]? RetainedFrameArray { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length > 1
                && RetainedFrameArray is null
                && MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                RetainedFrameArray = segment.Array;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}

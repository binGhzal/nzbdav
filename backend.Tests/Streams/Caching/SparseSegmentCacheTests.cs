using NzbWebDAV.Streams;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Streams.Caching;

public sealed class SparseSegmentCacheTests
{
    [Fact]
    public async Task ReadAtAsyncReusesCachedChunkForOverlappingReads()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3, 4, 5, 6, 7]);
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);

        try
        {
            var first = new byte[2];
            var second = new byte[2];

            Assert.Equal(2, await cached.ReadAtAsync(1, first, CancellationToken.None));
            Assert.Equal(2, await cached.ReadAtAsync(2, second, CancellationToken.None));

            Assert.Equal([1, 2], first);
            Assert.Equal([2, 3], second);
            Assert.Equal(1, inner.ReadCalls);
            Assert.Equal(1, manager.GetSnapshot().Hits);
            Assert.Equal(1, manager.GetSnapshot().Misses);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task ReadAtAsyncDeduplicatesConcurrentFetchesForSameChunk()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3], delay: TimeSpan.FromMilliseconds(100));
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);

        try
        {
            var first = new byte[2];
            var second = new byte[2];

            await Task.WhenAll(
                cached.ReadAtAsync(0, first, CancellationToken.None).AsTask(),
                cached.ReadAtAsync(1, second, CancellationToken.None).AsTask());

            Assert.Equal([0, 1], first);
            Assert.Equal([1, 2], second);
            Assert.Equal(1, inner.ReadCalls);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task CacheEvictsUnpinnedFilesWhenBudgetIsExceeded()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var firstInner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var secondInner = new ByteArrayRangeReader([4, 5, 6, 7]);

        var first = Open(manager, "file-a", firstInner, tempDir.Path, chunkBytes: 4, maxBytes: 4);
        try
        {
            var buffer = new byte[4];
            Assert.Equal(4, await first.ReadAtAsync(0, buffer, CancellationToken.None));
        }
        finally
        {
            await DisposeReaderAsync(first);
        }

        var second = Open(manager, "file-b", secondInner, tempDir.Path, chunkBytes: 4, maxBytes: 4);
        try
        {
            var buffer = new byte[4];
            Assert.Equal(4, await second.ReadAtAsync(0, buffer, CancellationToken.None));
        }
        finally
        {
            await DisposeReaderAsync(second);
        }

        var snapshot = manager.GetSnapshot();
        Assert.True(snapshot.Bytes <= 4);
        Assert.Equal(1, snapshot.Evictions);
    }

    [Fact]
    public async Task OpeningCacheCleansOrphanTemporaryFiles()
    {
        using var tempDir = new TempDirectory();
        var orphan = Path.Join(tempDir.Path, "orphan.nzbdav-cache.tmp");
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);

        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);

        try
        {
            Assert.False(File.Exists(orphan));
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task NzbFileStreamUsesSparseCacheForSeekBackIntoCachedChunk()
    {
        using var tempDir = new TempDirectory();
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0)
            .AddSegment("segment-2", [3, 4, 5], partOffset: 3);
        var options = CreateOptions(tempDir.Path, chunkBytes: 3, maxBytes: 1024);
        await using var stream = new NzbFileStream(
            ["segment-1", "segment-2"],
            fileSize: 6,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: options);

        var first = new byte[2];
        var second = new byte[2];

        Assert.Equal(2, await stream.ReadAsync(first, CancellationToken.None));
        var decodedBodyCallsAfterFirstRead = client.DecodedBodyCallCount;
        stream.Seek(1, SeekOrigin.Begin);
        Assert.Equal(2, await stream.ReadAsync(second, CancellationToken.None));

        Assert.Equal([0, 1], first);
        Assert.Equal([1, 2], second);
        Assert.Equal(decodedBodyCallsAfterFirstRead, client.DecodedBodyCallCount);
    }

    [Fact]
    public async Task NzbFileStreamStopsCachedReadsAtRequestedEndByte()
    {
        using var tempDir = new TempDirectory();
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0)
            .AddSegment("segment-2", [3, 4, 5], partOffset: 3);
        var options = CreateOptions(tempDir.Path, chunkBytes: 3, maxBytes: 1024);
        await using var stream = new NzbFileStream(
            ["segment-1", "segment-2"],
            fileSize: 6,
            client,
            articleBufferSize: 1,
            requestedEndByte: 2,
            cacheOptions: options);

        var buffer = new byte[6];

        Assert.Equal(3, await stream.ReadAsync(buffer, CancellationToken.None));
        Assert.Equal(0, await stream.ReadAsync(buffer, CancellationToken.None));
        Assert.Equal([0, 1, 2], buffer[..3]);
    }

    [Fact]
    public async Task NzbFileStreamFailsBeforeFetchingKnownMissingSegment()
    {
        var missingSegment = $"missing-{Guid.NewGuid():N}";
        HealthCheckService.RememberMissingSegmentId(missingSegment);
        using var client = new FakeNntpClient()
            .AddSegment(missingSegment, [0, 1, 2], partOffset: 0);
        await using var stream = new NzbFileStream(
            [missingSegment],
            fileSize: 3,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: null);

        var buffer = new byte[1];

        var exception = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            () => stream.ReadAsync(buffer, CancellationToken.None).AsTask());
        Assert.Equal(missingSegment, exception.SegmentId);
        Assert.Equal(0, client.DecodedBodyCallCount);
    }

    [Fact]
    public async Task CacheEvictsAfterLastReaderReleasesWhenFileExceededBudgetWhilePinned()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 2);
        var buffer = new byte[4];

        Assert.Equal(4, await cached.ReadAtAsync(0, buffer, CancellationToken.None));
        Assert.True(manager.GetSnapshot().Bytes > 2);

        await DisposeReaderAsync(cached);

        Assert.Equal(0, manager.GetSnapshot().Bytes);
        Assert.Equal(1, manager.GetSnapshot().Evictions);
    }

    [Fact]
    public async Task OpenPinsNewFileBeforeBudgetEvictionRuns()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var firstInner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var secondInner = new ByteArrayRangeReader([4, 5, 6, 7]);
        var first = Open(manager, "file-a", firstInner, tempDir.Path, chunkBytes: 4, maxBytes: 2);
        var second = default(IFileRangeReader?);

        try
        {
            var firstBuffer = new byte[4];
            Assert.Equal(4, await first.ReadAtAsync(0, firstBuffer, CancellationToken.None));
            Assert.True(manager.GetSnapshot().Bytes > 2);

            second = Open(manager, "file-b", secondInner, tempDir.Path, chunkBytes: 4, maxBytes: 2);

            var secondBuffer = new byte[4];
            Assert.Equal(4, await second.ReadAtAsync(0, secondBuffer, CancellationToken.None));
            Assert.Equal([4, 5, 6, 7], secondBuffer);
        }
        finally
        {
            if (second != null) await DisposeReaderAsync(second);
            await DisposeReaderAsync(first);
        }
    }

    [Fact]
    public async Task ReadAheadDoesNotFetchBeyondRequestedReadLimit()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3, 4, 5, 6, 7]);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024) with
        {
            ReadAheadBytes = 8
        };
        var cached = manager.Open("file-a", inner, options, readLimitExclusive: 4);
        var buffer = new byte[1];

        try
        {
            Assert.Equal(1, await cached.ReadAtAsync(0, buffer, CancellationToken.None));
            await Task.Delay(100);
            Assert.Equal(1, inner.ReadCalls);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task ReadAheadUsesGlobalSpeculativeFetchLimit()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager(maxReadAheadTasks: 1);
        var options = CreateOptions(tempDir.Path, chunkBytes: 2, maxBytes: 1024) with
        {
            ReadAheadBytes = 2
        };
        var firstInner = new BlockingReadAheadRangeReader([0, 1, 2, 3, 4, 5]);
        var secondInner = new BlockingReadAheadRangeReader([6, 7, 8, 9, 10, 11]);
        var first = manager.Open("file-a", firstInner, options);
        var second = manager.Open("file-b", secondInner, options);

        try
        {
            var firstBuffer = new byte[1];
            var secondBuffer = new byte[1];

            Assert.Equal(1, await first.ReadAtAsync(0, firstBuffer, CancellationToken.None));
            await WaitUntilAsync(() => manager.GetSnapshot().ReadAheadActive == 1);

            Assert.Equal(1, await second.ReadAtAsync(0, secondBuffer, CancellationToken.None));
            await Task.Delay(100);

            var snapshot = manager.GetSnapshot();
            Assert.Equal(1, snapshot.ReadAheadActive);
            Assert.Equal(1, snapshot.PendingFetches);
            Assert.Equal(0, secondInner.BlockedReadAheadCalls);

            firstInner.ReleaseReadAhead();
            await WaitUntilAsync(() => manager.GetSnapshot().ReadAheadActive == 0);
        }
        finally
        {
            firstInner.ReleaseReadAhead();
            secondInner.ReleaseReadAhead();
            await DisposeReaderAsync(second);
            await DisposeReaderAsync(first);
        }
    }

    [Fact]
    public async Task CancelledOnlyWaiterDoesNotLeaveCompletedFetchPending()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3], delay: TimeSpan.FromMilliseconds(100));
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var buffer = new byte[2];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cached.ReadAtAsync(0, buffer, cts.Token).AsTask());

            await WaitUntilAsync(() => manager.GetSnapshot().PendingFetches == 0);

            var snapshot = manager.GetSnapshot();
            Assert.Equal(0, snapshot.PendingFetches);
            Assert.Equal(4, snapshot.Bytes);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedFetchForLaterReaders()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3], delay: TimeSpan.FromMilliseconds(100));
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var cancelledBuffer = new byte[2];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cached.ReadAtAsync(0, cancelledBuffer, cts.Token).AsTask());

            var buffer = new byte[2];
            Assert.Equal(2, await cached.ReadAtAsync(1, buffer, CancellationToken.None));
            Assert.Equal([1, 2], buffer);
            Assert.Equal(1, inner.ReadCalls);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task NoProgressTimeoutIsReportedAsRetryableDownloadFailure()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new NeverCompletingRangeReader(length: 4);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024) with
        {
            NoProgressTimeout = TimeSpan.FromMilliseconds(20)
        };
        var cached = manager.Open("file-a", inner, options);
        var buffer = new byte[2];

        try
        {
            var exception = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
                cached.ReadAtAsync(0, buffer, CancellationToken.None).AsTask());
            Assert.Contains("No progress reading cache chunk", exception.Message);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task SameContentKeyUsesDifferentEntriesForDifferentCacheDirectories()
    {
        using var firstTempDir = new TempDirectory();
        using var secondTempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var firstInner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var secondInner = new ByteArrayRangeReader([0, 1, 2, 3]);

        var first = Open(manager, "file-a", firstInner, firstTempDir.Path, chunkBytes: 4, maxBytes: 1024);
        try
        {
            var buffer = new byte[4];
            Assert.Equal(4, await first.ReadAtAsync(0, buffer, CancellationToken.None));
        }
        finally
        {
            await DisposeReaderAsync(first);
        }

        var second = Open(manager, "file-a", secondInner, secondTempDir.Path, chunkBytes: 4, maxBytes: 1024);
        try
        {
            var buffer = new byte[4];
            Assert.Equal(4, await second.ReadAtAsync(0, buffer, CancellationToken.None));
        }
        finally
        {
            await DisposeReaderAsync(second);
        }

        Assert.Equal(1, firstInner.ReadCalls);
        Assert.Equal(1, secondInner.ReadCalls);
    }

    private static IFileRangeReader Open(
        SparseSegmentCacheManager manager,
        string key,
        IFileRangeReader inner,
        string directory,
        int chunkBytes,
        long maxBytes)
    {
        return manager.Open(key, inner, CreateOptions(directory, chunkBytes, maxBytes));
    }

    private static SparseSegmentCacheOptions CreateOptions(string directory, int chunkBytes, long maxBytes)
    {
        return new SparseSegmentCacheOptions
        {
            Enabled = true,
            Directory = directory,
            ChunkBytes = chunkBytes,
            MaxBytes = maxBytes,
            ReadAheadBytes = 0,
            IdleTtl = TimeSpan.FromHours(1),
            NoProgressTimeout = TimeSpan.FromSeconds(5)
        };
    }

    private static async ValueTask DisposeReaderAsync(IFileRangeReader reader)
    {
        if (reader is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (reader is IDisposable disposable)
            disposable.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private sealed class ByteArrayRangeReader(byte[] bytes, TimeSpan delay = default) : IFileRangeReader
    {
        private int _readCalls;
        public long Length => bytes.LongLength;
        public int ReadCalls => Volatile.Read(ref _readCalls);

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCalls);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            if (offset >= bytes.Length) return 0;

            var count = Math.Min(buffer.Length, bytes.Length - (int)offset);
            bytes.AsMemory((int)offset, count).CopyTo(buffer);
            return count;
        }
    }

    private sealed class NeverCompletingRangeReader(long length) : IFileRangeReader
    {
        public long Length { get; } = length;

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }

    private sealed class BlockingReadAheadRangeReader(byte[] bytes) : IFileRangeReader
    {
        private readonly TaskCompletionSource _readAheadGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockedReadAheadCalls;

        public long Length => bytes.LongLength;
        public int BlockedReadAheadCalls => Volatile.Read(ref _blockedReadAheadCalls);

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset >= 2)
            {
                Interlocked.Increment(ref _blockedReadAheadCalls);
                await _readAheadGate.Task.WaitAsync(ct);
            }

            if (offset >= bytes.Length) return 0;

            var count = Math.Min(buffer.Length, bytes.Length - (int)offset);
            bytes.AsMemory((int)offset, count).CopyTo(buffer);
            return count;
        }

        public void ReleaseReadAhead()
        {
            _readAheadGate.TrySetResult();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "nzbdav-tests",
            "sparse-cache",
            Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

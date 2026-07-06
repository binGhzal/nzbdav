using NzbWebDAV.Streams;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using System.Reflection;
using UsenetSharp.Models;

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
    public async Task ReadAtAsyncRefetchesCompletedChunkWhenCacheFileIsTruncated()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3]);
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);

        try
        {
            var first = new byte[4];
            var second = new byte[4];

            Assert.Equal(4, await cached.ReadAtAsync(0, first, CancellationToken.None));
            TruncateCacheFile(cached, length: 1);

            Assert.Equal(4, await cached.ReadAtAsync(0, second, CancellationToken.None));

            Assert.Equal([0, 1, 2, 3], second);
            Assert.Equal(2, inner.ReadCalls);
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
    public void OrphanCleanupIgnoresMissingCacheDirectory()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var missingDirectory = Path.Join(tempDir.Path, "missing-cache");

        InvokeCleanupOrphanFilesOnce(manager, missingDirectory);
    }

    [Fact]
    public void OpenFallsBackToInnerReaderWhenCacheDirectoryCannotBeCreated()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var fileInsteadOfDirectory = Path.Join(tempDir.Path, "cache-file");
        File.WriteAllText(fileInsteadOfDirectory, "not a directory");
        var inner = new ByteArrayRangeReader([0, 1, 2, 3]);

        var reader = manager.Open(
            "file-a",
            inner,
            CreateOptions(fileInsteadOfDirectory, chunkBytes: 4, maxBytes: 1024));

        Assert.Same(inner, reader);
    }

    [Fact]
    public void OpenFallsBackToInnerReaderWhenCacheFilePathCannotBeOpened()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var inner = new ByteArrayRangeReader([0, 1, 2, 3]);
        Directory.CreateDirectory(GetCachePath("file-a", options));

        var reader = manager.Open("file-a", inner, options);

        Assert.Same(inner, reader);
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
    public async Task NzbFileStreamRejectsNegativeSeekWithoutChangingPosition()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0);
        await using var stream = new NzbFileStream(
            ["segment-1"],
            fileSize: 3,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: null);

        stream.Seek(1, SeekOrigin.Begin);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-2, SeekOrigin.Current));
        Assert.Equal(1, stream.Position);
    }

    [Fact]
    public async Task SegmentFileRangeReaderReadsFullRangeWhenSegmentSizesAreSkewed()
    {
        using var client = new FakeNntpClient();
        var segmentIds = new List<string>();
        var expected = Enumerable.Range(0, 20).Select(x => (byte)x).ToArray();
        long partOffset = 0;
        for (var i = 0; i < 10; i++)
        {
            var segmentId = $"small-segment-{i}";
            segmentIds.Add(segmentId);
            client.AddSegment(segmentId, [(byte)i], partOffset);
            partOffset++;
        }

        var largeSegment = Enumerable.Range(10, 90).Select(x => (byte)x).ToArray();
        segmentIds.Add("large-segment");
        client.AddSegment("large-segment", largeSegment, partOffset);

        var reader = new SegmentFileRangeReader(
            segmentIds.ToArray(),
            fileSize: 100,
            client,
            articleBufferSize: 1);
        var buffer = new byte[20];

        var read = await reader.ReadAtAsync(0, buffer, CancellationToken.None);

        Assert.Equal(20, read);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public async Task SegmentFileRangeReaderThrowsWhenSegmentEndsBeforeRequestedRange()
    {
        using var client = new TruncatedSegmentNntpClient();
        var reader = new SegmentFileRangeReader(
            ["segment-1"],
            fileSize: 4,
            client,
            articleBufferSize: 0);
        var buffer = new byte[4];

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            reader.ReadAtAsync(0, buffer, CancellationToken.None).AsTask());
        Assert.Contains("ended before satisfying range read", exception.Message);
    }

    [Fact]
    public async Task NzbFileStreamThrowsWhenDirectSegmentEndsBeforeDeclaredLength()
    {
        using var client = new TruncatedSegmentNntpClient();
        await using var stream = new NzbFileStream(
            ["segment-1"],
            fileSize: 4,
            client,
            articleBufferSize: 0,
            requestedEndByte: null,
            cacheOptions: null);
        var buffer = new byte[4];

        Assert.Equal(1, await stream.ReadAsync(buffer, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            stream.ReadAsync(buffer.AsMemory(1), CancellationToken.None).AsTask());
        Assert.Contains("ended before declared file length", exception.Message);
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
    public async Task NzbFileStreamRejectsNonEmptyFileWithoutSegmentMetadata()
    {
        using var client = new FakeNntpClient();
        await using var stream = new NzbFileStream(
            [],
            fileSize: 3,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: null);

        var buffer = new byte[1];

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => stream.ReadAsync(buffer, CancellationToken.None).AsTask());
        Assert.Contains("segment metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DecodedBodyCallCount);
        Assert.Equal(0, client.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task NzbFileStreamRejectsNonEmptyFileWithNullSegmentMetadata()
    {
        using var client = new FakeNntpClient();
        await using var stream = new NzbFileStream(
            null!,
            fileSize: 3,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: null);

        var buffer = new byte[1];

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => stream.ReadAsync(buffer, CancellationToken.None).AsTask());
        Assert.Contains("segment metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DecodedBodyCallCount);
        Assert.Equal(0, client.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task NzbFileStreamRejectsNonEmptyFileWithBlankSegmentMetadata()
    {
        using var client = new FakeNntpClient();
        await using var stream = new NzbFileStream(
            ["", " "],
            fileSize: 3,
            client,
            articleBufferSize: 1,
            requestedEndByte: null,
            cacheOptions: null);

        var buffer = new byte[1];

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => stream.ReadAsync(buffer, CancellationToken.None).AsTask());
        Assert.Contains("segment metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DecodedBodyCallCount);
        Assert.Equal(0, client.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task SegmentFileRangeReaderCachesYencHeadersAcrossRepeatedSeeks()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0)
            .AddSegment("segment-2", [3, 4, 5], partOffset: 3)
            .AddSegment("segment-3", [6, 7, 8], partOffset: 6);
        var reader = new SegmentFileRangeReader(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 9,
            client,
            articleBufferSize: 1);
        var first = new byte[1];
        var second = new byte[1];

        Assert.Equal(1, await reader.ReadAtAsync(4, first, CancellationToken.None));
        Assert.Equal(1, await reader.ReadAtAsync(5, second, CancellationToken.None));

        Assert.Equal([4], first);
        Assert.Equal([5], second);
        Assert.Equal(1, client.GetYencHeadersCallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SegmentFileRangeReaderRejectsNonEmptyFileWithoutUsableSegmentMetadata(string? segmentId)
    {
        using var client = new FakeNntpClient();
        var reader = new SegmentFileRangeReader(
            segmentId is null ? null! : [segmentId],
            fileSize: 3,
            client,
            articleBufferSize: 1);
        var buffer = new byte[1];

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAtAsync(0, buffer, CancellationToken.None).AsTask());
        Assert.Contains("segment metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DecodedBodyCallCount);
        Assert.Equal(0, client.GetYencHeadersCallCount);
    }

    [Fact]
    public async Task SegmentFileRangeReaderDoesNotCacheCancelledYencHeaderLookup()
    {
        using var client = new DelayedHeaderNntpClient(TimeSpan.FromMilliseconds(100));
        client
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0)
            .AddSegment("segment-2", [3, 4, 5], partOffset: 3)
            .AddSegment("segment-3", [6, 7, 8], partOffset: 6);
        var reader = new SegmentFileRangeReader(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 9,
            client,
            articleBufferSize: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAtAsync(4, new byte[1], cts.Token).AsTask());

        client.Delay = TimeSpan.Zero;
        var buffer = new byte[1];
        Assert.Equal(1, await reader.ReadAtAsync(4, buffer, CancellationToken.None));
        Assert.Equal([4], buffer);
    }

    [Fact]
    public async Task SegmentFileRangeReaderDoesNotCancelSharedYencHeaderLookupWhenOneWaiterCancels()
    {
        using var client = new BlockingHeaderNntpClient();
        client
            .AddSegment("segment-1", [0, 1, 2], partOffset: 0)
            .AddSegment("segment-2", [3, 4, 5], partOffset: 3)
            .AddSegment("segment-3", [6, 7, 8], partOffset: 6);
        var reader = new SegmentFileRangeReader(
            ["segment-1", "segment-2", "segment-3"],
            fileSize: 9,
            client,
            articleBufferSize: 1);
        using var firstCts = new CancellationTokenSource();
        var first = new byte[1];
        var second = new byte[1];

        var firstRead = reader.ReadAtAsync(4, first, firstCts.Token).AsTask();
        await client.FirstHeaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = reader.ReadAtAsync(5, second, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => client.HeaderLookupCalls == 1);

        await firstCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRead);

        client.ReleaseHeaders();

        Assert.Equal(1, await secondRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([5], second);
        Assert.Equal(1, client.GetYencHeadersCallCount);
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
    public async Task ActiveReaderFallsBackToSourceWhenCacheFileIsInvalidated()
    {
        using var tempDir = new TempDirectory();
        var manager = new SparseSegmentCacheManager();
        var inner = new ByteArrayRangeReader([0, 1, 2, 3, 4, 5, 6, 7]);
        var cached = Open(manager, "file-a", inner, tempDir.Path, chunkBytes: 4, maxBytes: 1024);

        try
        {
            var first = new byte[4];
            Assert.Equal(4, await cached.ReadAtAsync(0, first, CancellationToken.None));
            manager.Dispose();

            var second = new byte[2];
            Assert.Equal(2, await cached.ReadAtAsync(4, second, CancellationToken.None));

            Assert.Equal([4, 5], second);
        }
        finally
        {
            await DisposeReaderAsync(cached);
            manager.Dispose();
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
    public async Task ForegroundReadDoesNotFetchFullChunkBeyondRequestedReadLimit()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new RecordingRangeReader([9, 1, 2, 3, 4, 5, 6, 7]);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var cached = manager.Open("file-a", inner, options, readLimitExclusive: 1);
        var buffer = new byte[1];

        try
        {
            Assert.Equal(1, await cached.ReadAtAsync(0, buffer, CancellationToken.None));
            Assert.Equal([9], buffer);
            Assert.Equal([1], inner.RequestedByteCounts);
            Assert.Equal(0, manager.GetSnapshot().Bytes);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task CacheFetchUsesBoundedSourceReadBufferForLargeChunks()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new RecordingRangeReader(new byte[4 * 1024 * 1024]);
        var options = CreateOptions(
            tempDir.Path,
            chunkBytes: 4 * 1024 * 1024,
            maxBytes: 8 * 1024 * 1024);
        var cached = manager.Open("file-a", inner, options);
        var buffer = new byte[1];

        try
        {
            Assert.Equal(1, await cached.ReadAtAsync(0, buffer, CancellationToken.None));

            Assert.All(inner.RequestedByteCounts, count => Assert.True(count <= 256 * 1024));
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task RangeLimitedReadReusesCompletedCachedChunk()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var firstInner = new ByteArrayRangeReader([9, 1, 2, 3, 4, 5, 6, 7]);
        var secondInner = new RecordingRangeReader([100, 101, 102, 103, 104, 105, 106, 107]);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var first = manager.Open("file-a", firstInner, options);

        try
        {
            var firstBuffer = new byte[4];
            Assert.Equal(4, await first.ReadAtAsync(0, firstBuffer, CancellationToken.None));
            Assert.Equal([9, 1, 2, 3], firstBuffer);
        }
        finally
        {
            await DisposeReaderAsync(first);
        }

        var second = manager.Open("file-a", secondInner, options, readLimitExclusive: 1);
        try
        {
            var secondBuffer = new byte[1];
            Assert.Equal(1, await second.ReadAtAsync(0, secondBuffer, CancellationToken.None));

            Assert.Equal([9], secondBuffer);
            Assert.Empty(secondInner.RequestedByteCounts);
        }
        finally
        {
            await DisposeReaderAsync(second);
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
    public async Task RangeLimitedDirectReadUsesNoProgressTimeout()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new NeverCompletingRangeReader(length: 4);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024) with
        {
            NoProgressTimeout = TimeSpan.FromMilliseconds(20)
        };
        var cached = manager.Open("file-a", inner, options, readLimitExclusive: 1);
        var buffer = new byte[1];

        try
        {
            var exception = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
                cached.ReadAtAsync(0, buffer, CancellationToken.None).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("No progress reading cache direct range", exception.Message);
        }
        finally
        {
            await DisposeReaderAsync(cached);
        }
    }

    [Fact]
    public async Task RangeLimitedDirectReadReportsZeroProgress()
    {
        using var tempDir = new TempDirectory();
        using var manager = new SparseSegmentCacheManager();
        var inner = new ZeroProgressRangeReader(length: 4);
        var options = CreateOptions(tempDir.Path, chunkBytes: 4, maxBytes: 1024);
        var cached = manager.Open("file-a", inner, options, readLimitExclusive: 1);
        var buffer = new byte[1];

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                cached.ReadAtAsync(0, buffer, CancellationToken.None).AsTask());
            Assert.Contains("No progress reading cache direct range", exception.Message);
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

    private static string GetCachePath(string key, SparseSegmentCacheOptions options)
    {
        var method = typeof(SparseSegmentCacheManager).GetMethod(
            "CreateNamespacedKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var cacheKey = Assert.IsType<string>(method.Invoke(null, [key, options]));
        return Path.Join(options.Directory, $"{cacheKey}.nzbdav-cache.tmp");
    }

    private static void InvokeCleanupOrphanFilesOnce(SparseSegmentCacheManager manager, string directory)
    {
        var method = typeof(SparseSegmentCacheManager).GetMethod(
            "CleanupOrphanFilesOnce",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(manager, [directory]);
    }

    private static void TruncateCacheFile(IFileRangeReader reader, long length)
    {
        var cacheFile = reader
            .GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(x => x.FieldType.Name == "CacheFile")
            .GetValue(reader);
        Assert.NotNull(cacheFile);
        var stream = Assert.IsType<FileStream>(cacheFile
            .GetType()
            .GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cacheFile));

        stream.SetLength(length);
        stream.Flush();
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

    private sealed class RecordingRangeReader(byte[] bytes) : IFileRangeReader
    {
        private readonly List<int> _requestedByteCounts = [];
        private readonly object _lock = new();

        public long Length => bytes.LongLength;

        public IReadOnlyList<int> RequestedByteCounts
        {
            get
            {
                lock (_lock)
                    return _requestedByteCounts.ToArray();
            }
        }

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            lock (_lock)
                _requestedByteCounts.Add(buffer.Length);
            if (offset >= bytes.Length) return ValueTask.FromResult(0);

            var count = Math.Min(buffer.Length, bytes.Length - (int)offset);
            bytes.AsMemory((int)offset, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
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

    private sealed class ZeroProgressRangeReader(long length) : IFileRangeReader
    {
        public long Length { get; } = length;

        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            return ValueTask.FromResult(0);
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

    private sealed class TruncatedSegmentNntpClient : NntpClient
    {
        private static readonly UsenetYencHeader Header = new()
        {
            FileName = "segment.bin",
            FileSize = 4,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 4,
            PartOffset = 0
        };

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task<UsenetResponse> AuthenticateAsync(
            string user,
            string pass,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetResponse>(new NotSupportedException());
        }

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetStatResponse>(new NotSupportedException());
        }

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetHeadResponse>(new NotSupportedException());
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Body retrieved",
                Stream = new CachedYencStream(Header, new MemoryStream([1], writable: false))
            });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDecodedArticleResponse>(new NotSupportedException());
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDecodedArticleResponse>(new NotSupportedException());
        }

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDateResponse>(new NotSupportedException());
        }

        public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            return Task.FromResult(Header);
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    private sealed class DelayedHeaderNntpClient(TimeSpan delay) : FakeNntpClient
    {
        public TimeSpan Delay { get; set; } = delay;

        public override async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);

            return await base.GetYencHeadersAsync(segmentId, ct);
        }
    }

    private sealed class BlockingHeaderNntpClient : FakeNntpClient
    {
        private readonly TaskCompletionSource _releaseHeaders =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _headerLookupCalls;

        public TaskCompletionSource FirstHeaderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int HeaderLookupCalls => Volatile.Read(ref _headerLookupCalls);

        public override async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            Interlocked.Increment(ref _headerLookupCalls);
            FirstHeaderStarted.TrySetResult();
            await _releaseHeaders.Task.WaitAsync(ct);
            return await base.GetYencHeadersAsync(segmentId, ct);
        }

        public void ReleaseHeaders()
        {
            _releaseHeaders.TrySetResult();
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

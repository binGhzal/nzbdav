using System.Reflection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace backend.Tests.Clients.Usenet;

public sealed class ArticleCachingNntpClientTests
{
    [Fact]
    public async Task CheckSegmentsAsync_ReturnsExistsForCachedBodyWithoutStat()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();

        var batch = await client.CheckSegmentsAsync(["segment-1"], 4, null, CancellationToken.None);

        Assert.Equal(1, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(SegmentCheckState.Exists, batch.Results.Single().State);
        Assert.Equal(0, inner.StatCallCount);
    }

    [Fact]
    public async Task GetCachedSegmentIdsSnapshot_ReturnsSuccessfullyFetchedBodySegments()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();

        Assert.Equal(["segment-1"], client.GetCachedSegmentIdsSnapshot());
    }

    [Fact]
    public async Task GetFetchedSegmentIdsSnapshot_ReturnsFetchedSegmentsAfterCacheEviction()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3, 4])
            .AddSegment("segment-2", [5, 6, 7, 8]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 4,
            sharedBudget: new ArticleCacheBudget());

        var firstBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await firstBody.Stream.DisposeAsync();
        var secondBody = await client.DecodedBodyAsync("segment-2", CancellationToken.None);
        await secondBody.Stream.DisposeAsync();

        Assert.Equal(["segment-2"], client.GetCachedSegmentIdsSnapshot().Order());
        Assert.Equal(["segment-1", "segment-2"], client.GetFetchedSegmentIdsSnapshot().Order());
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsExistsForFetchedSegmentAfterCacheEvictionWithoutStat()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3, 4])
            .AddSegment("segment-2", [5, 6, 7, 8]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 4,
            sharedBudget: new ArticleCacheBudget());

        var firstBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await firstBody.Stream.DisposeAsync();
        var secondBody = await client.DecodedBodyAsync("segment-2", CancellationToken.None);
        await secondBody.Stream.DisposeAsync();

        var batch = await client.CheckSegmentsAsync(["segment-1", "segment-2"], 4, null, CancellationToken.None);

        Assert.Equal(["segment-1", "segment-2"], batch.Results.Select(x => x.SegmentId));
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(0, inner.StatCallCount);
    }

    [Fact]
    public async Task CheckSegmentsAsync_StatsOnlyUncachedSegmentsAndPreservesOrder()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("cached", [1, 2, 3])
            .AddSegment("uncached", [4, 5, 6]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("cached", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();

        var batch = await client.CheckSegmentsAsync(["uncached", "cached"], 4, null, CancellationToken.None);

        Assert.Equal(["uncached", "cached"], batch.Results.Select(x => x.SegmentId));
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(1, inner.StatCallCount);
    }

    [Fact]
    public async Task CheckSegmentsAsync_DoesNotPostUncachedProgressToSynchronizationContext()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("cached", [1, 2, 3])
            .AddSegment("uncached", [4, 5, 6]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());
        var cachedBody = await client.DecodedBodyAsync("cached", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();
        var progress = new RecordingProgress();

        var (batch, postCount) = await Task.Run(() => CheckSegmentsUnderCountingSynchronizationContext(client, progress));

        Assert.Equal(["cached", "uncached"], batch.Results.Select(x => x.SegmentId));
        Assert.Equal([1, 2], progress.Values);
        Assert.Equal(0, postCount);
    }

    [Fact]
    public async Task CheckSegmentsAsync_TreatsCachedFallbackCandidateAsExists()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("candidate", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("candidate", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();
        var encodedSegmentId = NzbSegmentIdSet.Encode(["missing", "candidate"]);

        var batch = await client.CheckSegmentsAsync([encodedSegmentId], 4, null, CancellationToken.None);

        Assert.Equal(SegmentCheckState.Exists, batch.Results.Single().State);
        Assert.Equal(0, inner.StatCallCount);
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsExistsForFetchedSegmentWhenCachedFileDisappears()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();
        DeleteCachedFiles(client);

        var batch = await client.CheckSegmentsAsync(["segment-1"], 4, null, CancellationToken.None);

        Assert.Equal(SegmentCheckState.Exists, batch.Results.Single().State);
        Assert.Equal(0, inner.StatCallCount);
    }

    [Fact]
    public async Task DecodedBodyAsync_RefetchesWhenCachedFileDisappears()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var firstBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await firstBody.Stream.DisposeAsync();
        Assert.Equal(1, inner.DecodedBodyCallCount);
        DeleteCachedFiles(client);

        var secondBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await secondBody.Stream.DisposeAsync();

        Assert.Equal(2, inner.DecodedBodyCallCount);
    }

    [Fact]
    public async Task DecodedBodyAsync_RecreatesCacheDirectoryWhenItDisappears()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var firstBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await firstBody.Stream.DisposeAsync();
        Assert.Equal(1, inner.DecodedBodyCallCount);
        Directory.Delete(GetCacheDir(client), recursive: true);

        var secondBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await secondBody.Stream.DisposeAsync();

        Assert.Equal(2, inner.DecodedBodyCallCount);
        Assert.True(Directory.Exists(GetCacheDir(client)));
    }

    [Fact]
    public async Task DisposeAsync_CompletesWhenCacheDirectoryAlreadyDisappeared()
    {
        using var inner = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);
        var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());

        var cachedBody = await client.DecodedBodyAsync("segment-1", CancellationToken.None);
        await cachedBody.Stream.DisposeAsync();
        Directory.Delete(GetCacheDir(client), recursive: true);

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task DecodedBodyAsync_ReturnsUnsuccessfulResponseWithoutCaching()
    {
        using var inner = new UnsuccessfulResponseNntpClient();
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());
        var callbackResults = new List<ArticleBodyResult>();

        var response = await client.DecodedBodyAsync(
            "missing-segment",
            callbackResults.Add,
            CancellationToken.None);

        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, response.ResponseType);
        Assert.Empty(client.GetCachedSegmentIdsSnapshot());
        Assert.Equal([ArticleBodyResult.NotRetrieved], callbackResults);
    }

    [Fact]
    public async Task DecodedArticleAsync_ReturnsUnsuccessfulResponseWithoutCaching()
    {
        using var inner = new UnsuccessfulResponseNntpClient();
        await using var client = new ArticleCachingNntpClient(
            inner,
            maxCacheBytes: 1024 * 1024,
            sharedBudget: new ArticleCacheBudget());
        var callbackResults = new List<ArticleBodyResult>();

        var response = await client.DecodedArticleAsync(
            "missing-segment",
            callbackResults.Add,
            CancellationToken.None);

        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, response.ResponseType);
        Assert.Empty(client.GetCachedSegmentIdsSnapshot());
        Assert.Equal([ArticleBodyResult.NotRetrieved], callbackResults);
    }

    private static void DeleteCachedFiles(ArticleCachingNntpClient client)
    {
        var cacheDir = GetCacheDir(client);
        foreach (var file in Directory.EnumerateFiles(cacheDir))
            File.Delete(file);
    }

    private static string GetCacheDir(ArticleCachingNntpClient client)
    {
        var field = typeof(ArticleCachingNntpClient)
            .GetField("_cacheDir", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<string>(field?.GetValue(client));
    }

    private static (SegmentCheckBatch Batch, int PostCount) CheckSegmentsUnderCountingSynchronizationContext(
        ArticleCachingNntpClient client,
        IProgress<int> progress)
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var batch = client
            .CheckSegmentsAsync(["cached", "uncached"], 4, progress, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return (batch, syncContext.PostCount);
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value)
        {
            Values.Add(value);
        }
    }

    private sealed class CountingSynchronizationContextScope : IDisposable
    {
        private readonly CountingSynchronizationContext _context = new();
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public int PostCount => _context.PostCount;

        public CountingSynchronizationContextScope()
        {
            SynchronizationContext.SetSynchronizationContext(_context);
        }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }
    }

    private sealed class UnsuccessfulResponseNntpClient : NntpClient
    {
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
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = "430 - No article with message-id",
                Stream = CreateBodyStream()
            });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = "430 - No article with message-id",
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Subject"] = segmentId
                    }
                },
                Stream = CreateBodyStream()
            });
        }

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDateResponse>(new NotSupportedException());
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private static CachedYencStream CreateBodyStream()
        {
            return new CachedYencStream(new UsenetYencHeader
            {
                FileName = "missing.bin",
                FileSize = 0,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartSize = 0,
                PartOffset = 0
            }, new MemoryStream([], writable: false));
        }
    }
}

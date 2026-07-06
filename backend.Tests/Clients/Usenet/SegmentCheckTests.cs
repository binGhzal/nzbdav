using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Tests.TestDoubles;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace backend.Tests.Clients.Usenet;

public sealed class SegmentCheckTests
{
    [Fact]
    public async Task CheckSegmentsAsync_ReturnsExistsForPresentSegments()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3])
            .AddSegment("segment-2", [4, 5, 6]);

        var batch = await client.CheckSegmentsAsync(["segment-1", "segment-2"], 2, null, CancellationToken.None);

        Assert.Equal(2, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(0, batch.ProviderErrors);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsMissingForDefinitiveMissingSegment()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);

        var batch = await client.CheckSegmentsAsync(["segment-1", "missing-segment"], 2, null, CancellationToken.None);

        Assert.Equal(2, batch.Checked);
        Assert.Equal(1, batch.Missing);
        Assert.Contains(batch.Results, result =>
            result.SegmentId == "missing-segment" && result.State == SegmentCheckState.Missing);
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsProviderErrorWithoutReportingMissing()
    {
        using var client = new ProviderErrorNntpClient();

        var batch = await client.CheckSegmentsAsync(["segment-1"], 1, null, CancellationToken.None);

        Assert.Equal(1, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(1, batch.ProviderErrors);
        Assert.Equal(SegmentCheckState.ProviderError, batch.Results.Single().State);
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsProviderErrorForInternalPipelinedCancellation()
    {
        using var client = new InternallyCanceledPipelinedNntpClient();

        var batch = await client.CheckSegmentsAsync(["segment-1"], 1, null, CancellationToken.None);

        Assert.Equal(1, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(1, batch.ProviderErrors);
        Assert.Equal(SegmentCheckState.ProviderError, batch.Results.Single().State);
    }

    [Fact]
    public async Task CheckSegmentsAsync_UsesPipelinedStatBatches()
    {
        using var client = new PipelinedOnlyNntpClient(["segment-1", "segment-2"]);

        var batch = await client.CheckSegmentsAsync(["segment-1", "segment-2"], 2, null, CancellationToken.None);

        Assert.Equal(2, batch.Checked);
        Assert.Equal(0, batch.ProviderErrors);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal([["segment-1", "segment-2"]], client.PipelinedCalls);
        Assert.Equal(0, client.StatCalls);
    }

    [Fact]
    public async Task CheckSegmentsAsync_RunsPipelinedStatBatchesConcurrently()
    {
        using var client = new BlockingPipelinedOnlyNntpClient(
            ["segment-1", "segment-2", "segment-3", "segment-4"]);

        var checkTask = client.CheckSegmentsAsync(
            ["segment-1", "segment-2", "segment-3", "segment-4"],
            2,
            null,
            CancellationToken.None);

        Assert.True(await client.WaitForActiveBatchesAsync(2, TimeSpan.FromSeconds(5)));
        client.ReleaseAllBatches();
        var batch = await checkTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(2, client.MaxActiveBatches);
        Assert.Equal(2, client.PipelinedCalls.Count);
    }

    [Fact]
    public async Task CheckSegmentsAsync_RunsNonPipelinedStatCallsConcurrently()
    {
        using var client = new ConcurrentStatNntpClient();

        var batch = await client.CheckSegmentsAsync(
            ["segment-1", "segment-2", "segment-3", "segment-4"],
            4,
            null,
            CancellationToken.None);

        Assert.Equal(4, batch.Checked);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(4, client.ConcurrentStatCallCount);
        Assert.True(client.MaxActiveStats > 1);
    }

    [Fact]
    public async Task CheckSegmentsAsync_UsesFallbackCandidateWhenPrimaryCandidateIsMissing()
    {
        using var client = new PipelinedOnlyNntpClient(["segment-1b"]);
        var encodedSegment = NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"]);

        var batch = await client.CheckSegmentsAsync([encodedSegment], 8, null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(SegmentCheckState.Exists, result.State);
        Assert.Equal("segment-1b", result.CandidateSegmentId);
        Assert.Equal([["segment-1a"], ["segment-1b"]], client.PipelinedCalls);
        Assert.Equal(0, client.StatCalls);
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReportsFallbackCandidateForNonPipelinedStats()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1b", [1, 2, 3]);
        var encodedSegment = NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"]);

        var batch = await client.CheckSegmentsAsync([encodedSegment], 8, null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(SegmentCheckState.Exists, result.State);
        Assert.Equal("segment-1b", result.CandidateSegmentId);
        Assert.Equal(2, client.StatCallCount);
    }

    [Fact]
    public async Task CheckSegmentsAsync_FallsBackWhenNonPipelinedStatReturnsMissingResponse()
    {
        using var client = new MissingResponseFallbackNntpClient("segment-1b");
        var encodedSegment = NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"]);

        var batch = await client.CheckSegmentsAsync([encodedSegment], 8, null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(SegmentCheckState.Exists, result.State);
        Assert.Equal("segment-1b", result.CandidateSegmentId);
        Assert.Equal(["segment-1a", "segment-1b"], client.StatCalls);
    }

    [Fact]
    public async Task StreamingCheckSegmentsAsync_UsesFallbackCandidateWithPipelinedBatches()
    {
        using var innerClient = new PipelinedOnlyNntpClient(["segment-1b"]);
        using var client = new TestUsenetStreamingClient(CreatePipelinedStreamingConfig(), innerClient);
        var encodedSegment = NzbSegmentIdSet.Encode(["segment-1a", "segment-1b"]);

        var batch = await client.CheckSegmentsAsync([encodedSegment], 4, null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(SegmentCheckState.Exists, result.State);
        Assert.Equal("segment-1b", result.CandidateSegmentId);
        Assert.Equal([["segment-1a"], ["segment-1b"]], innerClient.PipelinedCalls);
        Assert.Equal(0, innerClient.StatCalls);
    }

    [Fact]
    public async Task StreamingCheckSegmentsAsync_UsesConfiguredPipeliningDepthAsBatchSize()
    {
        var segmentIds = Enumerable.Range(1, 8)
            .Select(i => $"segment-{i}")
            .ToArray();
        using var innerClient = new PipelinedOnlyNntpClient(segmentIds);
        using var client = new TestUsenetStreamingClient(CreatePipelinedStreamingConfig(), innerClient);

        var batch = await client.CheckSegmentsAsync(segmentIds, 4, null, CancellationToken.None);

        Assert.Equal(8, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(4, innerClient.PipelinedCalls.Count);
        Assert.All(innerClient.PipelinedCalls, call => Assert.Equal(2, call.Length));
        Assert.Equal(0, innerClient.StatCalls);
    }

    [Fact]
    public async Task StreamingCheckSegmentsAsyncCapsConcurrentPipelinedStatBatches()
    {
        var segmentIds = Enumerable.Range(1, 3000)
            .Select(i => $"segment-{i}")
            .ToArray();
        using var innerClient = new BlockingPipelinedOnlyNntpClient(segmentIds);
        var configManager = CreatePipelinedStreamingConfig(depth: 50);
        using var client = new TestUsenetStreamingClient(configManager, innerClient);
        var expectedCap = UsenetStreamingClient.GetPipelinedStatBatchConcurrency(200);
        var expectedActiveBatches = Math.Min(expectedCap, (int)Math.Ceiling(segmentIds.Length / 50d));

        var checkTask = client.CheckSegmentsAsync(segmentIds, 200, null, CancellationToken.None);

        Assert.True(await innerClient.WaitForActiveBatchesAsync(expectedActiveBatches, TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        Assert.Equal(expectedActiveBatches, innerClient.MaxActiveBatches);

        innerClient.ReleaseAllBatches();
        var batch = await checkTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(segmentIds.Length, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.All(innerClient.PipelinedCalls, call => Assert.True(call.Length <= 50));
    }

    [Fact]
    public async Task StreamingCheckSegmentsAsyncRotatesProviderFanoutAcrossSmallPipelinedBatches()
    {
        var segmentIds = Enumerable.Range(1, 100)
            .Select(i => $"segment-{i}")
            .ToArray();
        var primary = new FakePipelinedProvider(
            "primary",
            UsenetResponseType.ArticleExists,
            maxConnections: 50);
        var secondary = new FakePipelinedProvider(
            "secondary",
            UsenetResponseType.ArticleExists,
            maxConnections: 50);
        using var innerClient = new MultiProviderNntpClient([primary, secondary]);
        using var client = new TestUsenetStreamingClient(CreatePipelinedStreamingConfig(), innerClient);

        var batch = await client.CheckSegmentsAsync(segmentIds, 50, null, CancellationToken.None);

        Assert.Equal(100, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(50, primary.PipelinedCalls.SelectMany(x => x).Count());
        Assert.Equal(50, secondary.PipelinedCalls.SelectMany(x => x).Count());
    }

    [Fact]
    public async Task CheckSegmentsAsync_DoesNotMarkEntirePipelinedBatchMissingWhenOneCandidateThrows()
    {
        using var client = new ThrowingPipelinedMissingNntpClient("missing-segment");

        var batch = await client.CheckSegmentsAsync(
            ["missing-segment", "unchecked-segment"],
            2,
            null,
            CancellationToken.None);

        Assert.Equal(SegmentCheckState.Missing, batch.Results[0].State);
        Assert.Equal(SegmentCheckState.Unknown, batch.Results[1].State);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_ThrowsArticleNotFoundOnlyForMissingSegment()
    {
        using var client = new FakeNntpClient();

        var ex = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(["missing-segment"], 1, null, CancellationToken.None));

        Assert.Equal("missing-segment", ex.SegmentId);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_ThrowsRetryableErrorForProviderFailure()
    {
        using var client = new ProviderErrorNntpClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CheckAllSegmentsAsync(["segment-1"], 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderPipelinedStatThrowsWhenMissingProviderIsFollowedByProviderError()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("backup", new IOException("backup unavailable"))
        ]);

        await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            client.StatPipelinedAsync(["segment-1"], CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderPipelinedStatReturnsMissingWhenEveryProviderDefinitivelyMisses()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId)
        ]);

        var result = await client.StatPipelinedAsync(["segment-1"], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, result.Single().ResponseType);
    }

    [Fact]
    public async Task MultiProviderStatThrowsWhenProviderErrorIsFollowedByMissing()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId)
        ]);

        await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            client.StatAsync("segment-1", CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderStatCanReturnSuccessFromBackupAfterProviderError()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatAsync("segment-1", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, result.ResponseType);
    }

    [Fact]
    public async Task MultiProviderStatContinuesAfterProviderErrorThenMissingWhenLaterProviderExists()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("tertiary", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatAsync("segment-1", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, result.ResponseType);
    }

    [Fact]
    public async Task MultiProviderStatContinuesAfterUnknownWhenLaterProviderExists()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.Unknown),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatAsync("segment-1", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, result.ResponseType);
    }

    [Fact]
    public async Task MultiProviderPipelinedStatContinuesAfterUnknownWhenLaterProviderExists()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.Unknown),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatPipelinedAsync(["segment-1"], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(UsenetResponseType.ArticleExists, result.Single().ResponseType);
    }

    [Fact]
    public async Task MultiProviderPipelinedStatFallsBackWhenProviderReturnsShortBatch()
    {
        using var client = new MultiProviderNntpClient([
            new ShortPipelinedProvider("primary"),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatPipelinedAsync(["segment-1", "segment-2"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, response => Assert.Equal(UsenetResponseType.ArticleExists, response.ResponseType));
    }

    [Fact]
    public async Task MultiProviderPipelinedStatFansOutInitialChecksAcrossHealthyProviders()
    {
        var segmentIds = Enumerable.Range(1, 20)
            .Select(i => $"segment-{i}")
            .ToArray();
        var primary = new FakePipelinedProvider(
            "primary",
            UsenetResponseType.ArticleExists,
            maxConnections: 1,
            providerPriority: 0);
        var backup = new FakePipelinedProvider(
            "backup",
            UsenetResponseType.ArticleExists,
            maxConnections: 1,
            providerPriority: 10);
        using var client = new MultiProviderNntpClient([primary, backup]);

        var result = await client.StatPipelinedAsync(segmentIds, CancellationToken.None);

        Assert.Equal(20, result.Count);
        Assert.All(result, response => Assert.Equal(UsenetResponseType.ArticleExists, response.ResponseType));
        Assert.NotEmpty(primary.PipelinedCalls);
        Assert.NotEmpty(backup.PipelinedCalls);
        var checkedSegments = primary.PipelinedCalls
            .Concat(backup.PipelinedCalls)
            .SelectMany(x => x)
            .Order()
            .ToArray();
        Assert.Equal(segmentIds.Order().ToArray(), checkedSegments);
    }

    [Fact]
    public async Task MultiProviderPipelinedStatUsesConcurrentStatsForNonPipelinedProviderInMixedPool()
    {
        var segmentIds = Enumerable.Range(1, 8)
            .Select(i => $"segment-{i}")
            .ToArray();
        var tracker = new ConcurrentStatTracker();
        using var client = new MultiProviderNntpClient([
            new NonPipelinedConcurrentProvider("primary", tracker, maxConnections: 4),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists, maxConnections: 1)
        ]);

        var result = await client.StatPipelinedAsync(segmentIds, CancellationToken.None);

        Assert.Equal(8, result.Count);
        Assert.All(result, response => Assert.Equal(UsenetResponseType.ArticleExists, response.ResponseType));
        Assert.True(tracker.StatCalls > 1);
        Assert.True(tracker.MaxActiveStats > 1);
    }

    [Fact]
    public async Task MultiProviderCheckSegmentsFansOutInitialChecksAcrossHealthyProviders()
    {
        var segmentIds = Enumerable.Range(1, 20)
            .Select(i => $"segment-{i}")
            .ToArray();
        var primary = new FakePipelinedProvider(
            "primary",
            UsenetResponseType.ArticleExists,
            maxConnections: 1,
            providerPriority: 0);
        var backup = new FakePipelinedProvider(
            "backup",
            UsenetResponseType.ArticleExists,
            maxConnections: 1,
            providerPriority: 10);
        using var client = new MultiProviderNntpClient([primary, backup]);

        var batch = await client.CheckSegmentsAsync(segmentIds, 8, null, CancellationToken.None);

        Assert.Equal(20, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.NotEmpty(primary.PipelinedCalls);
        Assert.NotEmpty(backup.PipelinedCalls);
        var checkedSegments = primary.PipelinedCalls
            .Concat(backup.PipelinedCalls)
            .SelectMany(x => x)
            .Order()
            .ToArray();
        Assert.Equal(segmentIds.Order().ToArray(), checkedSegments);
    }

    [Fact]
    public async Task MultiProviderCheckSegmentsWeightsInitialFanoutByFullAvailableConnections()
    {
        var segmentIds = Enumerable.Range(1, 201)
            .Select(i => $"segment-{i}")
            .ToArray();
        var primary = new FakePipelinedProvider(
            "primary",
            UsenetResponseType.ArticleExists,
            maxConnections: 200,
            providerPriority: 0);
        var backup = new FakePipelinedProvider(
            "backup",
            UsenetResponseType.ArticleExists,
            maxConnections: 1,
            providerPriority: 10);
        using var client = new MultiProviderNntpClient([primary, backup]);

        var batch = await client.CheckSegmentsAsync(segmentIds, 201, null, CancellationToken.None);

        Assert.Equal(201, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.Equal(200, primary.PipelinedCalls.SelectMany(x => x).Count());
        Assert.Single(backup.PipelinedCalls.SelectMany(x => x));
    }

    [Fact]
    public async Task MultiProviderCheckSegmentsUsesConcurrentStatsForNonPipelinedProviderInMixedPool()
    {
        var tracker = new ConcurrentStatTracker();
        using var client = new MultiProviderNntpClient([
            new NonPipelinedConcurrentProvider("primary", tracker, maxConnections: 4),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);
        var progress = new RecordingProgress();

        var batch = await client.CheckSegmentsAsync(
            ["segment-1", "segment-2", "segment-3", "segment-4"],
            4,
            progress,
            CancellationToken.None);

        Assert.Equal(4, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
        Assert.True(tracker.MaxActiveStats > 1);
        Assert.Equal(4, tracker.StatCalls);
        Assert.Equal([1, 2, 3, 4], progress.Values.Order().ToArray());
    }

    [Fact]
    public async Task MultiProviderCheckSegmentsPreservesProviderErrorOverLaterUnknown()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.Unknown)
        ]);

        var batch = await client.CheckSegmentsAsync(["segment-1"], 4, null, CancellationToken.None);

        var result = Assert.Single(batch.Results);
        Assert.Equal(SegmentCheckState.ProviderError, result.State);
        Assert.Contains("primary unavailable", result.Error);
    }

    private sealed class ProviderErrorNntpClient : FakeNntpClient
    {
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            throw new IOException("provider unavailable");
        }
    }

    private sealed class InternallyCanceledPipelinedNntpClient : FakeNntpClient
    {
        protected override bool SupportsPipelinedSegmentChecks => true;

        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("provider command timed out");
        }
    }

    private sealed class PipelinedOnlyNntpClient : FakeNntpClient
    {
        private readonly HashSet<string> _existingSegments;
        private readonly List<string[]> _pipelinedCalls = [];
        private int _statCalls;
        protected override bool SupportsPipelinedSegmentChecks => true;

        public PipelinedOnlyNntpClient(IEnumerable<string> existingSegments)
        {
            _existingSegments = existingSegments.ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyList<string[]> PipelinedCalls => _pipelinedCalls;
        public int StatCalls => _statCalls;

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _statCalls);
            throw new InvalidOperationException("Segment checks should use pipelined STAT.");
        }

        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken)
        {
            _pipelinedCalls.Add(segmentIds.ToArray());
            IReadOnlyList<UsenetStatResponse> responses = segmentIds
                .Select(segmentId => CreateStatResponse(_existingSegments.Contains(segmentId)
                    ? UsenetResponseType.ArticleExists
                    : UsenetResponseType.NoArticleWithThatMessageId))
                .ToArray();
            return Task.FromResult(responses);
        }

        private static UsenetStatResponse CreateStatResponse(UsenetResponseType responseType)
        {
            return new UsenetStatResponse
            {
                ArticleExists = responseType == UsenetResponseType.ArticleExists,
                ResponseCode = (int)responseType,
                ResponseMessage = responseType.ToString()
            };
        }
    }

    private sealed class BlockingPipelinedOnlyNntpClient : FakeNntpClient
    {
        private readonly HashSet<string> _existingSegments;
        private readonly List<string[]> _pipelinedCalls = [];
        private readonly TaskCompletionSource _releaseBatches = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _activeBatches;
        private int _maxActiveBatches;
        protected override bool SupportsPipelinedSegmentChecks => true;

        public BlockingPipelinedOnlyNntpClient(IEnumerable<string> existingSegments)
        {
            _existingSegments = existingSegments.ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyList<string[]> PipelinedCalls
        {
            get
            {
                lock (_lock)
                    return _pipelinedCalls.ToArray();
            }
        }

        public int MaxActiveBatches => Volatile.Read(ref _maxActiveBatches);

        public async Task<bool> WaitForActiveBatchesAsync(int expected, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                if (Volatile.Read(ref _activeBatches) >= expected)
                    return true;

                try
                {
                    await Task.Delay(10, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return Volatile.Read(ref _activeBatches) >= expected;
        }

        public void ReleaseAllBatches()
        {
            _releaseBatches.TrySetResult();
        }

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Segment checks should use pipelined STAT.");
        }

        public override async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken)
        {
            lock (_lock)
                _pipelinedCalls.Add(segmentIds.ToArray());

            var active = Interlocked.Increment(ref _activeBatches);
            UpdateMaxActiveBatches(active);
            try
            {
                await _releaseBatches.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return segmentIds
                    .Select(segmentId => CreateStatResponse(_existingSegments.Contains(segmentId)
                        ? UsenetResponseType.ArticleExists
                        : UsenetResponseType.NoArticleWithThatMessageId))
                    .ToArray();
            }
            finally
            {
                Interlocked.Decrement(ref _activeBatches);
            }
        }

        private void UpdateMaxActiveBatches(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActiveBatches);
                if (active <= current) return;
                if (Interlocked.CompareExchange(ref _maxActiveBatches, active, current) == current) return;
            }
        }

        private static UsenetStatResponse CreateStatResponse(UsenetResponseType responseType)
        {
            return new UsenetStatResponse
            {
                ArticleExists = responseType == UsenetResponseType.ArticleExists,
                ResponseCode = (int)responseType,
                ResponseMessage = responseType.ToString()
            };
        }
    }

    private sealed class ThrowingPipelinedMissingNntpClient(string missingSegmentId) : FakeNntpClient
    {
        protected override bool SupportsPipelinedSegmentChecks => true;

        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken)
        {
            throw new UsenetArticleNotFoundException(missingSegmentId);
        }
    }

    private sealed class ConcurrentStatNntpClient : FakeNntpClient
    {
        private int _activeStats;
        private int _maxActiveStats;
        private int _statCalls;

        public int MaxActiveStats => Volatile.Read(ref _maxActiveStats);
        public int ConcurrentStatCallCount => Volatile.Read(ref _statCalls);

        public override async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _statCalls);
            var active = Interlocked.Increment(ref _activeStats);
            UpdateMaxActiveStats(active);
            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                return new UsenetStatResponse
                {
                    ArticleExists = true,
                    ResponseCode = (int)UsenetResponseType.ArticleExists,
                    ResponseMessage = "223 - Article exists"
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeStats);
            }
        }

        private void UpdateMaxActiveStats(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActiveStats);
                if (active <= current) return;
                if (Interlocked.CompareExchange(ref _maxActiveStats, active, current) == current) return;
            }
        }
    }

    private sealed class MissingResponseFallbackNntpClient(string existingSegmentId) : FakeNntpClient
    {
        private readonly List<string> _statCalls = [];

        public IReadOnlyList<string> StatCalls => _statCalls;

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            _statCalls.Add(segmentId);
            var exists = string.Equals(segmentId, existingSegmentId, StringComparison.Ordinal);
            return Task.FromResult(new UsenetStatResponse
            {
                ArticleExists = exists,
                ResponseCode = (int)(exists
                    ? UsenetResponseType.ArticleExists
                    : UsenetResponseType.NoArticleWithThatMessageId),
                ResponseMessage = exists ? "223 - Article exists" : "430 - No article with that message-id"
            });
        }
    }

    private sealed class TestUsenetStreamingClient : UsenetStreamingClient
    {
        public TestUsenetStreamingClient(ConfigManager configManager, INntpClient innerClient)
            : base(configManager, new WebsocketManager())
        {
            ReplaceUnderlyingClient(innerClient);
        }
    }

    private static ConfigManager CreatePipelinedStreamingConfig(int depth = 2)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = System.Text.Json.JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            Type = ProviderType.Pooled,
                            Host = "news.example.invalid",
                            Port = 563,
                            UseSsl = true,
                            User = "user",
                            Pass = "pass",
                            MaxConnections = 4,
                            StatPipeliningEnabled = true
                        }
                    ]
                })
            },
            new ConfigItem { ConfigName = "usenet.nntp-pipelining.enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "usenet.nntp-pipelining.depth", ConfigValue = depth.ToString() },
        ]);
        return configManager;
    }

    private sealed class ShortPipelinedProvider(string name) : FakePipelinedProvider(name, UsenetResponseType.ArticleExists)
    {
        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
        (
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<UsenetStatResponse> results = [
                CreateStatResponse(UsenetResponseType.NoArticleWithThatMessageId)
            ];
            return Task.FromResult(results);
        }
    }

    private sealed class NonPipelinedConcurrentProvider : MultiConnectionNntpClient
    {
        public NonPipelinedConcurrentProvider(string name, ConcurrentStatTracker tracker, int maxConnections)
            : base(
                new ConnectionPool<INntpClient>(
                    maxConnections,
                    _ => ValueTask.FromResult<INntpClient>(new TrackingStatNntpClient(tracker)),
                    TimeSpan.FromMinutes(1)),
                ProviderType.Pooled,
                new ProviderCircuitBreaker(name),
                name,
                providerPriority: 0,
                statPipeliningEnabled: false)
        {
        }
    }

    private sealed class TrackingStatNntpClient(ConcurrentStatTracker tracker) : FakeNntpClient
    {
        public override async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            return await tracker.StatAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ConcurrentStatTracker
    {
        private int _activeStats;
        private int _maxActiveStats;
        private int _statCalls;

        public int MaxActiveStats => Volatile.Read(ref _maxActiveStats);
        public int StatCalls => Volatile.Read(ref _statCalls);

        public async Task<UsenetStatResponse> StatAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _statCalls);
            var active = Interlocked.Increment(ref _activeStats);
            UpdateMaxActiveStats(active);
            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                return new UsenetStatResponse
                {
                    ArticleExists = true,
                    ResponseCode = (int)UsenetResponseType.ArticleExists,
                    ResponseMessage = "223 - Article exists"
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeStats);
            }
        }

        private void UpdateMaxActiveStats(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActiveStats);
                if (active <= current) return;
                if (Interlocked.CompareExchange(ref _maxActiveStats, active, current) == current) return;
            }
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value)
        {
            Values.Add(value);
        }
    }

    private static void AssertLazyAllExists(SegmentCheckBatch batch)
    {
        Assert.True(batch.IsClean);
        Assert.Contains("AllExistsResults", batch.Results.GetType().Name);
    }

    private class FakePipelinedProvider : MultiConnectionNntpClient
    {
        private readonly UsenetResponseType? _responseType;
        private readonly Exception? _exception;
        private readonly List<string[]> _pipelinedCalls = [];

        public FakePipelinedProvider(
            string name,
            UsenetResponseType responseType,
            int maxConnections = 1,
            int providerPriority = 0)
            : base(
                new ConnectionPool<INntpClient>(
                    maxConnections,
                    _ => ValueTask.FromResult<INntpClient>(new FakeNntpClient()),
                    TimeSpan.FromMinutes(1)),
                ProviderType.Pooled,
                new ProviderCircuitBreaker(name),
                name,
                providerPriority,
                statPipeliningEnabled: true)
        {
            _responseType = responseType;
        }

        public FakePipelinedProvider(string name, Exception exception)
            : this(name, UsenetResponseType.ArticleExists)
        {
            _responseType = null;
            _exception = exception;
        }

        public IReadOnlyList<string[]> PipelinedCalls => _pipelinedCalls;

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            if (_exception is not null) throw _exception;
            return Task.FromResult(CreateStatResponse(_responseType ?? UsenetResponseType.ArticleExists));
        }

        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
        (
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken
        )
        {
            if (_exception is not null) throw _exception;

            _pipelinedCalls.Add(segmentIds.ToArray());
            var responseType = _responseType ?? UsenetResponseType.ArticleExists;
            IReadOnlyList<UsenetStatResponse> results = segmentIds
                .Select(_ => CreateStatResponse(responseType))
                .ToArray();
            return Task.FromResult(results);
        }

        protected static UsenetStatResponse CreateStatResponse(UsenetResponseType responseType)
        {
            return new UsenetStatResponse
            {
                ArticleExists = responseType == UsenetResponseType.ArticleExists,
                ResponseCode = (int)responseType,
                ResponseMessage = responseType.ToString()
            };
        }
    }
}

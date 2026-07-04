using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DownloadingNntpClientTests
{
    [Fact]
    public async Task StreamingContextLimitsConcurrentArticleDownloadsPerStream()
    {
        using var innerClient = new BlockingNntpClient();
        using var client = new DownloadingNntpClient(innerClient, CreateConfigManager());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var connectionLimiter = new SemaphoreSlim(1);
        using var priorityContext = timeout.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiter = connectionLimiter
        });

        var first = client.DecodedBodyAsync("segment-1", timeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(1, timeout.Token);

        var second = client.DecodedBodyAsync("segment-2", timeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(timeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(2, timeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(timeout.Token);
        await Task.WhenAll(first, second);
        Assert.Equal(2, innerClient.StartedBodyDownloads);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);
    }

    [Fact]
    public async Task StreamingContextsShareGlobalConnectionLimiter()
    {
        using var innerClient = new BlockingNntpClient();
        using var client = new DownloadingNntpClient(innerClient, CreateConfigManager());
        using var firstTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var secondTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var globalConnectionLimiter = new SemaphoreSlim(1);
        using var firstPerStreamLimiter = new SemaphoreSlim(2);
        using var secondPerStreamLimiter = new SemaphoreSlim(2);
        using var firstPriorityContext = firstTimeout.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiters =
            [
                new SemaphoreSlimConnectionLimiter(firstPerStreamLimiter),
                new SemaphoreSlimConnectionLimiter(globalConnectionLimiter)
            ]
        });
        using var secondPriorityContext = secondTimeout.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiters =
            [
                new SemaphoreSlimConnectionLimiter(secondPerStreamLimiter),
                new SemaphoreSlimConnectionLimiter(globalConnectionLimiter)
            ]
        });

        var first = client.DecodedBodyAsync("segment-1", firstTimeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(1, firstTimeout.Token);

        var second = client.DecodedBodyAsync("segment-2", secondTimeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(firstTimeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(2, secondTimeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(secondTimeout.Token);
        await Task.WhenAll(first, second);
        Assert.Equal(2, innerClient.StartedBodyDownloads);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);
    }

    [Fact]
    public async Task StreamingContextIgnoresLateCallbacksAfterStreamLimiterIsDisposed()
    {
        using var innerClient = new BlockingNntpClient();
        using var client = new DownloadingNntpClient(innerClient, CreateConfigManager());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectionLimiter = new SemaphoreSlim(1);
        using var priorityContext = timeout.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiter = connectionLimiter
        });

        var request = client.DecodedBodyAsync("segment-1", timeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(1, timeout.Token);

        connectionLimiter.Dispose();
        await innerClient.ReleaseOneAsync(timeout.Token);

        var response = await request;
        Assert.Equal("segment-1", response.SegmentId);
    }

    [Fact]
    public async Task StatPipelinedAsyncUsesSharedDownloadConnectionLimiter()
    {
        using var innerClient = new BlockingNntpClient();
        using var client = new DownloadingNntpClient(innerClient, CreateConfigManager(maxDownloadConnections: 1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var first = client.StatPipelinedAsync(["segment-1"], timeout.Token);
        await innerClient.WaitForStartedPipelinedStatCountAsync(1, timeout.Token);

        var second = client.StatPipelinedAsync(["segment-2"], timeout.Token);
        await Task.Delay(100, timeout.Token);

        Assert.Equal(1, innerClient.StartedPipelinedStats);
        Assert.Equal(1, innerClient.MaxActivePipelinedStats);

        await innerClient.ReleaseOneAsync(timeout.Token);
        await first.WaitAsync(timeout.Token);
        await innerClient.WaitForStartedPipelinedStatCountAsync(2, timeout.Token);
        Assert.Equal(1, innerClient.MaxActivePipelinedStats);

        await innerClient.ReleaseOneAsync(timeout.Token);
        await second.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task VerificationPriorityRunsBeforeQueuedBulkDownloads()
    {
        using var innerClient = new BlockingNntpClient();
        using var client = new DownloadingNntpClient(innerClient, CreateConfigManager(maxDownloadConnections: 1));
        using var lowTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var verifyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var verifyPriorityContext = verifyTimeout.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.Normal
        });

        var activeDownload = client.DecodedBodyAsync("segment-1", lowTimeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(1, lowTimeout.Token);

        var queuedDownload = client.DecodedBodyAsync("segment-2", lowTimeout.Token);
        await Task.Delay(100, lowTimeout.Token);
        var verification = client.StatPipelinedAsync(["segment-3"], verifyTimeout.Token);

        await innerClient.ReleaseOneAsync(lowTimeout.Token);
        await activeDownload.WaitAsync(lowTimeout.Token);
        await innerClient.WaitForStartedPipelinedStatCountAsync(1, verifyTimeout.Token);

        Assert.Equal(1, innerClient.StartedBodyDownloads);

        await innerClient.ReleaseOneAsync(verifyTimeout.Token);
        await verification.WaitAsync(verifyTimeout.Token);
        await innerClient.WaitForStartedBodyCountAsync(2, lowTimeout.Token);

        await innerClient.ReleaseOneAsync(lowTimeout.Token);
        await queuedDownload.WaitAsync(lowTimeout.Token);
    }

    private static ConfigManager CreateConfigManager(int maxDownloadConnections = 8)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "usenet.max-download-connections", ConfigValue = maxDownloadConnections.ToString() },
            new ConfigItem { ConfigName = "usenet.adaptive-connections-enabled", ConfigValue = "false" },
            new ConfigItem { ConfigName = "usenet.streaming-priority", ConfigValue = "100" }
        ]);
        return configManager;
    }

    private sealed class BlockingNntpClient : NntpClient
    {
        private readonly Channel<TaskCompletionSource> _releaseSignals = Channel.CreateUnbounded<TaskCompletionSource>();
        private readonly object _startedLock = new();
        private TaskCompletionSource _startedChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeBodyDownloads;
        private int _maxActiveBodyDownloads;
        private int _startedBodyDownloads;
        private int _activePipelinedStats;
        private int _maxActivePipelinedStats;
        private int _startedPipelinedStats;

        public int MaxActiveBodyDownloads => Volatile.Read(ref _maxActiveBodyDownloads);
        public int StartedBodyDownloads => Volatile.Read(ref _startedBodyDownloads);
        public int MaxActivePipelinedStats => Volatile.Read(ref _maxActivePipelinedStats);
        public int StartedPipelinedStats => Volatile.Read(ref _startedPipelinedStats);

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

        public override async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activePipelinedStats);
            UpdateMaxActive(ref _maxActivePipelinedStats, active);
            Interlocked.Increment(ref _startedPipelinedStats);
            NotifyStartedChanged();

            var releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _releaseSignals.Writer.WriteAsync(releaseSignal, cancellationToken).ConfigureAwait(false);
            await releaseSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            Interlocked.Decrement(ref _activePipelinedStats);
            return segmentIds.Select(_ => new UsenetStatResponse
            {
                ArticleExists = true,
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = "223 - Article exists"
            }).ToArray();
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

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeBodyDownloads);
            UpdateMaxActive(ref _maxActiveBodyDownloads, active);
            Interlocked.Increment(ref _startedBodyDownloads);
            NotifyStartedChanged();

            var releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _releaseSignals.Writer.WriteAsync(releaseSignal, cancellationToken).ConfigureAwait(false);
            await releaseSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            Interlocked.Decrement(ref _activeBodyDownloads);
            return CreateResponse(segmentId);
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

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public Task WaitForStartedBodyCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            return WaitForStartedCountAsync(() => Volatile.Read(ref _startedBodyDownloads), expectedCount, cancellationToken);
        }

        public Task WaitForStartedPipelinedStatCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            return WaitForStartedCountAsync(() => Volatile.Read(ref _startedPipelinedStats), expectedCount, cancellationToken);
        }

        public async Task ReleaseOneAsync(CancellationToken cancellationToken)
        {
            var releaseSignal = await _releaseSignals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            releaseSignal.SetResult();
        }

        private static void UpdateMaxActive(ref int maxActiveField, int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxActiveField);
                if (active <= current) return;
                if (Interlocked.CompareExchange(ref maxActiveField, active, current) == current) return;
            }
        }

        private async Task WaitForStartedCountAsync(
            Func<int> getStartedCount,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            while (getStartedCount() < expectedCount)
            {
                Task waitTask;
                lock (_startedLock)
                {
                    waitTask = _startedChanged.Task;
                }

                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void NotifyStartedChanged()
        {
            TaskCompletionSource toRelease;
            lock (_startedLock)
            {
                toRelease = _startedChanged;
                _startedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            toRelease.TrySetResult();
        }

        private static UsenetDecodedBodyResponse CreateResponse(string segmentId)
        {
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Body retrieved",
                Stream = new CachedYencStream(new UsenetYencHeader
                {
                    FileName = "segment.bin",
                    FileSize = 1,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartSize = 1,
                    PartOffset = 0
                }, new MemoryStream([1], writable: false))
            };
        }
    }
}

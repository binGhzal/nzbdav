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
        await innerClient.WaitForStartedCountAsync(1, timeout.Token);

        var second = client.DecodedBodyAsync("segment-2", timeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(timeout.Token);
        await innerClient.WaitForStartedCountAsync(2, timeout.Token);
        Assert.Equal(1, innerClient.MaxActiveBodyDownloads);

        await innerClient.ReleaseOneAsync(timeout.Token);
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
        await innerClient.WaitForStartedCountAsync(1, timeout.Token);

        connectionLimiter.Dispose();
        await innerClient.ReleaseOneAsync(timeout.Token);

        var response = await request;
        Assert.Equal("segment-1", response.SegmentId);
    }

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "usenet.max-download-connections", ConfigValue = "8" },
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

        public int MaxActiveBodyDownloads => Volatile.Read(ref _maxActiveBodyDownloads);
        public int StartedBodyDownloads => Volatile.Read(ref _startedBodyDownloads);

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

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeBodyDownloads);
            UpdateMaxActive(active);
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

        public async Task WaitForStartedCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _startedBodyDownloads) < expectedCount)
            {
                Task waitTask;
                lock (_startedLock)
                {
                    waitTask = _startedChanged.Task;
                }

                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task ReleaseOneAsync(CancellationToken cancellationToken)
        {
            var releaseSignal = await _releaseSignals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            releaseSignal.SetResult();
        }

        private void UpdateMaxActive(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActiveBodyDownloads);
                if (active <= current) return;
                if (Interlocked.CompareExchange(ref _maxActiveBodyDownloads, active, current) == current) return;
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

using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for limiting NNTP operations (STAT/HEAD/BODY/ARTICLE)
/// to the configured number of maximum download connections, with priority-based
/// scheduling that favors streaming over queue operations.
/// </summary>
/// <param name="usenetClient"></param>
public class DownloadingNntpClient : WrappingNntpClient
{
    private sealed class ConnectionLease(
        PrioritizedSemaphore downloadSemaphore,
        IReadOnlyList<IConnectionLimiter> streamLimiters)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            downloadSemaphore.Release();

            foreach (var streamLimiter in streamLimiters.Reverse())
            {
                try
                {
                    streamLimiter.Release();
                }
                catch (ObjectDisposedException)
                {
                    // A streaming response can complete while the NNTP callback is still unwinding.
                    // The shared download lease is already returned; the limiter is gone.
                }
            }
        }
    }

    private readonly ConfigManager _configManager;
    private readonly PrioritizedSemaphore _semaphore;
    private int _maxAllowedConnections;

    public DownloadingNntpClient(INntpClient usenetClient, ConfigManager configManager) : base(usenetClient)
    {
        var maxDownloadConnections = configManager.GetAdaptiveMaxDownloadConnections();
        var streamingPriority = configManager.GetStreamingPriority();
        _configManager = configManager;
        _maxAllowedConnections = maxDownloadConnections;
        _semaphore = new PrioritizedSemaphore(maxDownloadConnections, maxDownloadConnections, streamingPriority);
        configManager.OnConfigChanged += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (e.ChangedConfig.ContainsKey("usenet.max-download-connections")
            || e.ChangedConfig.ContainsKey("usenet.providers")
            || e.ChangedConfig.ContainsKey("usenet.adaptive-connections-enabled"))
        {
            RefreshMaxAllowedConnections();
        }

        if (e.ChangedConfig.ContainsKey("usenet.streaming-priority"))
        {
            var streamingPriority = _configManager.GetStreamingPriority();
            _semaphore.UpdatePriorityOdds(streamingPriority);
        }
    }

    /// <summary>
    /// Gate STAT/HEAD through the PrioritizedSemaphore so that queue operations
    /// (which perform many STATs for InterpolationSearch, PAR2 parsing, etc.)
    /// cannot monopolize all ConnectionPool connections and starve streaming.
    /// Without this, 55 concurrent queue items doing STAT operations bypass the
    /// semaphore and consume all connections, making streaming-priority ineffective.
    /// </summary>
    public override async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var lease = await AcquireConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Release();
        }
    }

    public override async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken)
    {
        var lease = await AcquireConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.StatPipelinedAsync(segmentIds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Release();
        }
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var lease = await AcquireConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Release();
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var lease = await AcquireConnectionLeaseAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Release();
            throw;
        }

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            lease.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var lease = await AcquireConnectionLeaseAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Release();
            throw;
        }

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            lease.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    private async Task<ConnectionLease> AcquireConnectionLeaseAsync(Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AcquireConnectionLeaseAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }
    }

    private async Task<ConnectionLease> AcquireConnectionLeaseAsync(CancellationToken cancellationToken)
    {
        RefreshMaxAllowedConnections();
        var downloadPriorityContext = cancellationToken.GetContext<DownloadPriorityContext>();
        var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.Low;
        var streamLimiters = GetStreamLimiters(downloadPriorityContext, semaphorePriority);

        var acquiredStreamLimiters = new List<IConnectionLimiter>();
        try
        {
            foreach (var streamLimiter in streamLimiters)
            {
                await streamLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquiredStreamLimiters.Add(streamLimiter);
            }

            await _semaphore.WaitAsync(semaphorePriority, cancellationToken).ConfigureAwait(false);
            return new ConnectionLease(_semaphore, acquiredStreamLimiters);
        }
        catch
        {
            foreach (var streamLimiter in acquiredStreamLimiters.AsEnumerable().Reverse())
            {
                try
                {
                    streamLimiter.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The request can complete while a later limiter acquisition is canceled.
                }
            }

            throw;
        }
    }

    private static IReadOnlyList<IConnectionLimiter> GetStreamLimiters
    (
        DownloadPriorityContext? downloadPriorityContext,
        SemaphorePriority semaphorePriority
    )
    {
        if (downloadPriorityContext == null || semaphorePriority != SemaphorePriority.High) return [];

        var streamLimiters = new List<IConnectionLimiter>();
        if (downloadPriorityContext.ConnectionLimiter != null)
            streamLimiters.Add(new SemaphoreSlimConnectionLimiter(downloadPriorityContext.ConnectionLimiter));
        streamLimiters.AddRange(downloadPriorityContext.ConnectionLimiters);
        return streamLimiters;
    }

    private void RefreshMaxAllowedConnections()
    {
        var maxDownloadConnections = _configManager.GetAdaptiveMaxDownloadConnections();
        if (Interlocked.Exchange(ref _maxAllowedConnections, maxDownloadConnections) == maxDownloadConnections)
            return;

        _semaphore.UpdateMaxAllowed(maxDownloadConnections);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var lease = await AcquireConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        return new UsenetExclusiveConnection(_ => lease.Release());
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}

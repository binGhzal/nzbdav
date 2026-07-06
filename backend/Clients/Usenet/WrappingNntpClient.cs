using NzbWebDAV.Clients.Usenet.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class WrappingNntpClient(INntpClient usenetClient) : NntpClient
{
    private INntpClient _usenetClient = usenetClient;

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        _usenetClient.ConnectAsync(host, port, useSsl, cancellationToken);

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        _usenetClient.AuthenticateAsync(user, pass, cancellationToken);

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.StatAsync(segmentId, cancellationToken);

    public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync(
        IReadOnlyList<string> segmentIds, CancellationToken cancellationToken) =>
        _usenetClient.StatPipelinedAsync(segmentIds, cancellationToken);

    // Keep the compatibility wrapper on the current instance so subclasses that override
    // CheckSegmentsAsync keep their batch/pipelined behavior when callers still use CheckAll.
    public override Task CheckAllSegmentsAsync(
        IEnumerable<string> segmentIds, int concurrency, IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        base.CheckAllSegmentsAsync(segmentIds, concurrency, progress, cancellationToken);

    public override Task<SegmentCheckBatch> CheckSegmentsAsync(
        IEnumerable<string> segmentIds, int concurrency, IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        _usenetClient.CheckSegmentsAsync(segmentIds, concurrency, progress, cancellationToken);

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.HeadAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, cancellationToken);

    public override Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken) =>
        _usenetClient.DateAsync(cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken);


    protected void ReplaceUnderlyingClient(INntpClient usenetClient, TimeSpan? drainDelay = null)
    {
        var old = Interlocked.Exchange(ref _usenetClient, usenetClient);
        if (old is IDisposable disposable)
            DisposeAfterDrain(disposable, drainDelay ?? TimeSpan.Zero);
    }

    private static void DisposeAfterDrain(IDisposable disposable, TimeSpan drainDelay)
    {
        if (drainDelay <= TimeSpan.Zero)
        {
            DisposeClientSafely(disposable);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(drainDelay).ConfigureAwait(false);
                DisposeClientSafely(disposable);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to dispose drained NNTP client {ClientType}.", disposable.GetType().FullName);
            }
        });
    }

    private static void DisposeClientSafely(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispose NNTP client {ClientType}.", disposable.GetType().FullName);
        }
    }

    public override void Dispose()
    {
        DisposeClientSafely(_usenetClient);
        GC.SuppressFinalize(this);
    }
}

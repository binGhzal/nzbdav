using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreStreamFile(HttpContext context, ConfigManager configManager) : BaseStoreReadonlyItem
{
    protected HttpContext RequestContext { get; } = context;
    protected ConfigManager ConfigManager { get; } = configManager;

    protected abstract Task<Stream> GetStreamAsync(CancellationToken cancellationToken);

    public override async Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        var connectionLimiter = new SemaphoreSlim(ConfigManager.GetAdaptiveMaxStreamingConnections());
        var globalConnectionLimiter = RequestContext.RequestServices.GetRequiredService<StreamingConnectionLimiter>();
        var activeStreamSlot = await globalConnectionLimiter.WaitForStreamAsync(cancellationToken).ConfigureAwait(false);
        var downloadPriorityContext = new DownloadPriorityContext()
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiters =
            [
                new SemaphoreSlimConnectionLimiter(connectionLimiter),
                globalConnectionLimiter
            ]
        };
        var scopedDownloadPriorityContext = cancellationToken.SetContext(downloadPriorityContext);

        var streamingTimeoutContext = new StreamingTimeoutContext
        {
            PerAttemptTimeout = ConfigManager.GetStreamingSegmentTimeout(),
            MaxRetries = ConfigManager.GetStreamingSegmentRetries()
        };
        var scopedStreamingTimeoutContext = cancellationToken.SetContext(streamingTimeoutContext);
        var activeStreamTracker = RequestContext.RequestServices.GetRequiredService<ActiveStreamTracker>();
        var activeStreamLease = activeStreamTracker.Open(
            RequestContext.Request.Path.Value,
            RequestContext.Request.Headers.UserAgent.ToString());

        var completed = false;
        RequestContext.Response.OnCompleted(() =>
        {
            completed = true;
            scopedDownloadPriorityContext.Dispose();
            scopedStreamingTimeoutContext.Dispose();
            connectionLimiter.Dispose();
            activeStreamLease.Dispose();
            activeStreamSlot.Dispose();
            return Task.CompletedTask;
        });

        try
        {
            return await GetStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!completed)
            {
                scopedDownloadPriorityContext.Dispose();
                scopedStreamingTimeoutContext.Dispose();
                connectionLimiter.Dispose();
                activeStreamLease.Dispose();
                activeStreamSlot.Dispose();
            }

            throw;
        }
    }
}

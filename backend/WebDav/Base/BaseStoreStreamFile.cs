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
    protected abstract Task<Stream> GetStreamAsync(CancellationToken cancellationToken);

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        var connectionLimiter = new SemaphoreSlim(configManager.GetMaxStreamingConnections());
        var globalConnectionLimiter = context.RequestServices.GetRequiredService<StreamingConnectionLimiter>();
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
            PerAttemptTimeout = configManager.GetStreamingSegmentTimeout(),
            MaxRetries = configManager.GetStreamingSegmentRetries()
        };
        var scopedStreamingTimeoutContext = cancellationToken.SetContext(streamingTimeoutContext);
        var activeStreamTracker = context.RequestServices.GetRequiredService<ActiveStreamTracker>();
        var activeStreamLease = activeStreamTracker.Open(
            context.Request.Path.Value,
            context.Request.Headers.UserAgent.ToString());

        context.Response.OnCompleted(() =>
        {
            scopedDownloadPriorityContext.Dispose();
            scopedStreamingTimeoutContext.Dispose();
            connectionLimiter.Dispose();
            activeStreamLease.Dispose();
            return Task.CompletedTask;
        });

        return GetStreamAsync(cancellationToken);
    }
}

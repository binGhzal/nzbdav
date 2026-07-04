using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePriority;

public class ChangeQueuePriorityController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<SabBaseResponse> ChangeQueuePriority(ChangeQueuePriorityRequest request)
    {
        await dbClient
            .UpdateQueueItemsPriorityAsync(request.NzoIds, request.Priority, request.CancellationToken)
            .ConfigureAwait(false);
        queueManager.UpdateInProgressQueueItemsPriority(request.NzoIds, request.Priority);
        queueManager.AwakenQueue();
        return new SabBaseResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await ChangeQueuePriorityRequest.New(RequestContext).ConfigureAwait(false);
        return Ok(await ChangeQueuePriority(request).ConfigureAwait(false));
    }
}

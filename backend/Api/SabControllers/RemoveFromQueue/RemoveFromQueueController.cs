using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromQueueResponse> RemoveFromQueue(RemoveFromQueueRequest request)
    {
        var nzoIds = request.RemoveAll
            ? await dbClient.GetAllQueueItemIdsAsync(request.CancellationToken).ConfigureAwait(false)
            : request.NzoIds;
        nzoIds = nzoIds.Distinct().ToList();

        if (nzoIds.Count > 0)
        {
            await queueManager.RemoveQueueItemsAsync(nzoIds, dbClient, request.CancellationToken).ConfigureAwait(false);
            dbClient.Ctx.EnqueueRcloneVfsForgetPaths(["/nzbs"]);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, string.Join(",", nzoIds));
        }

        return new RemoveFromQueueResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromQueueRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromQueue(request).ConfigureAwait(false));
    }
}

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.PauseResumeQueueItem;

public class PauseResumeQueueItemController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    bool isPaused
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var request = await PauseResumeQueueItemRequest.New(RequestContext).ConfigureAwait(false);
        var priority = isPaused
            ? QueueItem.PriorityOption.Paused
            : QueueItem.PriorityOption.Normal;

        await dbClient
            .UpdateQueueItemsPriorityAsync(request.NzoIds, priority, request.CancellationToken)
            .ConfigureAwait(false);
        queueManager.UpdateInProgressQueueItemsPriority(request.NzoIds, priority);
        queueManager.AwakenQueue();

        return Ok(new PauseResumeQueueItemResponse { Status = true, NzoIds = request.NzoIds.Select(x => x.ToString()).ToList() });
    }

    private class PauseResumeQueueItemResponse : SabBaseResponse
    {
        [JsonPropertyName("nzo_ids")]
        public List<string> NzoIds { get; init; } = [];
    }
}

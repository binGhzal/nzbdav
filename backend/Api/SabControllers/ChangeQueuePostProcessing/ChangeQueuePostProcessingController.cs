using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePostProcessing;

public class ChangeQueuePostProcessingController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var request = await ChangeQueuePostProcessingRequest.New(httpContext).ConfigureAwait(false);
        await dbClient
            .UpdateQueueItemsPostProcessingAsync(request.NzoIds, request.PostProcessing, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(new SabBaseResponse { Status = true });
    }
}

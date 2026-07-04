using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetFiles;

public sealed class GetFilesController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var nzoId = RequestContext.GetRequestParam("value")
                    ?? RequestContext.GetRequestParam("nzo_id");
        if (!Guid.TryParse(nzoId, out var id))
            throw new BadHttpRequestException("Invalid value/nzo_id parameter");

        var queueItem = await dbClient.Ctx.QueueItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, RequestContext.RequestAborted)
            .ConfigureAwait(false);
        if (queueItem is not null)
        {
            var state = (await dbClient.GetLatestQueueLifecycleStatesAsync([id], RequestContext.RequestAborted)
                    .ConfigureAwait(false))
                .GetValueOrDefault(id) ?? "Queued";
            return Ok(new GetFilesResponse
            {
                Status = true,
                NzoId = id.ToString(),
                Files = [GetFilesResponse.FileSlot.FromQueueItem(queueItem, state)]
            });
        }

        var historyItem = await dbClient.Ctx.HistoryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, RequestContext.RequestAborted)
            .ConfigureAwait(false);
        if (historyItem is null)
            return Ok(new GetFilesResponse { Status = true, NzoId = id.ToString(), Files = [] });

        return Ok(new GetFilesResponse
        {
            Status = true,
            NzoId = id.ToString(),
            Files = [GetFilesResponse.FileSlot.FromHistoryItem(historyItem)]
        });
    }
}

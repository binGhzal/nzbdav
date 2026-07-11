using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromHistoryResponse> RemoveFromHistory(RemoveFromHistoryRequest request)
    {
        var nzoIds = request.RemoveAll
            ? await dbClient.Ctx.HistoryItems
                .Where(x => !request.FailedOnly || x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed)
                .Select(x => x.Id)
                .ToListAsync(request.CancellationToken)
                .ConfigureAwait(false)
            : request.NzoIds;
        nzoIds = nzoIds.Distinct().ToList();

        if (nzoIds.Count == 0)
            return new RemoveFromHistoryResponse() { Status = true };

        await using var ownedTransaction = dbClient.Ctx.Database.CurrentTransaction == null
            ? await dbClient.Ctx.Database.BeginTransactionAsync(request.CancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await dbClient.RemoveHistoryItemsAsync(nzoIds, request.DeleteCompletedFiles, request.CancellationToken)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            if (ownedTransaction != null)
                await ownedTransaction.CommitAsync(request.CancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Item already removed by a prior call; SAB API delete is idempotent.
            if (ownedTransaction != null)
                await ownedTransaction.RollbackAsync(request.CancellationToken).ConfigureAwait(false);
        }
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", nzoIds));
        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(RequestContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}

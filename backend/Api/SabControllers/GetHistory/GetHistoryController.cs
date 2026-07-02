using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems.AsNoTracking();
        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        if (request.Category != null)
            query = query.Where(q => q.Category == request.Category);
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(q => q.JobName.Contains(request.Search) || q.FileName.Contains(request.Search));
        if (request.FailedOnly)
            query = query.Where(q => q.DownloadStatus == HistoryItem.DownloadStatusOption.Failed);
        if (request.Statuses.Count > 0)
        {
            var includeCompleted = request.Statuses.Contains("completed");
            var includeFailed = request.Statuses.Contains("failed");
            query = query.Where(q =>
                includeCompleted && q.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                || includeFailed && q.DownloadStatus == HistoryItem.DownloadStatusOption.Failed);
        }

        // get total count
        var totalCountPromise = query
            .CountAsync(request.CancellationToken);
        var totalCountAllPromise = dbClient.Ctx.HistoryItems
            .AsNoTracking()
            .CountAsync(request.CancellationToken);

        // get history items
        var historyItemsPromise = query
            .OrderByDescending(q => q.CreatedAt)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken);

        // await results
        var totalCount = await totalCountPromise.ConfigureAwait(false);
        var totalCountAll = await totalCountAllPromise.ConfigureAwait(false);
        var historyItems = await historyItemsPromise.ConfigureAwait(false);

        // get download folders
        var downloadFolderIds = historyItems.Select(x => x.DownloadDirId).ToHashSet();
        var davItems = await dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => downloadFolderIds.Contains(x.Id))
            .ToArrayAsync(request.CancellationToken).ConfigureAwait(false);
        var davItemsDict = davItems
            .ToDictionary(x => x.Id, x => x);

        // get slots
        var slots = historyItems
            .Select(x =>
                GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    configManager
                )
            )
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
                TotalCountAll = totalCountAll,
                Start = request.Start,
                Limit = request.Limit,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(httpContext, configManager);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }
}

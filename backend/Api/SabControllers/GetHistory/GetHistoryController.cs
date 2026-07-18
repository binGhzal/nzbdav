using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

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
        var hasActiveRepairJobs = await HistoryVisibilityPolicy
            .HasActiveRepairJobsAsync(dbClient.Ctx, request.CancellationToken)
            .ConfigureAwait(false);
        IQueryable<HistoryItem> query = GetHistoryItemsVisibleToSab(hasActiveRepairJobs);
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

        // Keep the scoped EF DbContext single-threaded. Running these queries concurrently can
        // intermittently fail under live refresh with "A second operation was started..." errors.
        var totalCount = await query
            .CountAsync(request.CancellationToken)
            .ConfigureAwait(false);
        var totalCountAll = IsUnfiltered(request)
            ? totalCount
            : await GetHistoryItemsVisibleToSab(hasActiveRepairJobs)
                .CountAsync(request.CancellationToken)
                .ConfigureAwait(false);

        // get history items
        var historyItems = await query
            .OrderByDescending(q => q.CreatedAt)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken)
            .ConfigureAwait(false);

        // get download folders only for completed rows that actually have a persisted folder
        var downloadFolderIds = historyItems
            .Select(x => x.DownloadDirId)
            .OfType<Guid>()
            .ToHashSet();
        var davItemsDict = downloadFolderIds.Count == 0
            ? new Dictionary<Guid, DavItem>()
            : (await dbClient.Ctx.Items
                    .AsNoTracking()
                    .Where(x => downloadFolderIds.Contains(x.Id))
                    .ToArrayAsync(request.CancellationToken).ConfigureAwait(false))
                .ToDictionary(x => x.Id, x => x);

        // get slots
        var slots = historyItems
            .Select(x =>
                GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    ConfigManager
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

    private IQueryable<HistoryItem> GetHistoryItemsVisibleToSab(bool hasActiveRepairJobs) =>
        HistoryVisibilityPolicy.VisibleToSab(
            dbClient.Ctx.HistoryItems.AsNoTracking(),
            dbClient.Ctx,
            hasActiveRepairJobs);

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(RequestContext, ConfigManager);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }

    private static bool IsUnfiltered(GetHistoryRequest request) =>
        request.NzoIds.Count == 0
        && request.Category == null
        && string.IsNullOrWhiteSpace(request.Search)
        && !request.FailedOnly
        && request.Statuses.Count == 0;
}

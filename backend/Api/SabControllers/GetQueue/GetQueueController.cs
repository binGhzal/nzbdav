using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get in progress item
        var inProgressQueueItems = queueManager
            .GetInProgressQueueItems()
            .Where(x => request.Category == null || x.queueItem.Category == request.Category)
            .Where(x => MatchesRequestFilters(x.queueItem, request, status: "downloading"))
            .ToList();
        var inProgressQueueItemIds = inProgressQueueItems
            .Select(x => x.queueItem.Id)
            .ToHashSet();
        var progressPercentageById = inProgressQueueItems
            .ToDictionary(x => x.queueItem.Id, x => x.progress);
        var activePageItems = inProgressQueueItems
            .Skip(request.Start)
            .Take(request.Limit)
            .ToList();

        // get total count
        var ct = request.CancellationToken;
        var priorities = request.Priorities
            .Select(x => (QueueItem.PriorityOption)(int)x)
            .ToHashSet();
        var waitingStatuses = GetWaitingItemStatuses(request);
        var includeWaitingItems = request.Statuses.Count == 0 || waitingStatuses.Count > 0;
        var waitingTotalCount = includeWaitingItems
            ? await dbClient.GetQueueItemsCount(
                request.Category,
                request.NzoIds,
                request.Search,
                priorities,
                waitingStatuses,
                ct,
                inProgressQueueItemIds).ConfigureAwait(false)
            : 0;
        var totalCount = inProgressQueueItems.Count + waitingTotalCount;
        var totalCountAll = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items
        var activeCountOnPage = activePageItems.Count;
        var waitingStart = Math.Max(0, request.Start - inProgressQueueItems.Count);
        var waitingLimit = Math.Max(0, request.Limit - activeCountOnPage);
        var queueItems = includeWaitingItems && waitingLimit > 0
            ? await dbClient.GetQueueItems(
                request.Category,
                inProgressQueueItemIds,
                request.NzoIds,
                request.Search,
                priorities,
                waitingStatuses,
                waitingStart,
                waitingLimit,
                ct).ConfigureAwait(false)
            : [];

        // get slots
        var visibleQueueItems = activePageItems.Select(x => x.queueItem).Concat(queueItems);
        var slots = visibleQueueItems
            .Take(request.Limit)
            .Select((queueItem, index) =>
            {
                var isInProgress = progressPercentageById.TryGetValue(queueItem.Id, out var percentage);
                var status = isInProgress
                    ? "Downloading"
                    : queueItem.Priority == QueueItem.PriorityOption.Paused ? "Paused" : "Queued";
                return GetQueueResponse.QueueSlot.FromQueueItem(queueItem, request.Start + index, percentage, status);
            })
            .ToList();
        var sizeLeftBytes = slots
            .Select(x => double.TryParse(x.SizeLeftInMB, out var mbLeft) ? mbLeft * 1024 * 1024 : 0)
            .Sum();
        var sizeBytes = slots
            .Select(x => double.TryParse(x.SizeInMB, out var mb) ? mb * 1024 * 1024 : 0)
            .Sum();
        var isPaused = configManager.IsQueuePaused();
        var queueStatus = isPaused
            ? "Paused"
            : inProgressQueueItems.Count > 0 ? "Downloading"
            : totalCount > 0 ? "Queued"
            : "Idle";

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Status = queueStatus,
                Paused = isPaused,
                PausedAll = isPaused,
                Slots = slots,
                TotalCount = totalCount,
                TotalCountAll = totalCountAll,
                Start = request.Start,
                Limit = request.Limit,
                SizeInMB = GetQueueResponse.QueueSlot.FormatSizeMB((long)sizeBytes),
                SizeLeftInMB = GetQueueResponse.QueueSlot.FormatSizeMB((long)sizeLeftBytes),
                Size = $"{GetQueueResponse.QueueSlot.FormatSizeMB((long)sizeBytes)} MB",
                SizeLeft = $"{GetQueueResponse.QueueSlot.FormatSizeMB((long)sizeLeftBytes)} MB",
            }
        };
    }

    private static bool MatchesRequestFilters(QueueItem queueItem, GetQueueRequest request, string status)
    {
        if (request.NzoIds.Count > 0 && !request.NzoIds.Contains(queueItem.Id)) return false;
        if (!string.IsNullOrWhiteSpace(request.Search)
            && !queueItem.JobName.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
            && !queueItem.FileName.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.Priorities.Count > 0
            && !request.Priorities.Contains((GetQueueRequest.QueuePriorityFilter)(int)queueItem.Priority))
            return false;
        if (request.Statuses.Count > 0 && !request.Statuses.Contains(status.ToLowerInvariant()))
            return false;
        return true;
    }

    private static HashSet<string> GetWaitingItemStatuses(GetQueueRequest request)
    {
        if (request.Statuses.Count == 0) return [];

        var statuses = new HashSet<string>();
        if (request.Statuses.Contains("queued")) statuses.Add("queued");
        if (request.Statuses.Contains("paused")) statuses.Add("paused");
        return statuses;
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}

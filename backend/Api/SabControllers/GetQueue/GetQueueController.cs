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
        // get total count
        var ct = request.CancellationToken;
        var priorities = request.Priorities
            .Select(x => (QueueItem.PriorityOption)(int)x)
            .ToHashSet();
        var sortOptions = new DavDatabaseClient.QueueSortOptions(
            ToDatabaseSortField(request.SortField),
            request.SortDescending);
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
        var waitingLimit = Math.Max(0, request.Start + request.Limit);
        var queueItems = includeWaitingItems && waitingLimit > 0
            ? await dbClient.GetQueueItems(
                request.Category,
                inProgressQueueItemIds,
                request.NzoIds,
                request.Search,
                priorities,
                waitingStatuses,
                sortOptions,
                0,
                waitingLimit,
                ct).ConfigureAwait(false)
            : [];

        // get slots
        var visibleQueueItems = inProgressQueueItems
            .Select(x => new QueueListItem(x.queueItem, x.progress, "Downloading"))
            .Concat(queueItems.Select(x => new QueueListItem(
                x,
                ProgressPercentage: null,
                x.Priority == QueueItem.PriorityOption.Paused ? "Paused" : "Queued")));
        var slots = QueueListItem.Order(visibleQueueItems, request)
            .Skip(request.Start)
            .Take(request.Limit)
            .Select((queueItem, index) =>
            {
                var percentage = queueItem.ProgressPercentage ?? 0;
                return GetQueueResponse.QueueSlot.FromQueueItem(
                    queueItem.QueueItem,
                    request.Start + index,
                    percentage,
                    queueItem.Status);
            })
            .ToList();
        var sizeLeftBytes = slots
            .Select(x => double.TryParse(x.SizeLeftInMB, out var mbLeft) ? mbLeft * 1024 * 1024 : 0)
            .Sum();
        var sizeBytes = slots
            .Select(x => double.TryParse(x.SizeInMB, out var mb) ? mb * 1024 * 1024 : 0)
            .Sum();
        var isPaused = ConfigManager.IsQueuePaused();
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

    private static DavDatabaseClient.QueueSortField ToDatabaseSortField(GetQueueRequest.QueueSortField field)
    {
        return field switch
        {
            GetQueueRequest.QueueSortField.Name => DavDatabaseClient.QueueSortField.Name,
            GetQueueRequest.QueueSortField.Category => DavDatabaseClient.QueueSortField.Category,
            GetQueueRequest.QueueSortField.Status => DavDatabaseClient.QueueSortField.Status,
            GetQueueRequest.QueueSortField.Size => DavDatabaseClient.QueueSortField.Size,
            GetQueueRequest.QueueSortField.CreatedAt => DavDatabaseClient.QueueSortField.CreatedAt,
            _ => DavDatabaseClient.QueueSortField.Priority
        };
    }

    private sealed record QueueListItem(QueueItem QueueItem, int? ProgressPercentage, string Status)
    {
        public static IEnumerable<QueueListItem> Order(IEnumerable<QueueListItem> items, GetQueueRequest request)
        {
            var ordered = request.SortField switch
            {
                GetQueueRequest.QueueSortField.Name => request.SortDescending
                    ? items.OrderByDescending(x => x.QueueItem.JobName, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(x => x.QueueItem.JobName, StringComparer.OrdinalIgnoreCase),
                GetQueueRequest.QueueSortField.Category => request.SortDescending
                    ? items.OrderByDescending(x => x.QueueItem.Category, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(x => x.QueueItem.Category, StringComparer.OrdinalIgnoreCase),
                GetQueueRequest.QueueSortField.Status => request.SortDescending
                    ? items.OrderByDescending(x => GetStatusWeight(x.Status))
                    : items.OrderBy(x => GetStatusWeight(x.Status)),
                GetQueueRequest.QueueSortField.Size => request.SortDescending
                    ? items.OrderByDescending(x => x.QueueItem.TotalSegmentBytes)
                    : items.OrderBy(x => x.QueueItem.TotalSegmentBytes),
                GetQueueRequest.QueueSortField.CreatedAt => request.SortDescending
                    ? items.OrderByDescending(x => x.QueueItem.CreatedAt)
                    : items.OrderBy(x => x.QueueItem.CreatedAt),
                _ => request.SortDescending
                    ? items.OrderByDescending(x => x.QueueItem.Priority)
                    : items.OrderBy(x => x.QueueItem.Priority)
            };

            return ordered
                .ThenByDescending(x => x.QueueItem.Priority)
                .ThenBy(x => x.QueueItem.CreatedAt)
                .ThenBy(x => x.QueueItem.Id);
        }

        private static int GetStatusWeight(string status)
        {
            return status.ToLowerInvariant() switch
            {
                "downloading" => 2,
                "queued" => 1,
                "paused" => 0,
                _ => -1
            };
        }
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(RequestContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        var rawInProgressQueueItems = queueManager
            .GetInProgressQueueItems()
            .Where(x => request.Category == null || x.queueItem.Category == request.Category)
            .ToList();
        var latestActiveStates = await dbClient.GetLatestQueueLifecycleStatesAsync(
                rawInProgressQueueItems.Select(x => x.queueItem.Id).ToHashSet(),
                request.CancellationToken)
            .ConfigureAwait(false);
        var inProgressQueueItems = rawInProgressQueueItems
            .Select(x => new
            {
                x.queueItem,
                x.progress,
                status = NormalizeQueueStatus(latestActiveStates.GetValueOrDefault(x.queueItem.Id) ?? "Downloading")
            })
            .Where(x => MatchesRequestFilters(x.queueItem, request, x.status))
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
        var includePostDownloadVerifyItems = ShouldIncludePostDownloadVerifyItems(request);
        var postDownloadVerifyTotalCount = includePostDownloadVerifyItems
            ? await CountPostDownloadVerifyItemsAsync(request, ct).ConfigureAwait(false)
            : 0;
        var includePostDownloadRepairItems = ShouldIncludePostDownloadRepairItems(request);
        var postDownloadRepairTotalCount = includePostDownloadRepairItems
            ? await CountPostDownloadRepairItemsAsync(request, ct).ConfigureAwait(false)
            : 0;
        var totalCount = inProgressQueueItems.Count
                         + waitingTotalCount
                         + postDownloadVerifyTotalCount
                         + postDownloadRepairTotalCount;
        var totalCountAll = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false)
                            + await CountPostDownloadVerifyItemsAsync(
                                    request,
                                    ct,
                                    includeRegardlessOfStatus: true,
                                    onlyCategoryFilter: true)
                                .ConfigureAwait(false)
                            + await CountPostDownloadRepairItemsAsync(
                                    request,
                                    ct,
                                    includeRegardlessOfStatus: true,
                                    onlyCategoryFilter: true)
                                .ConfigureAwait(false);

        List<GetQueueResponse.QueueSlot> slots;
        if (inProgressQueueItems.Count == 0
            && postDownloadVerifyTotalCount == 0
            && postDownloadRepairTotalCount == 0)
        {
            // Common live-refresh path: with no active rows to merge, let the database do paging
            // instead of over-fetching start + limit rows and sorting/skipping them in memory.
            var queueItems = includeWaitingItems && request.Limit > 0
                ? await dbClient.GetQueueItems(
                    request.Category,
                    inProgressQueueItemIds,
                    request.NzoIds,
                    request.Search,
                    priorities,
                    waitingStatuses,
                    sortOptions,
                    request.Start,
                    request.Limit,
                    ct).ConfigureAwait(false)
                : [];
            var hints = await dbClient.GetQueuePriorityHintsAsync(
                    queueItems.Select(x => x.Id).ToHashSet(),
                    ct)
                .ConfigureAwait(false);
            var hintsByQueueItemId = hints.ToDictionary(x => x.QueueItemId);
            slots = queueItems
                .Select((queueItem, index) =>
                    GetQueueResponse.QueueSlot.FromQueueItem(
                        queueItem,
                        request.Start + index,
                        0,
                        queueItem.Priority == QueueItem.PriorityOption.Paused ? "Paused" : "Queued",
                        hintsByQueueItemId.GetValueOrDefault(queueItem.Id)))
                .ToList();
        }
        else
        {
            // Active rows come from memory, so this path still merges active + waiting items before
            // applying the SAB page window.
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
            var postDownloadVerifyItems = includePostDownloadVerifyItems && waitingLimit > 0
                ? await GetPostDownloadVerifyItemsAsync(request, waitingLimit, ct).ConfigureAwait(false)
                : [];
            var postDownloadRepairItems = includePostDownloadRepairItems && waitingLimit > 0
                ? await GetPostDownloadRepairItemsAsync(request, waitingLimit, ct).ConfigureAwait(false)
                : [];

            var visibleQueueItems = inProgressQueueItems
                .Select(x => new QueueListItem(x.queueItem, x.progress, x.status, null))
                .Concat(queueItems.Select(x => new QueueListItem(
                    x,
                    null,
                    x.Priority == QueueItem.PriorityOption.Paused ? "Paused" : "Queued",
                    null)))
                .Concat(postDownloadVerifyItems.Select(x => QueueListItem.FromPostDownloadWorker(x)))
                .Concat(postDownloadRepairItems.Select(x => QueueListItem.FromPostDownloadWorker(x)))
                .ToList();
            var hints = await dbClient.GetQueuePriorityHintsAsync(
                    visibleQueueItems
                        .Where(x => x.QueueItem != null)
                        .Select(x => x.QueueItem!.Id)
                        .ToHashSet(),
                    ct)
                .ConfigureAwait(false);
            var hintsByQueueItemId = hints.ToDictionary(x => x.QueueItemId);
            visibleQueueItems = visibleQueueItems
                .Select(x => x.QueueItem == null
                    ? x
                    : x with { PriorityHint = hintsByQueueItemId.GetValueOrDefault(x.QueueItem.Id) })
                .ToList();
            var orderedVisibleQueueItems = QueueListItem.Order(visibleQueueItems, request)
                .Skip(request.Start)
                .Take(request.Limit)
                .ToList();
            slots = orderedVisibleQueueItems
                .Select((queueItem, index) =>
                    queueItem.ToSlot(request.Start + index))
                .ToList();
        }
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
            : postDownloadRepairTotalCount > 0 ? "Repairing"
            : postDownloadVerifyTotalCount > 0 ? "Verifying"
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

    private async Task<int> CountPostDownloadVerifyItemsAsync(
        GetQueueRequest request,
        CancellationToken ct,
        bool includeRegardlessOfStatus = false,
        bool onlyCategoryFilter = false)
    {
        if (!includeRegardlessOfStatus && !ShouldIncludePostDownloadVerifyItems(request)) return 0;
        return await GetPostDownloadVerifyItemsQuery(request, onlyCategoryFilter)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<List<PostDownloadWorkerQueueItem>> GetPostDownloadVerifyItemsAsync(
        GetQueueRequest request,
        int limit,
        CancellationToken ct)
    {
        if (!ShouldIncludePostDownloadVerifyItems(request) || limit <= 0) return [];
        return await GetPostDownloadVerifyItemsQuery(request, onlyCategoryFilter: false)
            .OrderByDescending(x => x.WorkerPriority)
            .ThenBy(x => x.HistoryCreatedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private IQueryable<PostDownloadWorkerQueueItem> GetPostDownloadVerifyItemsQuery(
        GetQueueRequest request,
        bool onlyCategoryFilter)
    {
        var activeStatuses = new[]
        {
            WorkerJob.JobStatus.Pending,
            WorkerJob.JobStatus.Leased,
            WorkerJob.JobStatus.Retry
        };
        var query =
            from workerJob in dbClient.Ctx.WorkerJobs.AsNoTracking()
            join historyItem in dbClient.Ctx.HistoryItems.AsNoTracking()
                on workerJob.TargetId equals historyItem.DownloadDirId
            where workerJob.Kind == WorkerJob.JobKind.Verify
                  && activeStatuses.Contains(workerJob.Status)
                  && workerJob.PayloadJson != null
                  && workerJob.PayloadJson.Contains("post_download_verify")
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
            select new PostDownloadWorkerQueueItem
            {
                HistoryItemId = historyItem.Id,
                HistoryCreatedAt = historyItem.CreatedAt,
                FileName = historyItem.FileName,
                JobName = historyItem.JobName,
                Category = historyItem.Category,
                TotalSegmentBytes = historyItem.TotalSegmentBytes,
                WorkerPriority = workerJob.Priority,
                Status = "Verifying"
            };

        if (request.Category != null)
            query = query.Where(x => x.Category == request.Category);
        if (onlyCategoryFilter) return query;

        if (request.NzoIds.Count > 0)
            query = query.Where(x => request.NzoIds.Contains(x.HistoryItemId));
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.JobName.Contains(request.Search) || x.FileName.Contains(request.Search));
        if (request.Priorities.Count > 0)
            query = ApplyPostDownloadWorkerPriorityFilter(query, request.Priorities);

        return query;
    }

    private async Task<int> CountPostDownloadRepairItemsAsync(
        GetQueueRequest request,
        CancellationToken ct,
        bool includeRegardlessOfStatus = false,
        bool onlyCategoryFilter = false)
    {
        if (!includeRegardlessOfStatus && !ShouldIncludePostDownloadRepairItems(request)) return 0;
        return await GetPostDownloadRepairItemsQuery(request, onlyCategoryFilter)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<List<PostDownloadWorkerQueueItem>> GetPostDownloadRepairItemsAsync(
        GetQueueRequest request,
        int limit,
        CancellationToken ct)
    {
        if (!ShouldIncludePostDownloadRepairItems(request) || limit <= 0) return [];
        return await GetPostDownloadRepairItemsQuery(request, onlyCategoryFilter: false)
            .OrderByDescending(x => x.WorkerPriority)
            .ThenBy(x => x.HistoryCreatedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private IQueryable<PostDownloadWorkerQueueItem> GetPostDownloadRepairItemsQuery(
        GetQueueRequest request,
        bool onlyCategoryFilter)
    {
        var activeStatuses = new[]
        {
            WorkerJob.JobStatus.Pending,
            WorkerJob.JobStatus.Leased,
            WorkerJob.JobStatus.Retry
        };
        var repairRows =
            from workerJob in dbClient.Ctx.WorkerJobs.AsNoTracking()
            join davItem in dbClient.Ctx.Items.AsNoTracking()
                on workerJob.TargetId equals davItem.Id
            join historyItem in dbClient.Ctx.HistoryItems.AsNoTracking()
                on davItem.HistoryItemId equals historyItem.Id
            where workerJob.Kind == WorkerJob.JobKind.Repair
                  && activeStatuses.Contains(workerJob.Status)
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
            select new
            {
                HistoryItemId = historyItem.Id,
                HistoryCreatedAt = historyItem.CreatedAt,
                historyItem.FileName,
                historyItem.JobName,
                historyItem.Category,
                historyItem.TotalSegmentBytes,
                WorkerPriority = workerJob.Priority
            };

        var query = repairRows
            .GroupBy(x => new
            {
                x.HistoryItemId,
                x.HistoryCreatedAt,
                x.FileName,
                x.JobName,
                x.Category,
                x.TotalSegmentBytes
            })
            .Select(x => new PostDownloadWorkerQueueItem
            {
                HistoryItemId = x.Key.HistoryItemId,
                HistoryCreatedAt = x.Key.HistoryCreatedAt,
                FileName = x.Key.FileName,
                JobName = x.Key.JobName,
                Category = x.Key.Category,
                TotalSegmentBytes = x.Key.TotalSegmentBytes,
                WorkerPriority = x.Max(row => row.WorkerPriority),
                Status = "Repairing"
            });

        if (request.Category != null)
            query = query.Where(x => x.Category == request.Category);
        if (onlyCategoryFilter) return query;

        if (request.NzoIds.Count > 0)
            query = query.Where(x => request.NzoIds.Contains(x.HistoryItemId));
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.JobName.Contains(request.Search) || x.FileName.Contains(request.Search));
        if (request.Priorities.Count > 0)
            query = ApplyPostDownloadWorkerPriorityFilter(query, request.Priorities);

        return query;
    }

    private static bool ShouldIncludePostDownloadVerifyItems(GetQueueRequest request)
    {
        return request.Statuses.Count == 0 || request.Statuses.Contains("verifying");
    }

    private static bool ShouldIncludePostDownloadRepairItems(GetQueueRequest request)
    {
        return request.Statuses.Count == 0 || request.Statuses.Contains("repairing");
    }

    private static IQueryable<PostDownloadWorkerQueueItem> ApplyPostDownloadWorkerPriorityFilter(
        IQueryable<PostDownloadWorkerQueueItem> query,
        HashSet<GetQueueRequest.QueuePriorityFilter> priorities)
    {
        var includeForce = priorities.Contains(GetQueueRequest.QueuePriorityFilter.Force);
        var includeHigh = priorities.Contains(GetQueueRequest.QueuePriorityFilter.High);
        var includeNormal = priorities.Contains(GetQueueRequest.QueuePriorityFilter.Normal);
        var includeLow = priorities.Contains(GetQueueRequest.QueuePriorityFilter.Low);

        return query.Where(x =>
            includeForce && x.WorkerPriority >= 100
            || includeHigh && x.WorkerPriority >= 50 && x.WorkerPriority < 100
            || includeNormal && x.WorkerPriority >= 0 && x.WorkerPriority < 50
            || includeLow && x.WorkerPriority < 0);
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

    private static string NormalizeQueueStatus(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "verifying" => "Verifying",
            "repairing" => "Repairing",
            "moving" => "Moving",
            "completed" => "Completed",
            "failed" => "Failed",
            "paused" => "Paused",
            "queued" => "Queued",
            _ => "Downloading"
        };
    }

    private sealed record QueueListItem(
        QueueItem? QueueItem,
        PostDownloadWorkerQueueItem? PostDownloadWorkerItem,
        int? ProgressPercentage,
        string Status,
        QueuePriorityHint? PriorityHint)
    {
        public QueueListItem(
            QueueItem queueItem,
            int? progressPercentage,
            string status,
            QueuePriorityHint? priorityHint)
            : this(queueItem, null, progressPercentage, status, priorityHint)
        {
        }

        public static QueueListItem FromPostDownloadWorker(PostDownloadWorkerQueueItem item) =>
            new(null, item, 100, item.Status, null);

        public GetQueueResponse.QueueSlot ToSlot(int index)
        {
            if (PostDownloadWorkerItem != null)
            {
                return GetQueueResponse.QueueSlot.FromPostDownloadWorker(
                    PostDownloadWorkerItem.HistoryItemId,
                    PostDownloadWorkerItem.FileName,
                    PostDownloadWorkerItem.Category,
                    PostDownloadWorkerItem.TotalSegmentBytes,
                    GetPriorityName(GetPriority()),
                    PostDownloadWorkerItem.Status,
                    index);
            }

            var percentage = ProgressPercentage ?? 0;
            return GetQueueResponse.QueueSlot.FromQueueItem(
                QueueItem!,
                index,
                percentage,
                Status,
                PriorityHint);
        }

        public static IEnumerable<QueueListItem> Order(IEnumerable<QueueListItem> items, GetQueueRequest request)
        {
            var ordered = request.SortField switch
            {
                GetQueueRequest.QueueSortField.Name => request.SortDescending
                    ? items.OrderByDescending(x => x.GetJobName(), StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(x => x.GetJobName(), StringComparer.OrdinalIgnoreCase),
                GetQueueRequest.QueueSortField.Category => request.SortDescending
                    ? items.OrderByDescending(x => x.GetCategory(), StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(x => x.GetCategory(), StringComparer.OrdinalIgnoreCase),
                GetQueueRequest.QueueSortField.Status => request.SortDescending
                    ? items.OrderByDescending(x => GetStatusWeight(x.Status))
                    : items.OrderBy(x => GetStatusWeight(x.Status)),
                GetQueueRequest.QueueSortField.Size => request.SortDescending
                    ? items.OrderByDescending(x => x.GetTotalSegmentBytes())
                    : items.OrderBy(x => x.GetTotalSegmentBytes()),
                GetQueueRequest.QueueSortField.CreatedAt => request.SortDescending
                    ? items.OrderByDescending(x => x.GetCreatedAt())
                    : items.OrderBy(x => x.GetCreatedAt()),
                _ => request.SortDescending
                    ? items.OrderByDescending(GetEffectivePriority).ThenByDescending(GetPriorityScore)
                    : items.OrderBy(GetEffectivePriority).ThenBy(GetPriorityScore)
            };

            return ordered
                .ThenByDescending(GetEffectivePriority)
                .ThenByDescending(GetPriorityScore)
                .ThenByDescending(x => x.GetPriority())
                .ThenBy(x => x.GetCreatedAt())
                .ThenBy(x => x.GetId());
        }

        private static int GetEffectivePriority(QueueListItem item)
        {
            var hint = item.PriorityHint;
            if (hint is null || !hint.ApplyToScheduling || hint.ExpiresAt < DateTimeOffset.UtcNow)
                return (int)item.GetPriority();
            return Math.Max((int)item.GetPriority(), (int)hint.EffectivePriority);
        }

        private static int GetPriorityScore(QueueListItem item)
        {
            var hint = item.PriorityHint;
            return hint is null || !hint.ApplyToScheduling || hint.ExpiresAt < DateTimeOffset.UtcNow
                ? 0
                : hint.Score;
        }

        private static int GetStatusWeight(string status)
        {
            return status.ToLowerInvariant() switch
            {
                "repairing" => 5,
                "verifying" => 4,
                "moving" => 3,
                "downloading" => 2,
                "queued" => 1,
                "paused" => 0,
                _ => -1
            };
        }

        private Guid GetId() => QueueItem?.Id ?? PostDownloadWorkerItem!.HistoryItemId;

        private string GetJobName() => QueueItem?.JobName ?? PostDownloadWorkerItem!.JobName;

        private string GetCategory() => QueueItem?.Category ?? PostDownloadWorkerItem!.Category;

        private long GetTotalSegmentBytes() => QueueItem?.TotalSegmentBytes ?? PostDownloadWorkerItem!.TotalSegmentBytes;

        private DateTime GetCreatedAt() => QueueItem?.CreatedAt ?? PostDownloadWorkerItem!.HistoryCreatedAt;

        private QueueItem.PriorityOption GetPriority()
        {
            if (QueueItem != null) return QueueItem.Priority;
            return PostDownloadWorkerItem!.WorkerPriority switch
            {
                >= 100 => QueueItem.PriorityOption.Force,
                >= 50 => QueueItem.PriorityOption.High,
                < 0 => QueueItem.PriorityOption.Low,
                _ => QueueItem.PriorityOption.Normal
            };
        }

        private static string GetPriorityName(QueueItem.PriorityOption priority)
        {
            return priority.ToString();
        }
    }

    private sealed class PostDownloadWorkerQueueItem
    {
        public Guid HistoryItemId { get; init; }
        public DateTime HistoryCreatedAt { get; init; }
        public string FileName { get; init; } = "";
        public string JobName { get; init; } = "";
        public string Category { get; init; } = "";
        public long TotalSegmentBytes { get; init; }
        public int WorkerPriority { get; init; }
        public string Status { get; init; } = "";
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(RequestContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}

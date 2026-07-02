using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
    private const int MaxWorkerJobErrorLength = 1024;

    public DavDatabaseContext Ctx => ctx;

    // file
    public Task<DavItem?> GetFileById(string id)
    {
        var guid = Guid.Parse(id);
        return ctx.Items.Where(i => i.Id == guid).FirstOrDefaultAsync();
    }

    public Task<List<DavItem>> GetFilesByIdPrefix(string prefix)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(x => x.ParentId == dirId)
            .ToListAsync(ct);
    }

    public Task<DavItem?> GetDirectoryChildAsync(Guid dirId, string childName, CancellationToken ct = default)
    {
        return ctx.Items.FirstOrDefaultAsync(x => x.ParentId == dirId && x.Name == childName, ct);
    }

    public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
    {
        if (dirId == DavItem.Root.Id)
        {
            return await Ctx.Items.SumAsync(x => x.FileSize, ct).ConfigureAwait(false) ?? 0;
        }

        const string sql = @"
            WITH RECURSIVE RecursiveChildren AS (
                SELECT Id, FileSize
                FROM DavItems
                WHERE ParentId = @parentId

                UNION ALL

                SELECT d.Id, d.FileSize
                FROM DavItems d
                INNER JOIN RecursiveChildren rc ON d.ParentId = rc.Id
            )
            SELECT IFNULL(SUM(FileSize), 0)
            FROM RecursiveChildren;
        ";
        var connection = Ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@parentId";
        parameter.Value = dirId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    // usenet files
    public async Task<DavNzbFile?> GetDavNzbFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavNzbFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.NzbFiles
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavRarFile?> GetDavRarFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavRarFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.RarFiles
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavMultipartFile?> GetDavMultipartFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavMultipartFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.MultipartFiles
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    // queue
    public async Task<(QueueItem? queueItem, Stream? queueNzbStream)> GetTopQueueItem
    (
        IReadOnlyCollection<Guid>? excludeIds = null,
        CancellationToken ct = default
    )
    {
        // read queue item from database
        var nowTime = DateTime.Now;
        var queueItems = Ctx.QueueItems.AsQueryable();
        if (excludeIds is { Count: > 0 })
            queueItems = queueItems.Where(q => !excludeIds.Contains(q.Id));

        var queueItem = await queueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
            .Where(q => q.Priority != QueueItem.PriorityOption.Paused)
            .Skip(0)
            .Take(1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var queueNzbStream = queueItem != null
            ? await ReadQueueNzbStreamAsync(queueItem.Id, ct).ConfigureAwait(false)
            : null;

        // return
        return (queueItem, queueNzbStream);
    }

    public async Task<(QueueItem? queueItem, Stream? queueNzbStream, WorkerJob? workerJob)> LeaseTopQueueItemAsync
    (
        IReadOnlyCollection<Guid>? excludeIds,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var localNow = now.HasValue ? now.Value.LocalDateTime : DateTime.Now;
        var leaseExpiresAt = referenceTime + leaseDuration;

        await using var transaction = await Ctx.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var queueItems = Ctx.QueueItems.AsQueryable();
        if (excludeIds is { Count: > 0 })
            queueItems = queueItems.Where(q => !excludeIds.Contains(q.Id));

        var candidates = queueItems
            .Where(q => q.PauseUntil == null || localNow >= q.PauseUntil)
            .Where(q => q.Priority != QueueItem.PriorityOption.Paused)
            .GroupJoin(
                Ctx.WorkerJobs.Where(j => j.Kind == WorkerJob.JobKind.Download),
                q => q.Id,
                j => j.TargetId,
                (queueItem, workerJobs) => new { queueItem, workerJobs })
            .SelectMany(
                x => x.workerJobs.DefaultIfEmpty(),
                (x, workerJob) => new { x.queueItem, workerJob })
            .Where(x => x.workerJob == null
                        || (x.workerJob.Status == WorkerJob.JobStatus.Pending
                            || x.workerJob.Status == WorkerJob.JobStatus.Retry
                            || x.workerJob.Status == WorkerJob.JobStatus.Leased
                            && x.workerJob.LeaseExpiresAt <= referenceTime)
                        && x.workerJob.AvailableAt <= referenceTime
                        && (x.workerJob.LeaseExpiresAt == null || x.workerJob.LeaseExpiresAt <= referenceTime))
            .OrderByDescending(x => x.queueItem.Priority)
            .ThenBy(x => x.queueItem.CreatedAt)
            .ThenBy(x => x.queueItem.Id);

        var selected = await candidates
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (selected is null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (null, null, null);
        }

        var workerJob = selected.workerJob;
        if (workerJob is null)
        {
            workerJob = new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = WorkerJob.JobKind.Download,
                TargetId = selected.queueItem.Id,
                Priority = (int)selected.queueItem.Priority,
                Attempts = 0,
                CreatedAt = referenceTime,
                UpdatedAt = referenceTime,
                AvailableAt = referenceTime
            };
            Ctx.WorkerJobs.Add(workerJob);
        }

        workerJob.Status = WorkerJob.JobStatus.Leased;
        workerJob.Priority = (int)selected.queueItem.Priority;
        workerJob.LeaseOwner = owner;
        workerJob.LeaseExpiresAt = leaseExpiresAt;
        workerJob.Attempts += 1;
        workerJob.UpdatedAt = referenceTime;
        workerJob.LastError = null;

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var queueNzbStream = await ReadQueueNzbStreamAsync(selected.queueItem.Id, ct).ConfigureAwait(false);
        return (selected.queueItem, queueNzbStream, workerJob);
    }

    private async Task<Stream?> ReadQueueNzbStreamAsync(Guid queueItemId, CancellationToken ct)
    {
        // attempt to read nzb contents from blob-store.
        var queueNzbStream = BlobStore.ReadBlob(queueItemId);
        if (queueNzbStream != null) return queueNzbStream;

        // otherwise, read nzb contents from database.
        var queueNzbContents = await Ctx.QueueNzbContents
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct)
            .ConfigureAwait(false);

        return queueNzbContents != null
            ? new MemoryStream(Encoding.UTF8.GetBytes(queueNzbContents.NzbContents))
            : null;
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        return GetQueueItems(category, null, null, null, null, null, null, start, limit, ct);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        IReadOnlyCollection<Guid>? excludeIds = null,
        IReadOnlyCollection<Guid>? nzoIds = null,
        string? search = null,
        IReadOnlyCollection<QueueItem.PriorityOption>? priorities = null,
        IReadOnlyCollection<string>? statuses = null,
        QueueSortOptions? sortOptions = null,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        var queueItems = GetQueueItemsQuery(category, nzoIds, search, priorities, statuses);
        if (excludeIds is { Count: > 0 })
            queueItems = queueItems.Where(q => !excludeIds.Contains(q.Id));

        return ApplyQueueSort(queueItems, sortOptions)
            .Skip(start)
            .Take(limit)
            .ToArrayAsync(cancellationToken: ct);
    }

    public Task<int> GetQueueItemsCount(string? category, CancellationToken ct = default)
    {
        return GetQueueItemsCount(category, null, null, null, null, ct);
    }

    public Task<int> GetQueueItemsCount
    (
        string? category,
        IReadOnlyCollection<Guid>? nzoIds,
        string? search,
        IReadOnlyCollection<QueueItem.PriorityOption>? priorities,
        IReadOnlyCollection<string>? statuses,
        CancellationToken ct = default,
        IReadOnlyCollection<Guid>? excludeIds = null
    )
    {
        var queueItems = GetQueueItemsQuery(category, nzoIds, search, priorities, statuses);
        if (excludeIds is { Count: > 0 })
            queueItems = queueItems.Where(q => !excludeIds.Contains(q.Id));
        return queueItems.CountAsync(cancellationToken: ct);
    }

    public async Task RemoveQueueItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await CancelWorkerJobsAsync(WorkerJob.JobKind.Download, ids, ct).ConfigureAwait(false);
    }

    public async Task UpdateQueueItemsPriorityAsync
    (
        List<Guid> ids,
        QueueItem.PriorityOption priority,
        CancellationToken ct = default
    )
    {
        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Priority, priority),
                cancellationToken: ct)
            .ConfigureAwait(false);
        await Ctx.WorkerJobs
            .Where(x => x.Kind == WorkerJob.JobKind.Download && ids.Contains(x.TargetId))
            .Where(x => x.Status != WorkerJob.JobStatus.Completed
                        && x.Status != WorkerJob.JobStatus.Cancelled
                        && x.Status != WorkerJob.JobStatus.Quarantined)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Priority, (int)priority),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public async Task UpdateQueueItemsPostProcessingAsync
    (
        List<Guid> ids,
        QueueItem.PostProcessingOption postProcessing,
        CancellationToken ct = default
    )
    {
        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.PostProcessing, postProcessing),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public Task<List<Guid>> GetAllQueueItemIdsAsync(CancellationToken ct = default)
    {
        return Ctx.QueueItems
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    private IQueryable<QueueItem> GetQueueItemsQuery
    (
        string? category,
        IReadOnlyCollection<Guid>? nzoIds,
        string? search,
        IReadOnlyCollection<QueueItem.PriorityOption>? priorities,
        IReadOnlyCollection<string>? statuses
    )
    {
        var queueItems = category != null
            ? Ctx.QueueItems.AsNoTracking().Where(q => q.Category == category)
            : Ctx.QueueItems.AsNoTracking();

        if (nzoIds is { Count: > 0 })
            queueItems = queueItems.Where(q => nzoIds.Contains(q.Id));
        if (!string.IsNullOrWhiteSpace(search))
            queueItems = queueItems.Where(q => q.JobName.Contains(search) || q.FileName.Contains(search));
        if (priorities is { Count: > 0 })
            queueItems = queueItems.Where(q => priorities.Contains(q.Priority));
        if (statuses is { Count: > 0 })
        {
            var includePaused = statuses.Contains("paused");
            var includeDownloading = statuses.Contains("downloading");
            var includeQueued = statuses.Contains("queued");
            queueItems = queueItems.Where(q =>
                includePaused && q.Priority == QueueItem.PriorityOption.Paused
                || includeDownloading && q.Priority != QueueItem.PriorityOption.Paused
                || includeQueued && q.Priority != QueueItem.PriorityOption.Paused);
        }

        return queueItems;
    }

    private static IOrderedQueryable<QueueItem> ApplyQueueSort
    (
        IQueryable<QueueItem> queueItems,
        QueueSortOptions? options
    )
    {
        options ??= QueueSortOptions.Default;

        IOrderedQueryable<QueueItem> ordered = options.Field switch
        {
            QueueSortField.Name => options.Descending
                ? queueItems.OrderByDescending(q => q.JobName)
                : queueItems.OrderBy(q => q.JobName),
            QueueSortField.Category => options.Descending
                ? queueItems.OrderByDescending(q => q.Category)
                : queueItems.OrderBy(q => q.Category),
            QueueSortField.Status => options.Descending
                ? queueItems.OrderByDescending(q => q.Priority == QueueItem.PriorityOption.Paused ? 0 : 1)
                : queueItems.OrderBy(q => q.Priority == QueueItem.PriorityOption.Paused ? 0 : 1),
            QueueSortField.Size => options.Descending
                ? queueItems.OrderByDescending(q => q.TotalSegmentBytes)
                : queueItems.OrderBy(q => q.TotalSegmentBytes),
            QueueSortField.CreatedAt => options.Descending
                ? queueItems.OrderByDescending(q => q.CreatedAt)
                : queueItems.OrderBy(q => q.CreatedAt),
            _ => options.Descending
                ? queueItems.OrderByDescending(q => q.Priority)
                : queueItems.OrderBy(q => q.Priority)
        };

        return ordered
            .ThenByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .ThenBy(q => q.Id);
    }

    public sealed record QueueSortOptions(QueueSortField Field, bool Descending)
    {
        public static readonly QueueSortOptions Default = new(QueueSortField.Priority, true);
    }

    public enum QueueSortField
    {
        Priority,
        Name,
        Category,
        Status,
        Size,
        CreatedAt
    }

    // history
    public async Task<HistoryItem?> GetHistoryItemAsync(string id)
    {
        return await Ctx.HistoryItems.FirstOrDefaultAsync(x => x.Id == Guid.Parse(id)).ConfigureAwait(false);
    }

    public async Task RemoveHistoryItemsAsync(List<Guid> ids, bool deleteFiles, CancellationToken ct = default)
    {
        if (deleteFiles)
        {
            var results = await (
                from h in Ctx.HistoryItems
                where ids.Contains(h.Id)
                join d in Ctx.Items on h.DownloadDirId equals d.Id into items
                from d in items.DefaultIfEmpty()
                select new { HistoryItem = h, DavItem = d }
            ).ToListAsync(ct).ConfigureAwait(false);

            var historyItems = results.Select(r => r.HistoryItem).ToList();
            var davItems = results.Where(r => r.DavItem != null).Select(r => r.DavItem!).ToList();
            Ctx.Items.RemoveRange(davItems);
            Ctx.HistoryItems.RemoveRange(historyItems);
            await AddMissingHistoryCleanupItemsAsync(historyItems.Select(x => x.Id).ToList(), deleteFiles, ct)
                .ConfigureAwait(false);
            return;
        }

        var existingHistoryItems = await Ctx.HistoryItems
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        Ctx.HistoryItems.RemoveRange(existingHistoryItems);
        await AddMissingHistoryCleanupItemsAsync(existingHistoryItems.Select(x => x.Id).ToList(), deleteFiles, ct)
            .ConfigureAwait(false);
    }

    private async Task AddMissingHistoryCleanupItemsAsync(List<Guid> ids, bool deleteFiles, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var existingCleanupIds = await Ctx.HistoryCleanupItems
            .Where(x => ids.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingCleanupIdSet = existingCleanupIds.ToHashSet();

        Ctx.HistoryCleanupItems.AddRange(ids
            .Where(x => !existingCleanupIdSet.Contains(x))
            .Select(x => new HistoryCleanupItem
        {
            Id = x,
            DeleteMountedFiles = deleteFiles
        }));
    }

    private class FileSizeResult
    {
        public long TotalSize { get; init; }
    }

    // health check
    public async Task<List<HealthCheckStat>> GetHealthCheckStatsAsync
    (
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default
    )
    {
        return await Ctx.HealthCheckStats
            .AsNoTracking()
            .Where(h => h.DateStartInclusive >= from && h.DateStartInclusive <= to)
            .GroupBy(h => new { h.Result, h.RepairStatus })
            .Select(g => new HealthCheckStat
            {
                Result = g.Key.Result,
                RepairStatus = g.Key.RepairStatus,
                Count = g.Select(r => r.Count).Sum(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // completed-symlinks
    public async Task<List<DavItem>> GetCompletedSymlinkCategoryChildren(string category,
        CancellationToken ct = default)
    {
        var query = from historyItem in Ctx.HistoryItems
                .AsNoTracking()
            where historyItem.Category == category
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                  && historyItem.DownloadDirId != null
            join davItem in Ctx.Items.AsNoTracking() on historyItem.DownloadDirId equals davItem.Id
            where davItem.Type == DavItem.ItemType.Directory
            select davItem;
        return await query.Distinct().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<RcloneInvalidationStats> GetRcloneInvalidationStatsAsync
    (
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var baseQuery = Ctx.RcloneInvalidationItems.AsNoTracking();
        var failedQuery = baseQuery.Where(x => x.Attempts > 0 || x.LastError != null);

        var pending = await baseQuery.CountAsync(ct).ConfigureAwait(false);
        var ready = await baseQuery.CountAsync(x => x.NextAttemptAt <= referenceTime, ct).ConfigureAwait(false);
        var failed = await failedQuery.CountAsync(ct).ConfigureAwait(false);
        var maxAttempts = await baseQuery
            .Select(x => (int?)x.Attempts)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;
        var lastError = await failedQuery
            .Where(x => x.LastError != null)
            .OrderByDescending(x => x.LastAttemptAt ?? x.CreatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => x.LastError)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new RcloneInvalidationStats(pending, ready, failed, maxAttempts, lastError);
    }

    public async Task<HealthWorkerQueueStats> GetHealthWorkerQueueStatsAsync
    (
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var healthCheckQueue = HealthCheckService.GetHealthCheckQueueItemsQuery(this)
            .AsNoTracking();

        var verifyReady = await healthCheckQueue
            .CountAsync(x => x.NextHealthCheck == null || x.NextHealthCheck < referenceTime, ct)
            .ConfigureAwait(false);
        var repairActionNeeded = await Ctx.HealthCheckResults
            .AsNoTracking()
            .CountAsync(x => x.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded, ct)
            .ConfigureAwait(false);

        return new HealthWorkerQueueStats(verifyReady, repairActionNeeded);
    }

    public sealed record RcloneInvalidationStats
    (
        int Pending,
        int Ready,
        int Failed,
        int MaxAttempts,
        string? LastError
    );

    public sealed record HealthWorkerQueueStats
    (
        int VerifyReady,
        int RepairActionNeeded
    );

    public async Task<WorkerJobQueueStats> GetWorkerJobQueueStatsAsync
    (
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var statusCounts = await Ctx.WorkerJobs
            .AsNoTracking()
            .GroupBy(x => new { x.Kind, x.Status })
            .Select(x => new WorkerJobStatusCount(x.Key.Kind, x.Key.Status, x.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var readyCounts = await Ctx.WorkerJobs
            .AsNoTracking()
            .Where(x => x.Status == WorkerJob.JobStatus.Pending
                        || x.Status == WorkerJob.JobStatus.Retry
                        || x.Status == WorkerJob.JobStatus.Leased && x.LeaseExpiresAt <= referenceTime)
            .Where(x => x.AvailableAt <= referenceTime)
            .Where(x => x.LeaseExpiresAt == null || x.LeaseExpiresAt <= referenceTime)
            .GroupBy(x => x.Kind)
            .Select(x => new WorkerJobReadyCount(x.Key, x.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new WorkerJobQueueStats(
            Download: WorkerJobKindStats.FromRows(WorkerJob.JobKind.Download, statusCounts, readyCounts),
            Verify: WorkerJobKindStats.FromRows(WorkerJob.JobKind.Verify, statusCounts, readyCounts),
            Repair: WorkerJobKindStats.FromRows(WorkerJob.JobKind.Repair, statusCounts, readyCounts));
    }

    public async Task CancelWorkerJobsAsync
    (
        WorkerJob.JobKind kind,
        IReadOnlyCollection<Guid> targetIds,
        CancellationToken ct = default
    )
    {
        if (targetIds.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        await Ctx.WorkerJobs
            .Where(x => x.Kind == kind && targetIds.Contains(x.TargetId))
            .Where(x => x.Status != WorkerJob.JobStatus.Completed
                        && x.Status != WorkerJob.JobStatus.Cancelled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, WorkerJob.JobStatus.Cancelled)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    public sealed record WorkerJobQueueStats
    (
        WorkerJobKindStats Download,
        WorkerJobKindStats Verify,
        WorkerJobKindStats Repair
    );

    public sealed record WorkerJobKindStats
    (
        int Pending,
        int Retry,
        int Leased,
        int Ready,
        int Quarantined,
        int Completed,
        int Cancelled,
        int Total
    )
    {
        public static WorkerJobKindStats FromRows
        (
            WorkerJob.JobKind kind,
            IReadOnlyCollection<WorkerJobStatusCount> statusCounts,
            IReadOnlyCollection<WorkerJobReadyCount> readyCounts
        )
        {
            var countsByStatus = statusCounts
                .Where(x => x.Kind == kind)
                .ToDictionary(x => x.Status, x => x.Count);
            var ready = readyCounts.FirstOrDefault(x => x.Kind == kind)?.Count ?? 0;
            var pending = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Pending);
            var retry = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Retry);
            var leased = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Leased);
            var quarantined = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Quarantined);
            var completed = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Completed);
            var cancelled = countsByStatus.GetValueOrDefault(WorkerJob.JobStatus.Cancelled);

            return new WorkerJobKindStats(
                Pending: pending,
                Retry: retry,
                Leased: leased,
                Ready: ready,
                Quarantined: quarantined,
                Completed: completed,
                Cancelled: cancelled,
                Total: pending + retry + leased + quarantined + completed + cancelled);
        }
    }

    public sealed record WorkerJobStatusCount
    (
        WorkerJob.JobKind Kind,
        WorkerJob.JobStatus Status,
        int Count
    );

    public sealed record WorkerJobReadyCount
    (
        WorkerJob.JobKind Kind,
        int Count
    );

    public async Task<WorkerJob> EnqueueWorkerJobAsync
    (
        WorkerJob.JobKind kind,
        Guid targetId,
        int priority,
        DateTimeOffset? now = null,
        string? payloadJson = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var existingJob = await Ctx.WorkerJobs
            .FirstOrDefaultAsync(x => x.Kind == kind && x.TargetId == targetId, ct)
            .ConfigureAwait(false);

        if (existingJob is null)
        {
            existingJob = new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                TargetId = targetId,
                Status = WorkerJob.JobStatus.Pending,
                Priority = priority,
                Attempts = 0,
                CreatedAt = referenceTime,
                UpdatedAt = referenceTime,
                AvailableAt = referenceTime,
                PayloadJson = payloadJson
            };
            Ctx.WorkerJobs.Add(existingJob);
        }
        else if (existingJob.Status is WorkerJob.JobStatus.Completed or WorkerJob.JobStatus.Cancelled)
        {
            existingJob.Status = WorkerJob.JobStatus.Pending;
            existingJob.Priority = priority;
            existingJob.Attempts = 0;
            existingJob.UpdatedAt = referenceTime;
            existingJob.AvailableAt = referenceTime;
            existingJob.CompletedAt = null;
            existingJob.LeaseOwner = null;
            existingJob.LeaseExpiresAt = null;
            existingJob.LastError = null;
            existingJob.PayloadJson = payloadJson;
        }
        else
        {
            existingJob.Priority = priority;
            existingJob.UpdatedAt = referenceTime;
            if (payloadJson is not null) existingJob.PayloadJson = payloadJson;
        }

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return existingJob;
    }

    public async Task<WorkerJob?> LeaseNextWorkerJobAsync
    (
        WorkerJob.JobKind kind,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var leaseExpiresAt = referenceTime + leaseDuration;

        await using var transaction = await Ctx.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var job = await Ctx.WorkerJobs
            .Where(x => x.Kind == kind)
            .Where(x => x.Status == WorkerJob.JobStatus.Pending
                        || x.Status == WorkerJob.JobStatus.Retry
                        || x.Status == WorkerJob.JobStatus.Leased && x.LeaseExpiresAt <= referenceTime)
            .Where(x => x.AvailableAt <= referenceTime)
            .Where(x => x.LeaseExpiresAt == null || x.LeaseExpiresAt <= referenceTime)
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.AvailableAt)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (job is null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        job.Status = WorkerJob.JobStatus.Leased;
        job.LeaseOwner = owner;
        job.LeaseExpiresAt = leaseExpiresAt;
        job.Attempts += 1;
        job.UpdatedAt = referenceTime;

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return job;
    }

    public async Task CompleteWorkerJobAsync
    (
        WorkerJob job,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        AttachWorkerJobIfDetached(job);
        job.Status = WorkerJob.JobStatus.Completed;
        job.UpdatedAt = referenceTime;
        job.CompletedAt = referenceTime;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task FailWorkerJobAsync
    (
        WorkerJob job,
        string error,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        AttachWorkerJobIfDetached(job);
        job.Status = job.Attempts >= maxAttempts
            ? WorkerJob.JobStatus.Quarantined
            : WorkerJob.JobStatus.Retry;
        job.UpdatedAt = referenceTime;
        job.AvailableAt = nextAttemptAt;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LastError = TruncateWorkerJobError(error);

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void AttachWorkerJobIfDetached(WorkerJob job)
    {
        if (Ctx.Entry(job).State == EntityState.Detached)
            Ctx.WorkerJobs.Attach(job);
    }

    private static string TruncateWorkerJobError(string error)
    {
        return error.Length <= MaxWorkerJobErrorLength
            ? error
            : error[..MaxWorkerJobErrorLength];
    }
}

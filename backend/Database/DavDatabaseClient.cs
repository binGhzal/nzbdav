using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
    private const int MaxWorkerJobErrorLength = 1024;
    private const int MaxRepairRunMessageLength = 1024;

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

    public async Task<RepairRun> StartRepairRunAsync
    (
        int priority = 10,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        await using var transaction = await Ctx.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        var activeRun = await Ctx.RepairRuns
            .Where(x => x.Status == RepairRun.RepairRunStatus.Running)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (activeRun != null)
        {
            await RefreshRepairRunSummaryAsync(activeRun, referenceTime, ct).ConfigureAwait(false);
            if (activeRun.Status == RepairRun.RepairRunStatus.Running)
                throw new BadHttpRequestException($"Repair run {activeRun.Id} is already active.");
        }

        var items = await HealthCheckService.GetHealthCheckQueueItemsQuery(this)
            .AsNoTracking()
            .OrderBy(x => x.Path)
            .Select(x => new { x.Id, x.Path })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var run = new RepairRun
        {
            Id = Guid.NewGuid(),
            Status = items.Count == 0 ? RepairRun.RepairRunStatus.Completed : RepairRun.RepairRunStatus.Running,
            Stage = items.Count == 0 ? "completed" : "queued",
            StartedAt = referenceTime,
            UpdatedAt = referenceTime,
            CompletedAt = items.Count == 0 ? referenceTime : null,
            Total = items.Count,
            Message = items.Count == 0 ? "No eligible files found for repair verification." : null
        };
        Ctx.RepairRuns.Add(run);

        Ctx.RepairEntryHealth.AddRange(items.Select(item => new RepairEntryHealth
        {
            Id = Guid.NewGuid(),
            RepairRunId = run.Id,
            DavItemId = item.Id,
            Path = item.Path,
            State = RepairEntryHealth.RepairEntryState.Pending,
            CreatedAt = referenceTime,
            UpdatedAt = referenceTime
        }));

        if (items.Count > 0)
            await UpsertRepairRunVerifyJobsAsync(run.Id, items.Select(x => x.Id).ToArray(), priority, referenceTime, ct)
                .ConfigureAwait(false);

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return run;
    }

    public async Task CancelRepairRunAsync
    (
        Guid repairRunId,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var run = await Ctx.RepairRuns.FirstOrDefaultAsync(x => x.Id == repairRunId, ct).ConfigureAwait(false)
                  ?? throw new BadHttpRequestException($"Repair run {repairRunId} was not found.");
        if (run.Status is RepairRun.RepairRunStatus.Completed or RepairRun.RepairRunStatus.Cancelled)
            return;

        run.Status = RepairRun.RepairRunStatus.Cancelled;
        run.Stage = "cancelled";
        run.UpdatedAt = referenceTime;
        run.CancelledAt = referenceTime;
        run.Message = "Repair run cancelled by operator.";

        await Ctx.RepairEntryHealth
            .Where(x => x.RepairRunId == repairRunId)
            .Where(x => x.State == RepairEntryHealth.RepairEntryState.Pending
                        || x.State == RepairEntryHealth.RepairEntryState.Checking)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.State, RepairEntryHealth.RepairEntryState.Cancelled)
                    .SetProperty(x => x.UpdatedAt, referenceTime)
                    .SetProperty(x => x.Message, "Repair run cancelled by operator."),
                cancellationToken: ct)
            .ConfigureAwait(false);

        var payloadJson = CreateRepairRunPayloadJson(repairRunId);
        await Ctx.WorkerJobs
            .Where(x => x.PayloadJson == payloadJson)
            .Where(x => x.Status != WorkerJob.JobStatus.Completed
                        && x.Status != WorkerJob.JobStatus.Cancelled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, WorkerJob.JobStatus.Cancelled)
                    .SetProperty(x => x.UpdatedAt, referenceTime)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null),
                cancellationToken: ct)
            .ConfigureAwait(false);

        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearRepairRunsAsync(CancellationToken ct = default)
    {
        var hasActiveRuns = await Ctx.RepairRuns
            .AnyAsync(x => x.Status == RepairRun.RepairRunStatus.Running, ct)
            .ConfigureAwait(false);
        if (hasActiveRuns)
            throw new BadHttpRequestException("Cancel the active repair run before clearing repair history.");

        var payloads = await Ctx.RepairRuns
            .Select(x => CreateRepairRunPayloadJson(x.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (payloads.Count > 0)
        {
            var hasActivePayloadJobs = await Ctx.WorkerJobs
                .Where(x => x.PayloadJson != null && payloads.Contains(x.PayloadJson))
                .AnyAsync(
                    x => x.Status == WorkerJob.JobStatus.Pending
                         || x.Status == WorkerJob.JobStatus.Retry
                         || x.Status == WorkerJob.JobStatus.Leased,
                    ct)
                .ConfigureAwait(false);
            if (hasActivePayloadJobs)
                throw new BadHttpRequestException("Repair workers are still active. Cancel the repair run before clearing history.");

            await Ctx.WorkerJobs
                .Where(x => x.PayloadJson != null && payloads.Contains(x.PayloadJson))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        await Ctx.RepairBrokenFiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await Ctx.RepairEntryHealth.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await Ctx.RepairRuns.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<RepairRun?> GetActiveRepairRunAsync(CancellationToken ct = default)
    {
        var run = await Ctx.RepairRuns
            .Where(x => x.Status == RepairRun.RepairRunStatus.Running)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (run == null) return null;
        await RefreshRepairRunSummaryAsync(run, ct: ct).ConfigureAwait(false);
        return run.Status == RepairRun.RepairRunStatus.Running ? run : null;
    }

    public async Task<List<RepairRun>> GetRepairRunsAsync
    (
        int limit,
        CancellationToken ct = default
    )
    {
        var runs = await Ctx.RepairRuns
            .OrderByDescending(x => x.StartedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var run in runs.Where(x => x.Status == RepairRun.RepairRunStatus.Running))
            await RefreshRepairRunSummaryAsync(run, ct: ct).ConfigureAwait(false);

        return runs;
    }

    public async Task<RepairRun> RefreshRepairRunSummaryAsync
    (
        RepairRun run,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        await ReconcileRepairRunEntriesAsync(run.Id, referenceTime, ct).ConfigureAwait(false);
        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        var statusCounts = await Ctx.RepairEntryHealth
            .AsNoTracking()
            .Where(x => x.RepairRunId == run.Id)
            .GroupBy(x => x.State)
            .Select(x => new RepairEntryStateCount(x.Key, x.Count()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var counts = statusCounts.ToDictionary(x => x.State, x => x.Count);
        var payloadJson = CreateRepairRunPayloadJson(run.Id);
        var pendingJobs = await Ctx.WorkerJobs
            .AsNoTracking()
            .Where(x => x.PayloadJson == payloadJson)
            .Where(x => x.Status == WorkerJob.JobStatus.Pending
                        || x.Status == WorkerJob.JobStatus.Retry
                        || x.Status == WorkerJob.JobStatus.Leased)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        run.Total = counts.Values.Sum();
        run.Checked = counts
            .Where(x => x.Key is not RepairEntryHealth.RepairEntryState.Pending
                and not RepairEntryHealth.RepairEntryState.Checking
                and not RepairEntryHealth.RepairEntryState.Cancelled)
            .Sum(x => x.Value);
        run.Missing = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Missing);
        run.ProviderErrors = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.ProviderError);
        run.Unknown = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Unknown);
        run.Repaired = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Repaired);
        run.Deleted = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Deleted);
        run.ActionNeeded = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.ActionNeeded);
        run.BrokenFiles = await Ctx.RepairBrokenFiles
            .AsNoTracking()
            .Where(x => x.RepairRunId == run.Id && !x.Cleared)
            .CountAsync(ct)
            .ConfigureAwait(false);
        run.NextDueAt = pendingJobs
            .Where(x => x.Status is WorkerJob.JobStatus.Pending or WorkerJob.JobStatus.Retry)
            .Select(x => (DateTimeOffset?)x.AvailableAt)
            .OrderBy(x => x)
            .FirstOrDefault();

        if (run.Status == RepairRun.RepairRunStatus.Running)
        {
            var checking = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Checking);
            var pending = counts.GetValueOrDefault(RepairEntryHealth.RepairEntryState.Pending);
            var hasPendingJobs = pendingJobs.Count > 0;
            run.Stage = checking > 0
                ? "checking"
                : run.Missing > 0 && hasPendingJobs
                    ? "repairing"
                    : pending > 0 || hasPendingJobs
                        ? "queued"
                        : "completed";
            if (run.Stage == "completed")
            {
                run.Status = RepairRun.RepairRunStatus.Completed;
                run.CompletedAt = referenceTime;
            }
        }

        run.UpdatedAt = referenceTime;
        await Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return run;
    }

    public async Task UpsertRepairEntryAsync
    (
        Guid repairRunId,
        Guid davItemId,
        string path,
        RepairEntryHealth.RepairEntryState state,
        string? message,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var entry = await Ctx.RepairEntryHealth
            .FirstOrDefaultAsync(x => x.RepairRunId == repairRunId && x.DavItemId == davItemId, ct)
            .ConfigureAwait(false);
        if (entry == null)
        {
            entry = new RepairEntryHealth
            {
                Id = Guid.NewGuid(),
                RepairRunId = repairRunId,
                DavItemId = davItemId,
                Path = path,
                CreatedAt = referenceTime
            };
            Ctx.RepairEntryHealth.Add(entry);
        }

        entry.Path = path;
        entry.State = state;
        entry.Message = TruncateRepairMessage(message);
        entry.UpdatedAt = referenceTime;

        if (state is RepairEntryHealth.RepairEntryState.Healthy
            or RepairEntryHealth.RepairEntryState.Repaired
            or RepairEntryHealth.RepairEntryState.Deleted)
            await ClearRepairBrokenFilesAsync(davItemId, referenceTime, ct).ConfigureAwait(false);
    }

    public async Task MarkRepairVerificationFailureAsync
    (
        Guid repairRunId,
        Guid davItemId,
        string error,
        bool quarantined,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var path = await Ctx.Items
            .AsNoTracking()
            .Where(x => x.Id == davItemId)
            .Select(x => x.Path)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? "";
        var message = quarantined
            ? $"Verification failed and job was quarantined: {error}"
            : $"Verification failed. Will retry: {error}";
        await UpsertRepairEntryAsync(
                repairRunId,
                davItemId,
                path,
                RepairEntryHealth.RepairEntryState.ProviderError,
                message,
                now,
                ct)
            .ConfigureAwait(false);
    }

    public async Task UpsertRepairBrokenFileAsync
    (
        Guid repairRunId,
        Guid davItemId,
        string path,
        string reason,
        DateTimeOffset? now = null,
        CancellationToken ct = default
    )
    {
        var referenceTime = now ?? DateTimeOffset.UtcNow;
        var existing = await Ctx.RepairBrokenFiles
            .FirstOrDefaultAsync(x => x.RepairRunId == repairRunId && x.DavItemId == davItemId && !x.Cleared, ct)
            .ConfigureAwait(false);
        if (existing == null)
        {
            Ctx.RepairBrokenFiles.Add(new RepairBrokenFile
            {
                Id = Guid.NewGuid(),
                RepairRunId = repairRunId,
                DavItemId = davItemId,
                Path = path,
                Reason = TruncateRepairMessage(reason) ?? "",
                CreatedAt = referenceTime
            });
            return;
        }

        existing.Path = path;
        existing.Reason = TruncateRepairMessage(reason) ?? "";
    }

    private async Task ClearRepairBrokenFilesAsync
    (
        Guid davItemId,
        DateTimeOffset referenceTime,
        CancellationToken ct
    )
    {
        await Ctx.RepairBrokenFiles
            .Where(x => x.DavItemId == davItemId && !x.Cleared)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Cleared, true),
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task ReconcileRepairRunEntriesAsync
    (
        Guid repairRunId,
        DateTimeOffset referenceTime,
        CancellationToken ct
    )
    {
        var entries = await Ctx.RepairEntryHealth
            .Where(x => x.RepairRunId == repairRunId)
            .Where(x => x.State == RepairEntryHealth.RepairEntryState.Pending
                        || x.State == RepairEntryHealth.RepairEntryState.Checking)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (entries.Count == 0) return;

        var davItemIds = entries.Select(x => x.DavItemId).ToArray();
        var payloadJson = CreateRepairRunPayloadJson(repairRunId);
        var verifyJobs = await Ctx.WorkerJobs
            .AsNoTracking()
            .Where(x => x.Kind == WorkerJob.JobKind.Verify)
            .Where(x => x.PayloadJson == payloadJson)
            .Where(x => davItemIds.Contains(x.TargetId))
            .ToDictionaryAsync(x => x.TargetId, ct)
            .ConfigureAwait(false);
        var existingItemIds = await Ctx.Items
            .AsNoTracking()
            .Where(x => davItemIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingItems = existingItemIds.ToHashSet();

        foreach (var entry in entries)
        {
            if (!verifyJobs.TryGetValue(entry.DavItemId, out var job))
            {
                if (existingItems.Contains(entry.DavItemId))
                    SetRepairEntryState(
                        entry,
                        RepairEntryHealth.RepairEntryState.Unknown,
                        "Repair verification job is missing.",
                        referenceTime);
                else
                    await SetRepairEntryStateAsync(
                            entry,
                            RepairEntryHealth.RepairEntryState.Deleted,
                            "File no longer exists.",
                            referenceTime,
                            ct)
                        .ConfigureAwait(false);
                continue;
            }

            switch (job.Status)
            {
                case WorkerJob.JobStatus.Completed:
                    await ReconcileCompletedRepairEntryAsync(entry, existingItems.Contains(entry.DavItemId), referenceTime, ct)
                        .ConfigureAwait(false);
                    break;
                case WorkerJob.JobStatus.Cancelled:
                    SetRepairEntryState(
                        entry,
                        RepairEntryHealth.RepairEntryState.Cancelled,
                        "Repair verification job was cancelled.",
                        referenceTime);
                    break;
                case WorkerJob.JobStatus.Quarantined:
                    SetRepairEntryState(
                        entry,
                        RepairEntryHealth.RepairEntryState.ProviderError,
                        $"Repair verification job was quarantined: {job.LastError ?? "unknown error"}",
                        referenceTime);
                    break;
            }
        }
    }

    private async Task ReconcileCompletedRepairEntryAsync
    (
        RepairEntryHealth entry,
        bool itemExists,
        DateTimeOffset referenceTime,
        CancellationToken ct
    )
    {
        if (!itemExists)
        {
            await SetRepairEntryStateAsync(
                    entry,
                    RepairEntryHealth.RepairEntryState.Deleted,
                    "File no longer exists.",
                    referenceTime,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var latest = await Ctx.HealthCheckResults
            .AsNoTracking()
            .Where(x => x.DavItemId == entry.DavItemId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (latest == null)
        {
            SetRepairEntryState(
                entry,
                RepairEntryHealth.RepairEntryState.Unknown,
                "Repair verification completed without a health result.",
                referenceTime);
            return;
        }

        var state = GetRepairEntryStateFromHealthResult(latest);
        await SetRepairEntryStateAsync(entry, state, latest.Message, referenceTime, ct).ConfigureAwait(false);
    }

    private static RepairEntryHealth.RepairEntryState GetRepairEntryStateFromHealthResult(HealthCheckResult result)
    {
        if (result.Result == HealthCheckResult.HealthResult.Healthy)
            return RepairEntryHealth.RepairEntryState.Healthy;

        return result.RepairStatus switch
        {
            HealthCheckResult.RepairAction.Repaired => RepairEntryHealth.RepairEntryState.Repaired,
            HealthCheckResult.RepairAction.Deleted => RepairEntryHealth.RepairEntryState.Deleted,
            HealthCheckResult.RepairAction.ActionNeeded => RepairEntryHealth.RepairEntryState.Missing,
            _ when result.Message?.Contains("provider_errors", StringComparison.OrdinalIgnoreCase) == true
                => RepairEntryHealth.RepairEntryState.ProviderError,
            _ when result.Message?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true
                => RepairEntryHealth.RepairEntryState.Unknown,
            _ => RepairEntryHealth.RepairEntryState.Unknown
        };
    }

    private async Task SetRepairEntryStateAsync
    (
        RepairEntryHealth entry,
        RepairEntryHealth.RepairEntryState state,
        string? message,
        DateTimeOffset referenceTime,
        CancellationToken ct
    )
    {
        SetRepairEntryState(entry, state, message, referenceTime);
        if (state is RepairEntryHealth.RepairEntryState.Healthy
            or RepairEntryHealth.RepairEntryState.Repaired
            or RepairEntryHealth.RepairEntryState.Deleted)
            await ClearRepairBrokenFilesAsync(entry.DavItemId, referenceTime, ct).ConfigureAwait(false);
    }

    private static void SetRepairEntryState
    (
        RepairEntryHealth entry,
        RepairEntryHealth.RepairEntryState state,
        string? message,
        DateTimeOffset referenceTime
    )
    {
        entry.State = state;
        entry.Message = TruncateRepairMessage(message);
        entry.UpdatedAt = referenceTime;
    }

    public static string CreateRepairRunPayloadJson(Guid repairRunId)
    {
        return JsonSerializer.Serialize(new RepairRunPayload(repairRunId));
    }

    public static Guid? TryGetRepairRunId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<RepairRunPayload>(payloadJson)?.RepairRunId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task UpsertRepairRunVerifyJobsAsync
    (
        Guid repairRunId,
        IReadOnlyCollection<Guid> targetIds,
        int priority,
        DateTimeOffset referenceTime,
        CancellationToken ct
    )
    {
        var payloadJson = CreateRepairRunPayloadJson(repairRunId);
        var existingJobs = await Ctx.WorkerJobs
            .Where(x => x.Kind == WorkerJob.JobKind.Verify && targetIds.Contains(x.TargetId))
            .ToDictionaryAsync(x => x.TargetId, ct)
            .ConfigureAwait(false);

        foreach (var targetId in targetIds)
        {
            if (!existingJobs.TryGetValue(targetId, out var job))
            {
                Ctx.WorkerJobs.Add(new WorkerJob
                {
                    Id = Guid.NewGuid(),
                    Kind = WorkerJob.JobKind.Verify,
                    TargetId = targetId,
                    Status = WorkerJob.JobStatus.Pending,
                    Priority = priority,
                    Attempts = 0,
                    CreatedAt = referenceTime,
                    UpdatedAt = referenceTime,
                    AvailableAt = referenceTime,
                    PayloadJson = payloadJson
                });
                continue;
            }

            var isExpiredLease = job.Status == WorkerJob.JobStatus.Leased
                                 && job.LeaseExpiresAt <= referenceTime;
            if (job.Status is WorkerJob.JobStatus.Completed
                    or WorkerJob.JobStatus.Cancelled
                    or WorkerJob.JobStatus.Quarantined
                    or WorkerJob.JobStatus.Retry
                    or WorkerJob.JobStatus.Pending
                || isExpiredLease)
            {
                job.Status = WorkerJob.JobStatus.Pending;
                job.Attempts = 0;
                job.AvailableAt = referenceTime;
                job.CompletedAt = null;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                job.LastError = null;
            }

            job.Priority = priority;
            job.UpdatedAt = referenceTime;
            job.PayloadJson = payloadJson;
        }
    }

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

    private static string? TruncateRepairMessage(string? message)
    {
        if (message == null) return null;
        return message.Length <= MaxRepairRunMessageLength
            ? message
            : message[..MaxRepairRunMessageLength];
    }

    private sealed record RepairRunPayload(Guid RepairRunId);

    private sealed record RepairEntryStateCount(RepairEntryHealth.RepairEntryState State, int Count);
}

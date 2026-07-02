using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
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
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return ctx.Items.Where(x => x.ParentId == dirId).ToListAsync(ct);
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

        // attempt to read nzb contents from blob-store.
        var queueNzbStream = queueItem != null
            ? BlobStore.ReadBlob(queueItem.Id)
            : null;

        // otherwise, read nzb contents from database.
        if (queueItem != null && queueNzbStream == null)
        {
            var queueNzbContents = await Ctx.QueueNzbContents
                .FirstOrDefaultAsync(q => q.Id == queueItem.Id, ct)
                .ConfigureAwait(false);

            queueNzbStream = queueNzbContents != null
                ? new MemoryStream(Encoding.UTF8.GetBytes(queueNzbContents.NzbContents))
                : null;
        }

        // return
        return (queueItem, queueNzbStream);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        return GetQueueItems(category, null, null, null, null, null, start, limit, ct);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        IReadOnlyCollection<Guid>? excludeIds = null,
        IReadOnlyCollection<Guid>? nzoIds = null,
        string? search = null,
        IReadOnlyCollection<QueueItem.PriorityOption>? priorities = null,
        IReadOnlyCollection<string>? statuses = null,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        var queueItems = GetQueueItemsQuery(category, nzoIds, search, priorities, statuses);
        if (excludeIds is { Count: > 0 })
            queueItems = queueItems.Where(q => !excludeIds.Contains(q.Id));

        return queueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
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
            ? Ctx.QueueItems.Where(q => q.Category == category)
            : Ctx.QueueItems;

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
            where historyItem.Category == category
                  && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                  && historyItem.DownloadDirId != null
            join davItem in Ctx.Items on historyItem.DownloadDirId equals davItem.Id
            where davItem.Type == DavItem.ItemType.Directory
            select davItem;
        return await query.Distinct().ToListAsync(ct).ConfigureAwait(false);
    }
}

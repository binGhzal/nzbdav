using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public static class HistoryVisibilityPolicy
{
    public static Task<bool> HasActiveRepairJobsAsync(
        DavDatabaseContext dbContext,
        CancellationToken ct = default) =>
        dbContext.WorkerJobs.AsNoTracking().AnyAsync(workerJob =>
            workerJob.Kind == WorkerJob.JobKind.Repair
            && (workerJob.Status == WorkerJob.JobStatus.Pending
                || workerJob.Status == WorkerJob.JobStatus.Leased
                || workerJob.Status == WorkerJob.JobStatus.Retry), ct);

    public static IQueryable<HistoryItem> VisibleToSab(
        IQueryable<HistoryItem> historyItems,
        DavDatabaseContext dbContext,
        bool hasActiveRepairJobs)
    {
        var visible = historyItems
            .Where(historyItem => !dbContext.ArrImportCommands
                .Any(command => command.HistoryItemId == historyItem.Id && command.VisibleAt == null));
        if (!hasActiveRepairJobs) return visible;

        return visible.Where(historyItem => !dbContext.Items.Any(davItem =>
                davItem.HistoryItemId == historyItem.Id
                && dbContext.WorkerJobs.Any(workerJob =>
                    workerJob.Kind == WorkerJob.JobKind.Repair
                    && (workerJob.Status == WorkerJob.JobStatus.Pending
                        || workerJob.Status == WorkerJob.JobStatus.Leased
                        || workerJob.Status == WorkerJob.JobStatus.Retry)
                    && workerJob.TargetId == davItem.Id)));
    }
}

public class HistoryVisibilityNotifier(
    ConfigManager configManager,
    WebsocketManager websocketManager)
{
    public virtual async Task<bool> PublishIfVisibleAsync(
        Guid historyItemId,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            return await PublishIfVisibleAsync(dbContext, historyItemId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Debug("History visibility publication for {HistoryItemId} was cancelled.", historyItemId);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Could not publish visible history item {HistoryItemId}: {Message}",
                historyItemId,
                exception.Message);
            return false;
        }
    }

    public virtual async Task<bool> PublishIfVisibleAsync(
        DavDatabaseContext dbContext,
        Guid historyItemId,
        CancellationToken ct = default)
    {
        try
        {
            var hasActiveRepairJobs = await HistoryVisibilityPolicy
                .HasActiveRepairJobsAsync(dbContext, ct)
                .ConfigureAwait(false);
            var historyItem = await HistoryVisibilityPolicy
                .VisibleToSab(dbContext.HistoryItems.AsNoTracking(), dbContext, hasActiveRepairJobs)
                .SingleOrDefaultAsync(item => item.Id == historyItemId, ct)
                .ConfigureAwait(false);
            if (historyItem is null) return false;
            var downloadFolder = historyItem.DownloadDirId is { } downloadDirId
                ? await dbContext.Items.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == downloadDirId, ct)
                    .ConfigureAwait(false)
                : null;
            var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(
                historyItem,
                downloadFolder,
                configManager);
            await websocketManager
                .SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson())
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Debug("History visibility publication for {HistoryItemId} was cancelled.", historyItemId);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Could not publish visible history item {HistoryItemId}: {Message}",
                historyItemId,
                exception.Message);
            return false;
        }
    }

    public virtual async Task<bool> PublishForDavItemIfVisibleAsync(
        Guid davItemId,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var historyItemId = await dbContext.Items.AsNoTracking()
                .Where(item => item.Id == davItemId)
                .Select(item => item.HistoryItemId)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var historyItemIds = await dbContext.HistoryItems.AsNoTracking()
                .Where(historyItem => historyItem.DownloadDirId == davItemId)
                .Select(historyItem => historyItem.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (historyItemId is { } itemHistoryId) historyItemIds.Add(itemHistoryId);

            var published = false;
            foreach (var id in historyItemIds.Distinct())
            {
                try
                {
                    if (await PublishIfVisibleAsync(dbContext, id, ct).ConfigureAwait(false))
                        published = true;
                }
                catch (Exception exception)
                {
                    Log.Warning(
                        exception,
                        "Could not publish history visibility for history item {HistoryItemId}: {Message}",
                        id,
                        exception.Message);
                }
            }
            return published;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Debug("History visibility lookup for DAV item {DavItemId} was cancelled.", davItemId);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Could not publish history visibility for DAV item {DavItemId}: {Message}",
                davItemId,
                exception.Message);
            return false;
        }
    }
}

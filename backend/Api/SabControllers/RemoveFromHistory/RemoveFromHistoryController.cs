using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    private const int DuplicateVerificationBatchSize = 500;

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

        var callerTransaction = dbClient.Ctx.Database.CurrentTransaction;
        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException(
                "Removing history inside a caller-owned transaction requires savepoint support.");
        }

        var trackerSnapshot = CaptureChangeTracker(dbClient.Ctx);
        await using var ownedTransaction = callerTransaction == null
            ? await dbClient.Ctx.Database.BeginTransactionAsync(request.CancellationToken).ConfigureAwait(false)
            : null;
        var activeTransaction = ownedTransaction ?? callerTransaction!;
        var savepointName = callerTransaction != null
            ? $"remove_history_{Guid.NewGuid():N}"
            : null;
        if (savepointName != null)
        {
            await activeTransaction.CreateSavepointAsync(savepointName, request.CancellationToken)
                .ConfigureAwait(false);
        }
        IReadOnlyList<Guid> removedIds = [];
        try
        {
            removedIds = await dbClient.RemoveHistoryItemsAsync(
                    nzoIds,
                    request.DeleteCompletedFiles,
                    request.CancellationToken)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            if (ownedTransaction != null)
                await ownedTransaction.CommitAsync(request.CancellationToken).ConfigureAwait(false);
            else if (savepointName != null)
                await activeTransaction.ReleaseSavepointAsync(savepointName, request.CancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var canVerifyDuplicate = ownedTransaction != null
                                     && exception is DbUpdateConcurrencyException concurrencyException
                                     && HasOnlyRequestedDeletionEntries(
                                         concurrencyException,
                                         nzoIds,
                                         request.DeleteCompletedFiles);
            try
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await activeTransaction.RollbackToSavepointAsync(savepointName!, CancellationToken.None)
                        .ConfigureAwait(false);
                    await activeTransaction.ReleaseSavepointAsync(savepointName!, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                RestoreChangeTracker(dbClient.Ctx, trackerSnapshot);
            }

            if (canVerifyDuplicate
                && await IsCompletedRemovalAsync(
                        dbClient.Ctx,
                        nzoIds,
                        request.DeleteCompletedFiles)
                    .ConfigureAwait(false))
            {
                return new RemoveFromHistoryResponse() { Status = true };
            }

            throw;
        }
        if (removedIds.Count > 0)
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", removedIds));
        return new RemoveFromHistoryResponse() { Status = true };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(RequestContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }

    private static List<TrackedEntrySnapshot> CaptureChangeTracker(DavDatabaseContext dbContext)
    {
        return dbContext.ChangeTracker.Entries()
            .Select(entry => new TrackedEntrySnapshot(
                entry.Entity,
                entry.State,
                entry.CurrentValues.Clone(),
                entry.OriginalValues.Clone(),
                entry.Properties
                    .Select(property => new TrackedPropertySnapshot(
                        property.Metadata.Name,
                        property.IsModified,
                        property.IsTemporary))
                    .ToList()))
            .ToList();
    }

    private static bool HasOnlyRequestedDeletionEntries(
        DbUpdateConcurrencyException exception,
        IReadOnlyCollection<Guid> historyItemIds,
        bool deleteCompletedFiles)
    {
        if (exception.Entries.Count == 0) return false;

        var requestedIds = historyItemIds.ToHashSet();
        return exception.Entries.All(entry =>
            entry.State == EntityState.Deleted
            && entry.Entity switch
            {
                HistoryItem historyItem => requestedIds.Contains(historyItem.Id),
                DavItem davItem => deleteCompletedFiles
                                   && davItem.HistoryItemId is { } historyItemId
                                   && requestedIds.Contains(historyItemId),
                _ => false
            });
    }

    private static async Task<bool> IsCompletedRemovalAsync(
        DavDatabaseContext dbContext,
        IReadOnlyCollection<Guid> historyItemIds,
        bool deleteCompletedFiles)
    {
        foreach (var idBatch in historyItemIds.Distinct().Chunk(DuplicateVerificationBatchSize))
        {
            if (await dbContext.HistoryItems
                    .AsNoTracking()
                    .AnyAsync(x => idBatch.Contains(x.Id), CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return false;
            }

            if (await dbContext.ImportReceipts
                    .AsNoTracking()
                    .AnyAsync(
                        x => idBatch.Contains(x.HistoryItemId)
                             && x.State != ImportReceiptState.Removed
                             && x.State != ImportReceiptState.VerificationQuarantined,
                        CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return false;
            }

            var adequatelyQueuedIds = await dbContext.HistoryCleanupItems
                .AsNoTracking()
                .Where(x => idBatch.Contains(x.Id)
                            && (!deleteCompletedFiles || x.DeleteMountedFiles))
                .Select(x => x.Id)
                .ToListAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var completedCleanupIds = idBatch.Except(adequatelyQueuedIds).ToArray();
            if (completedCleanupIds.Length > 0
                && await dbContext.Items
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.HistoryItemId != null
                             && completedCleanupIds.Contains(x.HistoryItemId.Value),
                        CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static void RestoreChangeTracker(
        DavDatabaseContext dbContext,
        IReadOnlyCollection<TrackedEntrySnapshot> snapshots)
    {
        var originalEntities = snapshots.Select(x => x.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var entry in dbContext.ChangeTracker.Entries()
                     .Where(x => !originalEntities.Contains(x.Entity))
                     .ToList())
            entry.State = EntityState.Detached;

        foreach (var snapshot in snapshots)
        {
            var entry = dbContext.Entry(snapshot.Entity);
            entry.CurrentValues.SetValues(snapshot.CurrentValues);
            entry.OriginalValues.SetValues(snapshot.OriginalValues);
            entry.State = snapshot.State;
            foreach (var propertySnapshot in snapshot.Properties)
            {
                var property = entry.Property(propertySnapshot.Name);
                if (snapshot.State == EntityState.Modified)
                    property.IsModified = propertySnapshot.IsModified;
                property.IsTemporary = propertySnapshot.IsTemporary;
            }
        }
    }

    private sealed record TrackedEntrySnapshot(
        object Entity,
        EntityState State,
        PropertyValues CurrentValues,
        PropertyValues OriginalValues,
        IReadOnlyList<TrackedPropertySnapshot> Properties);

    private sealed record TrackedPropertySnapshot(
        string Name,
        bool IsModified,
        bool IsTemporary);
}

using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

public sealed record ImportReceiptResult(Guid Id, ImportReceiptState State, bool Changed);

public sealed record ImportClaimRequest(Guid DavItemId, Guid HistoryItemId, DateTimeOffset Now);

public sealed class ImportReceiptService(DavDatabaseContext dbContext)
{
    public async Task StageAvailableReceiptsAsync(
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var trackedItems = dbContext.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is not EntityState.Deleted and not EntityState.Detached)
            .Select(x => x.Entity)
            .Where(x => x.HistoryItemId == historyItemId && x.Type == DavItem.ItemType.UsenetFile)
            .ToList();
        var persistedItems = await dbContext.Items
            .Where(x => x.HistoryItemId == historyItemId && x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var deletedIds = dbContext.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Deleted)
            .Select(x => x.Entity.Id)
            .ToHashSet();
        var davItemIds = trackedItems.Select(x => x.Id)
            .Concat(persistedItems)
            .Where(x => !deletedIds.Contains(x))
            .Distinct()
            .ToList();
        if (davItemIds.Count == 0) return;

        var existingIds = await dbContext.ImportReceipts
            .Where(x => x.HistoryItemId == historyItemId && davItemIds.Contains(x.DavItemId))
            .Select(x => x.DavItemId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingIdSet = existingIds
            .Concat(dbContext.ChangeTracker.Entries<ImportReceipt>()
                .Where(x => x.State is not EntityState.Deleted and not EntityState.Detached)
                .Where(x => x.Entity.HistoryItemId == historyItemId)
                .Select(x => x.Entity.DavItemId))
            .ToHashSet();

        dbContext.ImportReceipts.AddRange(davItemIds
            .Where(x => !existingIdSet.Contains(x))
            .Select(davItemId => new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyItemId,
                State = ImportReceiptState.Available,
                CreatedAt = now,
                UpdatedAt = now
            }));
    }

    public async Task<ImportReceiptResult> ClaimAsync(ImportClaimRequest request, CancellationToken ct)
    {
        var receipt = await dbContext.ImportReceipts
            .SingleOrDefaultAsync(x => x.DavItemId == request.DavItemId && x.HistoryItemId == request.HistoryItemId, ct)
            .ConfigureAwait(false);
        if (receipt == null)
        {
            receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = request.DavItemId,
                HistoryItemId = request.HistoryItemId,
                State = ImportReceiptState.UnlinkClaimed,
                CreatedAt = request.Now,
                UpdatedAt = request.Now
            };
            dbContext.ImportReceipts.Add(receipt);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result(receipt, true);
        }

        if (receipt.State != ImportReceiptState.Available)
            return Result(receipt, false);

        receipt.State = ImportReceiptState.UnlinkClaimed;
        receipt.UpdatedAt = request.Now;
        receipt.Detail = null;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result(receipt, true);
    }

    public Task<IReadOnlyList<ImportReceiptResult>> MarkImportedAsync(
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct) => TransitionHistoryAsync(historyItemId, ImportReceiptState.Imported, now, null, ct);

    public async Task<ImportReceiptResult> MarkImportedAsync(
        Guid davItemId,
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var receipt = await dbContext.ImportReceipts
            .SingleAsync(x => x.DavItemId == davItemId && x.HistoryItemId == historyItemId, ct)
            .ConfigureAwait(false);
        if (receipt.State is ImportReceiptState.Removed or ImportReceiptState.Imported)
            return Result(receipt, false);

        receipt.State = ImportReceiptState.Imported;
        receipt.UpdatedAt = now;
        receipt.ImportedAt = now;
        receipt.Detail = null;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result(receipt, true);
    }

    public Task<IReadOnlyList<ImportReceiptResult>> MarkRemovedAsync(
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct) => TransitionHistoryAsync(historyItemId, ImportReceiptState.Removed, now, null, ct);

    public async Task<ImportReceiptResult> MarkNeedsReviewAsync(
        Guid davItemId,
        Guid historyItemId,
        DateTimeOffset now,
        string? detail,
        CancellationToken ct)
    {
        var receipt = await dbContext.ImportReceipts
            .SingleAsync(x => x.DavItemId == davItemId && x.HistoryItemId == historyItemId, ct)
            .ConfigureAwait(false);
        if (receipt.State != ImportReceiptState.UnlinkClaimed)
            return Result(receipt, false);

        receipt.State = ImportReceiptState.NeedsReview;
        receipt.UpdatedAt = now;
        receipt.Detail = detail;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result(receipt, true);
    }

    private async Task<IReadOnlyList<ImportReceiptResult>> TransitionHistoryAsync(
        Guid historyItemId,
        ImportReceiptState target,
        DateTimeOffset now,
        string? detail,
        CancellationToken ct)
    {
        var receipts = await dbContext.ImportReceipts
            .Where(x => x.HistoryItemId == historyItemId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var changed = false;
        var results = new List<ImportReceiptResult>(receipts.Count);
        foreach (var receipt in receipts)
        {
            var canTransition = receipt.State != ImportReceiptState.Removed
                                && receipt.State != target
                                && (target == ImportReceiptState.Removed
                                    || receipt.State is ImportReceiptState.Available
                                        or ImportReceiptState.UnlinkClaimed
                                        or ImportReceiptState.NeedsReview);
            if (canTransition)
            {
                receipt.State = target;
                receipt.UpdatedAt = now;
                receipt.Detail = detail;
                if (target == ImportReceiptState.Imported) receipt.ImportedAt = now;
                if (target == ImportReceiptState.Removed) receipt.RemovedAt = now;
                changed = true;
            }
            results.Add(Result(receipt, canTransition));
        }

        if (changed)
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return results;
    }

    private static ImportReceiptResult Result(ImportReceipt receipt, bool changed) =>
        new(receipt.Id, receipt.State, changed);
}

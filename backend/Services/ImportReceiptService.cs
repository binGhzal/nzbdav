using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

public sealed record ImportReceiptResult(Guid Id, ImportReceiptState State, bool Changed);

public sealed record ImportClaimRequest(Guid DavItemId, Guid HistoryItemId, DateTimeOffset Now);

public sealed class ImportReceiptService(DavDatabaseContext dbContext)
{
    private const string ReceiptUniqueIndex = "IX_ImportReceipts_DavItemId_HistoryItemId";

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
        DetachTrackedReceipts(request.DavItemId, request.HistoryItemId);
        var changed = await ClaimAvailableAsync(request, ct).ConfigureAwait(false);
        if (changed == 1)
            return Result(await ReloadAsync(request.DavItemId, request.HistoryItemId, ct).ConfigureAwait(false), true);

        var durable = await FindAsync(request.DavItemId, request.HistoryItemId, ct).ConfigureAwait(false);
        if (durable != null)
            return Result(durable, false);

        var receipt = new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = request.DavItemId,
            HistoryItemId = request.HistoryItemId,
            State = ImportReceiptState.UnlinkClaimed,
            CreatedAt = request.Now,
            UpdatedAt = request.Now
        };
        dbContext.ImportReceipts.Add(receipt);
        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result(receipt, true);
        }
        catch (DbUpdateException exception) when (IsReceiptUniqueViolation(exception))
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            changed = await ClaimAvailableAsync(request, ct).ConfigureAwait(false);
            durable = await ReloadAsync(request.DavItemId, request.HistoryItemId, ct).ConfigureAwait(false);
            return Result(durable, changed == 1);
        }
    }

    public async Task<IReadOnlyList<ImportReceiptResult>> MarkImportedAsync(
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return await TransitionHistoryAsync(
                historyItemId,
                [ImportReceiptState.Available, ImportReceiptState.UnlinkClaimed, ImportReceiptState.NeedsReview],
                ImportReceiptState.Imported,
                now,
                null,
                ct)
            .ConfigureAwait(false);
    }

    public Task<ImportReceiptResult> MarkImportedAsync(
        Guid davItemId,
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return TransitionOneAsync(
            davItemId,
            historyItemId,
            [ImportReceiptState.Available, ImportReceiptState.UnlinkClaimed, ImportReceiptState.NeedsReview],
            ImportReceiptState.Imported,
            now,
            null,
            ct);
    }

    public async Task<IReadOnlyList<ImportReceiptResult>> MarkRemovedAsync(
        Guid historyItemId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var activeStates = Enum.GetValues<ImportReceiptState>()
            .Where(x => x is not ImportReceiptState.Removed
                and not ImportReceiptState.VerificationQuarantined)
            .ToArray();
        return await TransitionHistoryAsync(
                historyItemId,
                activeStates,
                ImportReceiptState.Removed,
                now,
                null,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<int> MarkRemovedBatchAsync(
        IReadOnlyCollection<Guid> historyItemIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (historyItemIds.Count == 0) return 0;

        var ids = historyItemIds.Distinct().ToArray();
        var idSet = ids.ToHashSet();
        var trackedReceipts = dbContext.ChangeTracker.Entries<ImportReceipt>()
            .Where(entry => idSet.Contains(entry.Entity.HistoryItemId)
                            || (entry.State != EntityState.Added
                                && idSet.Contains(entry.OriginalValues.GetValue<Guid>(
                                    nameof(ImportReceipt.HistoryItemId)))))
            .ToList();
        ValidateTrackedReceipts(trackedReceipts);

        var changed = await dbContext.ImportReceipts
            .Where(x => ids.Contains(x.HistoryItemId)
                        && x.State != ImportReceiptState.Removed
                        && x.State != ImportReceiptState.VerificationQuarantined)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ImportReceiptState.Removed)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.RemovedAt, now)
                .SetProperty(x => x.Detail, (string?)null), ct)
            .ConfigureAwait(false);

        if (trackedReceipts.Count == 0) return changed;

        var durableReceipts = await dbContext.ImportReceipts
            .AsNoTracking()
            .Where(x => ids.Contains(x.HistoryItemId))
            .ToDictionaryAsync(x => x.Id, ct)
            .ConfigureAwait(false);
        foreach (var entry in trackedReceipts)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.State is ImportReceiptState.Removed
                    or ImportReceiptState.VerificationQuarantined)
                    continue;
                entry.Entity.State = ImportReceiptState.Removed;
                entry.Entity.UpdatedAt = now;
                entry.Entity.RemovedAt = now;
                entry.Entity.Detail = null;
                continue;
            }

            if (!durableReceipts.TryGetValue(entry.Entity.Id, out var durable))
            {
                throw new DbUpdateConcurrencyException(
                    $"Tracked import receipt '{entry.Entity.Id}' disappeared during history removal.");
            }

            ReconcileTerminalFields(entry, durable);
        }

        return changed;
    }

    public Task<ImportReceiptResult> MarkNeedsReviewAsync(
        Guid davItemId,
        Guid historyItemId,
        DateTimeOffset now,
        string? detail,
        CancellationToken ct)
    {
        return TransitionOneAsync(
            davItemId,
            historyItemId,
            [ImportReceiptState.UnlinkClaimed],
            ImportReceiptState.NeedsReview,
            now,
            detail,
            ct);
    }

    public async Task<IReadOnlyList<ImportReceiptResult>> MarkVerificationQuarantineAsync(
        Guid historyItemId,
        DateTimeOffset now,
        string detail,
        CancellationToken ct)
    {
        DetachTrackedReceipts(historyItemId);
        await dbContext.ImportReceipts
            .Where(x => x.HistoryItemId == historyItemId && x.State != ImportReceiptState.Removed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ImportReceiptState.VerificationQuarantined)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Detail, detail), ct)
            .ConfigureAwait(false);

        var durableReceipts = await dbContext.ImportReceipts
            .AsNoTracking()
            .Where(x => x.HistoryItemId == historyItemId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.State })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return durableReceipts
            .Select(x => new ImportReceiptResult(
                x.Id,
                x.State,
                x.State == ImportReceiptState.VerificationQuarantined))
            .ToList();
    }

    private Task<int> ClaimAvailableAsync(ImportClaimRequest request, CancellationToken ct)
    {
        return dbContext.ImportReceipts
            .Where(x => x.DavItemId == request.DavItemId
                        && x.HistoryItemId == request.HistoryItemId
                        && x.State == ImportReceiptState.Available)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ImportReceiptState.UnlinkClaimed)
                .SetProperty(x => x.UpdatedAt, request.Now)
                .SetProperty(x => x.Detail, (string?)null), ct);
    }

    private async Task<IReadOnlyList<ImportReceiptResult>> TransitionHistoryAsync(
        Guid historyItemId,
        IReadOnlyCollection<ImportReceiptState> allowedStates,
        ImportReceiptState target,
        DateTimeOffset now,
        string? detail,
        CancellationToken ct)
    {
        DetachTrackedReceipts(historyItemId);
        var receiptKeys = await dbContext.ImportReceipts
            .AsNoTracking()
            .Where(x => x.HistoryItemId == historyItemId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.DavItemId, x.HistoryItemId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var results = new List<ImportReceiptResult>(receiptKeys.Count);
        foreach (var key in receiptKeys)
        {
            results.Add(await TransitionOneAsync(
                    key.DavItemId,
                    key.HistoryItemId,
                    allowedStates,
                    target,
                    now,
                    detail,
                    ct)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<ImportReceiptResult> TransitionOneAsync(
        Guid davItemId,
        Guid historyItemId,
        IReadOnlyCollection<ImportReceiptState> allowedStates,
        ImportReceiptState target,
        DateTimeOffset now,
        string? detail,
        CancellationToken ct)
    {
        DetachTrackedReceipts(davItemId, historyItemId);
        var changed = await dbContext.ImportReceipts
            .Where(x => x.DavItemId == davItemId
                        && x.HistoryItemId == historyItemId
                        && allowedStates.Contains(x.State))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, target)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(
                    x => x.ImportedAt,
                    x => target == ImportReceiptState.Imported ? now : x.ImportedAt)
                .SetProperty(
                    x => x.RemovedAt,
                    x => target == ImportReceiptState.Removed ? now : x.RemovedAt), ct)
            .ConfigureAwait(false);
        var durable = await ReloadAsync(davItemId, historyItemId, ct).ConfigureAwait(false);
        return Result(durable, changed == 1);
    }

    private Task<ImportReceipt?> FindAsync(Guid davItemId, Guid historyItemId, CancellationToken ct)
    {
        return dbContext.ImportReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.DavItemId == davItemId && x.HistoryItemId == historyItemId, ct);
    }

    private async Task<ImportReceipt> ReloadAsync(Guid davItemId, Guid historyItemId, CancellationToken ct)
    {
        return await FindAsync(davItemId, historyItemId, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The import receipt disappeared during a state transition.");
    }

    private void DetachTrackedReceipts(Guid historyItemId)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<ImportReceipt>()
                     .Where(x => x.Entity.HistoryItemId == historyItemId)
                     .ToList())
            entry.State = EntityState.Detached;
    }

    private static void ValidateTrackedReceipts(IReadOnlyCollection<EntityEntry<ImportReceipt>> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Cannot remove history while a matching import receipt is tracked as Deleted.");
            }

            if (entry.State == EntityState.Added) continue;

            var originalHistoryItemId = entry.OriginalValues.GetValue<Guid>(
                nameof(ImportReceipt.HistoryItemId));
            var originalDavItemId = entry.OriginalValues.GetValue<Guid>(nameof(ImportReceipt.DavItemId));
            if (entry.Entity.HistoryItemId != originalHistoryItemId
                || entry.Entity.DavItemId != originalDavItemId)
            {
                throw new InvalidOperationException(
                    "Cannot remove history while a matching import receipt identity has pending changes.");
            }
        }
    }

    private static void ReconcileTerminalFields(
        EntityEntry<ImportReceipt> entry,
        ImportReceipt durable)
    {
        AcceptDurableValue(entry.Property(x => x.State), durable.State);
        AcceptDurableValue(entry.Property(x => x.UpdatedAt), durable.UpdatedAt);
        AcceptDurableValue(entry.Property(x => x.RemovedAt), durable.RemovedAt);
        AcceptDurableValue(entry.Property(x => x.Detail), durable.Detail);
    }

    private static void AcceptDurableValue<T>(PropertyEntry<ImportReceipt, T> property, T value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }

    private void DetachTrackedReceipts(Guid davItemId, Guid historyItemId)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<ImportReceipt>()
                     .Where(x => x.Entity.DavItemId == davItemId && x.Entity.HistoryItemId == historyItemId)
                     .ToList())
            entry.State = EntityState.Detached;
    }

    private static bool IsReceiptUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException switch
        {
            SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 } sqlite =>
                sqlite.Message.Contains("ImportReceipts.DavItemId, ImportReceipts.HistoryItemId", StringComparison.Ordinal),
            PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: ReceiptUniqueIndex
            } => true,
            _ => false
        };
    }

    private static ImportReceiptResult Result(ImportReceipt receipt, bool changed) =>
        new(receipt.Id, receipt.State, changed);
}

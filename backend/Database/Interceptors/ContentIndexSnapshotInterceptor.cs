using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database.Interceptors;

public sealed class ContentIndexSnapshotInterceptor : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, PendingSnapshotMarker> PendingSnapshots = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        MarkPendingSnapshot(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync
    (
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        MarkPendingSnapshot(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        RequestSnapshot(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync
    (
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        RequestSnapshot(eventData.Context);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ClearPendingSnapshot(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync
    (
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        ClearPendingSnapshot(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static void RequestSnapshot(DbContext? context)
    {
        if (context is not DavDatabaseContext dbContext) return;
        if (!PendingSnapshots.TryGetValue(dbContext, out _)) return;
        if (dbContext.Database.CurrentTransaction is not null) return;
        PendingSnapshots.Remove(dbContext);
        ContentIndexSnapshotWriterService.RequestSnapshot();
    }

    internal static void PublishCommittedSnapshot(DbContext? context)
    {
        if (context is not DavDatabaseContext dbContext) return;
        if (!PendingSnapshots.TryGetValue(dbContext, out _)) return;
        PendingSnapshots.Remove(dbContext);
        ContentIndexSnapshotWriterService.RequestSnapshot();
    }

    internal static void DiscardPendingSnapshot(DbContext? context)
    {
        if (context is null) return;
        PendingSnapshots.Remove(context);
    }

    private static void MarkPendingSnapshot(DbContext? dbContext)
    {
        if (dbContext == null || !HasContentIndexChanges(dbContext)) return;
        PendingSnapshots.GetValue(dbContext, _ => new PendingSnapshotMarker());
    }

    private static void ClearPendingSnapshot(DbContext? dbContext)
    {
        if (dbContext == null) return;
        if (dbContext.Database.CurrentTransaction is not null) return;
        PendingSnapshots.Remove(dbContext);
    }

    private static bool HasContentIndexChanges(DbContext dbContext)
    {
        return dbContext.ChangeTracker.Entries().Any(entry =>
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                return false;

            return entry.Entity switch
            {
                DavItem item => ContentPathUtil.IsContentChildPath(item.Path),
                DavNzbFile => true,
                DavRarFile => true,
                DavMultipartFile => true,
                _ => false
            };
        });
    }

    private sealed class PendingSnapshotMarker
    {
    }
}

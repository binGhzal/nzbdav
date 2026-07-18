using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

public sealed class DatabaseTransactionTelemetryInterceptor(DatabaseTelemetry telemetry) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        telemetry.RecordTransaction(eventData.Duration);
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.PublishCommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.PublishCommittedSnapshot(dbContext);
        }
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        telemetry.RecordTransaction(eventData.Duration);
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.PublishCommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.PublishCommittedSnapshot(dbContext);
        }
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        telemetry.RecordTransaction(eventData.Duration);
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.DiscardUncommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.DiscardPendingSnapshot(dbContext);
        }
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        telemetry.RecordTransaction(eventData.Duration);
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.DiscardUncommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.DiscardPendingSnapshot(dbContext);
        }
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.DiscardUncommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.DiscardPendingSnapshot(dbContext);
        }
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DavDatabaseContext dbContext)
        {
            dbContext.DiscardUncommittedDatabaseWakeSignals();
            ContentIndexSnapshotInterceptor.DiscardPendingSnapshot(dbContext);
        }
        return Task.CompletedTask;
    }
}

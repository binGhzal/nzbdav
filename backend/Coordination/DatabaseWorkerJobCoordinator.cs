using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Coordination;

public sealed class DatabaseWorkerJobCoordinator : IWorkerJobCoordinator
{
    private const int MaxJsonUtf8Bytes = 16 * 1024;
    private const int MaxErrorLength = 1024;
    private readonly Func<DavDatabaseContext> _contextFactory;
    private readonly DavDatabaseContext? _sharedContext;
    private readonly IWorkerLaneCapacityPolicy _capacityPolicy;
    private readonly WorkerLeaseOptions _options;

    public DatabaseWorkerJobCoordinator(
        IWorkerLaneCapacityPolicy capacityPolicy,
        IOptions<WorkerLeaseOptions> options)
        : this(() => new DavDatabaseContext(), null, capacityPolicy, options.Value)
    {
    }

    public DatabaseWorkerJobCoordinator(
        DavDatabaseContext dbContext,
        IWorkerLaneCapacityPolicy capacityPolicy,
        IOptions<WorkerLeaseOptions> options)
        : this(() => dbContext, dbContext, capacityPolicy, options.Value)
    {
    }

    private DatabaseWorkerJobCoordinator(
        Func<DavDatabaseContext> contextFactory,
        DavDatabaseContext? sharedContext,
        IWorkerLaneCapacityPolicy capacityPolicy,
        WorkerLeaseOptions options)
    {
        _contextFactory = contextFactory;
        _sharedContext = sharedContext;
        _capacityPolicy = capacityPolicy;
        _options = WorkerLeaseOptions.Validate(options);
    }

    public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
        WorkerJob.JobKind kind,
        string owner,
        int capacity,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("A lease owner is required.", nameof(owner));

        return WithLeaseAcquisitionRetryAsync(
            db => LeaseCoreAsync(db, kind, owner, Math.Clamp(capacity, 1, 128), now, ct),
            ct);
    }

    public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
        WithSqliteRetryAsync(async db =>
        {
            var changed = await ActiveLease(db, lease)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LastHeartbeatAt, now)
                    .SetProperty(job => job.LeaseExpiresAt, now + _options.Duration)
                    .SetProperty(job => job.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            return changed == 1;
        }, ct);

    public Task<bool> ReportProgressAsync(
        WorkerLeaseIdentity lease,
        string progressJson,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var boundedProgress = BoundUtf8(progressJson, MaxJsonUtf8Bytes);
        return WithSqliteRetryAsync(async db =>
        {
            var changed = await ActiveLease(db, lease)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.ProgressJson, boundedProgress)
                    .SetProperty(job => job.ProgressUpdatedAt, now)
                    .SetProperty(job => job.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            return changed == 1;
        }, ct);
    }

    public Task<bool> CompleteAsync(
        WorkerLeaseIdentity lease,
        string? resultJson,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var boundedResult = BoundUtf8(resultJson, MaxJsonUtf8Bytes);
        return WithSqliteRetryAsync(async db =>
        {
            var changed = await ActiveLease(db, lease)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, WorkerJob.JobStatus.Completed)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.ResultJson, boundedResult), ct)
                .ConfigureAwait(false);
            if (changed == 1) return true;

            return await db.WorkerJobs.AsNoTracking().AnyAsync(job =>
                job.Id == lease.JobId
                && job.Status == WorkerJob.JobStatus.Completed
                && job.LeaseOwner == lease.Owner
                && job.LeaseToken == lease.Token
                && job.LeaseGeneration == lease.Generation
                && job.ResultJson == boundedResult, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
        WithSqliteRetryAsync(async db =>
        {
            var changed = await ActiveLease(db, lease)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, WorkerJob.JobStatus.Pending)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.AvailableAt, now)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.LastHeartbeatAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.Attempts, job => job.Attempts > 0 ? job.Attempts - 1 : 0), ct)
                .ConfigureAwait(false);
            return changed == 1;
        }, ct);

    public Task<bool> FailAsync(
        WorkerLeaseIdentity lease,
        WorkerJob.FailureClass failureKind,
        string error,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var boundedError = BoundLength(error, MaxErrorLength);
        return WithSqliteRetryAsync(async db =>
        {
            if (failureKind == WorkerJob.FailureClass.Cancelled)
            {
                var cancelled = await AuthenticatedLease(db, lease)
                    .Where(job => job.CancelRequestedAt != null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, WorkerJob.JobStatus.Cancelled)
                        .SetProperty(job => job.UpdatedAt, now)
                        .SetProperty(job => job.CompletedAt, now)
                        .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(job => job.FailureKind, failureKind)
                        .SetProperty(job => job.LastError, boundedError), ct)
                    .ConfigureAwait(false);
                if (cancelled == 1) return true;

                return await db.WorkerJobs.AsNoTracking().AnyAsync(job =>
                    job.Id == lease.JobId
                    && job.Status == WorkerJob.JobStatus.Cancelled
                    && job.LeaseOwner == lease.Owner
                    && job.LeaseToken == lease.Token
                    && job.LeaseGeneration == lease.Generation
                    && job.FailureKind == WorkerJob.FailureClass.Cancelled, ct).ConfigureAwait(false);
            }

            var changed = await ActiveLease(db, lease)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status,
                        job => job.Attempts >= maxAttempts
                            ? WorkerJob.JobStatus.Quarantined
                            : WorkerJob.JobStatus.Retry)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.AvailableAt, nextAttemptAt)
                    .SetProperty(job => job.LeaseOwner, (string?)null)
                    .SetProperty(job => job.LeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.LastHeartbeatAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.FailureKind, failureKind)
                    .SetProperty(job => job.LastError, boundedError), ct)
                .ConfigureAwait(false);
            return changed == 1;
        }, ct);
    }

    public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
        WithSqliteRetryAsync(async db =>
        {
            var changed = await db.WorkerJobs
                .Where(job => job.Id == jobId
                              && job.Status == WorkerJob.JobStatus.Leased
                              && job.CancelRequestedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.CancelRequestedAt, now)
                    .SetProperty(job => job.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            if (changed == 1) return true;

            return await db.WorkerJobs.AsNoTracking().AnyAsync(job =>
                job.Id == jobId
                && job.Status == WorkerJob.JobStatus.Leased
                && job.CancelRequestedAt != null, ct).ConfigureAwait(false);
        }, ct);

    private async Task<IReadOnlyList<WorkerLease>> LeaseCoreAsync(
        DavDatabaseContext db,
        WorkerJob.JobKind kind,
        string owner,
        int requestedCapacity,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        await db.WorkerJobs
            .Where(job => job.Status == WorkerJob.JobStatus.Leased
                          && job.LeaseExpiresAt <= now
                          && job.CancelRequestedAt != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, WorkerJob.JobStatus.Cancelled)
                .SetProperty(job => job.UpdatedAt, now)
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(job => job.FailureKind, WorkerJob.FailureClass.Cancelled)
                .SetProperty(job => job.LastError, "Cancellation request expired before acknowledgement."), ct)
            .ConfigureAwait(false);

        var configuredMaximum = Math.Max(0, _capacityPolicy.GetMaximum(kind));
        var activeCount = await db.WorkerJobs.AsNoTracking()
            .CountAsync(job => job.Kind == kind
                               && job.Status == WorkerJob.JobStatus.Leased
                               && job.LeaseExpiresAt > now, ct)
            .ConfigureAwait(false);
        var availableSlots = Math.Min(requestedCapacity, Math.Max(0, configuredMaximum - activeCount));
        if (availableSlots == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return [];
        }

        var candidateIds = await db.WorkerJobs.AsNoTracking()
            .Where(job => job.Kind == kind
                          && job.CancelRequestedAt == null
                          && job.AvailableAt <= now
                          && (job.Status == WorkerJob.JobStatus.Pending
                              || job.Status == WorkerJob.JobStatus.Retry
                              || job.Status == WorkerJob.JobStatus.Leased && job.LeaseExpiresAt <= now))
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.AvailableAt)
            .ThenBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .Take(availableSlots)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var leasedIds = new List<Guid>(candidateIds.Count);
        foreach (var candidateId in candidateIds)
        {
            var token = Guid.NewGuid();
            var changed = await db.WorkerJobs
                .Where(job => job.Id == candidateId
                              && job.Kind == kind
                              && job.CancelRequestedAt == null
                              && job.AvailableAt <= now
                              && (job.Status == WorkerJob.JobStatus.Pending
                                  || job.Status == WorkerJob.JobStatus.Retry
                                  || job.Status == WorkerJob.JobStatus.Leased && job.LeaseExpiresAt <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, WorkerJob.JobStatus.Leased)
                    .SetProperty(job => job.LeaseOwner, owner)
                    .SetProperty(job => job.LeaseToken, token)
                    .SetProperty(job => job.LeaseGeneration, job => job.LeaseGeneration + 1)
                    .SetProperty(job => job.LeaseExpiresAt, now + _options.Duration)
                    .SetProperty(job => job.LastHeartbeatAt, now)
                    .SetProperty(job => job.StartedAt, now)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.Attempts, job => job.Attempts + 1)
                    .SetProperty(job => job.CancelRequestedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.FailureKind, (WorkerJob.FailureClass?)null)
                    .SetProperty(job => job.LastError, (string?)null)
                    .SetProperty(job => job.ProgressJson, (string?)null)
                    .SetProperty(job => job.ProgressUpdatedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.ResultJson, (string?)null)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null), ct)
                .ConfigureAwait(false);
            if (changed == 1) leasedIds.Add(candidateId);
        }

        var leases = await db.WorkerJobs.AsNoTracking()
            .Where(job => leasedIds.Contains(job.Id))
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.AvailableAt)
            .ThenBy(job => job.CreatedAt)
            .Select(job => new WorkerLease(
                new WorkerLeaseIdentity(
                    job.Id,
                    job.LeaseOwner!,
                    job.LeaseToken!.Value,
                    job.LeaseGeneration),
                job.Kind,
                job.TargetId,
                job.Priority,
                job.Attempts,
                job.PayloadJson,
                job.LeaseExpiresAt!.Value,
                job.CancelRequestedAt != null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return leases;
    }

    private static IQueryable<WorkerJob> AuthenticatedLease(
        DavDatabaseContext db,
        WorkerLeaseIdentity lease) => db.WorkerJobs.Where(job =>
        job.Id == lease.JobId
        && job.Status == WorkerJob.JobStatus.Leased
        && job.LeaseOwner == lease.Owner
        && job.LeaseToken == lease.Token
        && job.LeaseGeneration == lease.Generation);

    private static IQueryable<WorkerJob> ActiveLease(
        DavDatabaseContext db,
        WorkerLeaseIdentity lease) =>
        AuthenticatedLease(db, lease).Where(job => job.CancelRequestedAt == null);

    private async Task<T> WithSqliteRetryAsync<T>(
        Func<DavDatabaseContext, Task<T>> action,
        CancellationToken ct)
    {
        return await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            if (_sharedContext is not null)
                return await action(_sharedContext).ConfigureAwait(false);

            await using var db = _contextFactory();
            return await action(db).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> WithLeaseAcquisitionRetryAsync<T>(
        Func<DavDatabaseContext, Task<T>> action,
        CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await WithSqliteRetryAsync(action, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < maxAttempts && IsRetryablePostgreSqlTransactionFailure(exception))
            {
                _sharedContext?.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryablePostgreSqlTransactionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgresException)
                return postgresException.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected;
        }

        return false;
    }

    private static string? BoundUtf8(string? value, int maxBytes)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        var length = Math.Min(value.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maxBytes)
            length--;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    private static string BoundLength(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;

        var length = maxLength;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}

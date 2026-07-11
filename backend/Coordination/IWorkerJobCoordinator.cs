using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Coordination;

public interface IWorkerJobCoordinator
{
    Task<IReadOnlyList<WorkerLease>> LeaseAsync(
        WorkerJob.JobKind kind,
        string owner,
        int capacity,
        DateTimeOffset now,
        CancellationToken ct);

    Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct);

    Task<bool> ReportProgressAsync(
        WorkerLeaseIdentity lease,
        string progressJson,
        DateTimeOffset now,
        CancellationToken ct);

    Task<bool> CompleteAsync(
        WorkerLeaseIdentity lease,
        string? resultJson,
        DateTimeOffset now,
        CancellationToken ct);

    Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct);

    Task<bool> FailAsync(
        WorkerLeaseIdentity lease,
        WorkerJob.FailureClass failureKind,
        string error,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        DateTimeOffset now,
        CancellationToken ct);

    Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct);
}

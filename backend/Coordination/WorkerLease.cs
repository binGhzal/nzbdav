using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Coordination;

public readonly record struct WorkerLeaseIdentity(
    Guid JobId,
    string Owner,
    Guid Token,
    long Generation);

public sealed record WorkerLease(
    WorkerLeaseIdentity Identity,
    WorkerJob.JobKind Kind,
    Guid TargetId,
    int Priority,
    int Attempt,
    string? PayloadJson,
    DateTimeOffset ExpiresAt,
    bool CancellationRequested);

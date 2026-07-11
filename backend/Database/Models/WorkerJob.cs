namespace NzbWebDAV.Database.Models;

public class WorkerJob
{
    public Guid Id { get; set; }
    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; }
    public Guid TargetId { get; set; }
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public FailureClass? FailureKind { get; set; }
    public string? ProgressJson { get; set; }
    public DateTimeOffset? ProgressUpdatedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? LastError { get; set; }
    public string? PayloadJson { get; set; }

    public enum JobKind
    {
        Download = 1,
        Verify = 2,
        Repair = 3,
    }

    public enum JobStatus
    {
        Pending = 0,
        Leased = 1,
        Retry = 2,
        Completed = 3,
        Quarantined = 4,
        Cancelled = 5,
    }

    public enum FailureClass
    {
        Retryable = 1,
        Provider = 2,
        InvalidData = 3,
        Cancelled = 4,
        Permanent = 5,
    }
}

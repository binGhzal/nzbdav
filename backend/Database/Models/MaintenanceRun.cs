namespace NzbWebDAV.Database.Models;

public sealed class MaintenanceRun
{
    public Guid Id { get; set; }
    public MaintenanceRunKind Kind { get; set; }
    public MaintenanceRunStatus Status { get; set; }
    public int? ActiveSlot { get; set; }
    public string RequestedBy { get; set; } = "manual";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public int ProgressCurrent { get; set; }
    public int? ProgressTotal { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public enum MaintenanceRunKind
{
    RemoveUnlinkedFiles = 0,
    RemoveUnlinkedFilesDryRun = 1,
}

public enum MaintenanceRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    CancellationRequested = 4,
    Cancelled = 5,
    Interrupted = 6,
}

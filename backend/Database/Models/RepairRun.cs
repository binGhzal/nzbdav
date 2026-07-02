namespace NzbWebDAV.Database.Models;

public class RepairRun
{
    public Guid Id { get; set; }
    public RepairRunStatus Status { get; set; }
    public string Stage { get; set; } = "pending";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }
    public int Total { get; set; }
    public int Checked { get; set; }
    public int Missing { get; set; }
    public int ProviderErrors { get; set; }
    public int Unknown { get; set; }
    public int Repaired { get; set; }
    public int Deleted { get; set; }
    public int ActionNeeded { get; set; }
    public int BrokenFiles { get; set; }
    public string? Message { get; set; }

    public enum RepairRunStatus
    {
        Running = 0,
        Completed = 1,
        Cancelled = 2,
        Failed = 3,
    }
}

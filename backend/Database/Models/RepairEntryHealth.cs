namespace NzbWebDAV.Database.Models;

public class RepairEntryHealth
{
    public Guid Id { get; set; }
    public Guid RepairRunId { get; set; }
    public Guid DavItemId { get; set; }
    public string Path { get; set; } = "";
    public RepairEntryState State { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public enum RepairEntryState
    {
        Pending = 0,
        Checking = 1,
        Healthy = 2,
        Missing = 3,
        ProviderError = 4,
        Unknown = 5,
        Repaired = 6,
        Deleted = 7,
        ActionNeeded = 8,
        Cancelled = 9,
    }
}

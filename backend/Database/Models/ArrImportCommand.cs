namespace NzbWebDAV.Database.Models;

public sealed class ArrImportCommand
{
    public Guid Id { get; set; }
    public Guid HistoryItemId { get; set; }
    public string Category { get; set; } = "";
    public string RequiredInvalidationPathsJson { get; set; } = "[]";
    public ArrImportCommandStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? VisibleAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string ResultsJson { get; set; } = "[]";
    public string? LastError { get; set; }
}

public enum ArrImportCommandStatus
{
    Pending = 0,
    WaitingForInvalidation = 1,
    Executing = 2,
    Retry = 3,
    Dispatched = 4,
    NoRoute = 5,
    Quarantined = 6,
}

namespace NzbWebDAV.Database.Models;

public class QueuePriorityHint
{
    public Guid QueueItemId { get; set; }
    public int Score { get; set; }
    public QueueItem.PriorityOption EffectivePriority { get; set; }
    public bool ApplyToScheduling { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public string Source { get; set; } = "arr";
    public DateTimeOffset ComputedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? StaleReason { get; set; }
}

namespace NzbWebDAV.Database.Models;

public class ArrDownloadLifecycleEvent
{
    public Guid Id { get; set; }
    public Guid? QueueItemId { get; set; }
    public Guid? HistoryItemId { get; set; }
    public string ArrApp { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string? DownloadId { get; set; }
    public string? MediaKey { get; set; }
    public string State { get; set; } = "";
    public string? StateReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

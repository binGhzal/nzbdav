namespace NzbWebDAV.Database.Models;

public sealed class ImportReceipt
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public Guid HistoryItemId { get; set; }
    public ImportReceiptState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? Detail { get; set; }
}

public enum ImportReceiptState
{
    Available = 0,
    UnlinkClaimed = 1,
    Imported = 2,
    Removed = 3,
    NeedsReview = 4,
}

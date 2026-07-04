namespace NzbWebDAV.Database.Models;

public class ArrDownloadCorrelation
{
    public Guid Id { get; set; }
    public Guid? QueueItemId { get; set; }
    public Guid? HistoryItemId { get; set; }
    public string ArrApp { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string InstanceHost { get; set; } = "";
    public string? DownloadId { get; set; }
    public int? QueueRecordId { get; set; }
    public string? MediaKey { get; set; }
    public int? MovieId { get; set; }
    public int? SeriesId { get; set; }
    public int? EpisodeId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }
    public string? EpisodeIdsJson { get; set; }
    public string? ReleaseTitle { get; set; }
    public string? Category { get; set; }
    public string? Indexer { get; set; }
    public string? DownloadClient { get; set; }
    public string? Quality { get; set; }
    public string? CustomFormatsJson { get; set; }
    public string? Status { get; set; }
    public string? TrackedDownloadStatus { get; set; }
    public string? TrackedDownloadState { get; set; }
    public string Source { get; set; } = "auto";
    public bool ManualLock { get; set; }
    public bool IsUpgrade { get; set; }
    public bool IsDuplicate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

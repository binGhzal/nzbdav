using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public class ArrQueueRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("downloadClient")]
    public string? DownloadClient { get; set; }

    [JsonPropertyName("downloadId")]
    public string? DownloadId { get; set; }

    [JsonPropertyName("downloadClientId")]
    public int? DownloadClientId { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("sizeleft")]
    public long? SizeLeft { get; set; }

    [JsonPropertyName("quality")]
    public JsonElement? Quality { get; set; }

    [JsonPropertyName("customFormats")]
    public JsonElement? CustomFormats { get; set; }

    [JsonPropertyName("languages")]
    public JsonElement? Languages { get; set; }

    [JsonPropertyName("statusMessages")]
    public List<ArrQueueStatusMessage> StatusMessages { get; set; } = [];

    public bool HasStatusMessage(string message)
    {
        return StatusMessages
            .SelectMany(x => x.Messages)
            .Any(x => x.Contains(message));
    }
}

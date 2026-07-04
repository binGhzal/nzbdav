using System.Text.Json.Serialization;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetFiles;

public sealed class GetFilesResponse : SabBaseResponse
{
    [JsonPropertyName("nzo_id")]
    public string NzoId { get; init; } = "";

    [JsonPropertyName("files")]
    public IReadOnlyList<FileSlot> Files { get; init; } = [];

    public sealed class FileSlot
    {
        [JsonPropertyName("filename")]
        public string Filename { get; init; } = "";

        [JsonPropertyName("status")]
        public string Status { get; init; } = "";

        [JsonPropertyName("mb")]
        public string SizeInMb { get; init; } = "0.00";

        public static FileSlot FromQueueItem(QueueItem queueItem, string status)
        {
            return new FileSlot
            {
                Filename = queueItem.JobName,
                Status = status,
                SizeInMb = GetQueueResponse.QueueSlot.FormatSizeMB(queueItem.TotalSegmentBytes)
            };
        }

        public static FileSlot FromHistoryItem(HistoryItem historyItem)
        {
            return new FileSlot
            {
                Filename = historyItem.JobName,
                Status = historyItem.DownloadStatus.ToString(),
                SizeInMb = GetQueueResponse.QueueSlot.FormatSizeMB(historyItem.TotalSegmentBytes)
            };
        }
    }
}

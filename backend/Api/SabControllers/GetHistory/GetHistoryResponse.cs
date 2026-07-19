using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryResponse : SabBaseResponse
{
    [JsonPropertyName("history")]
    public HistoryObject History { get; set; } = new();

    public class HistoryObject
    {
        [JsonPropertyName("slots")]
        public List<HistorySlot> Slots { get; set; } = [];

        [JsonPropertyName("noofslots")]
        public int TotalCount { get; set; }

        [JsonPropertyName("noofslots_total")]
        public int TotalCountAll { get; set; }

        [JsonPropertyName("start")]
        public int Start { get; init; }

        [JsonPropertyName("limit")]
        public int Limit { get; init; }
    }

    public class HistorySlot
    {
        [JsonPropertyName("nzo_id")]
        public string NzoId { get; set; } = "";

        [JsonPropertyName("nzb_name")]
        public string NzbName { get; set; } = "";

        [JsonPropertyName("name")]
        public string JobName { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HistoryItem.DownloadStatusOption Status { get; set; }

        [JsonPropertyName("bytes")]
        public long SizeInBytes { get; set; }

        [JsonPropertyName("storage")]
        public string? DownloadPath { get; set; }

        [JsonPropertyName("download_time")]
        public int DownloadTimeSeconds { get; set; }

        [JsonPropertyName("fail_message")]
        public string FailMessage { get; set; } = "";

        [JsonPropertyName("nzb_blob_id")]
        public string? NzbBlobId { get; set; }

        public static HistorySlot FromHistoryItem
        (
            HistoryItem historyItem,
            DavItem? downloadFolder,
            ConfigManager configManager
        )
        {
            return new HistorySlot()
            {
                NzoId = historyItem.Id.ToString(),
                NzbName = historyItem.FileName,
                JobName = historyItem.JobName,
                Category = historyItem.Category,
                Status = historyItem.DownloadStatus,
                SizeInBytes = historyItem.TotalSegmentBytes,
                DownloadPath = GetDownloadPath(historyItem, downloadFolder, configManager),
                DownloadTimeSeconds = historyItem.DownloadTimeSeconds,
                FailMessage = PublicDiagnosticContract.HistoryFailureDetail(
                    historyItem.FailMessage) ?? "",
                NzbBlobId = historyItem.NzbBlobId?.ToString(),
            };
        }

        private static string? GetDownloadPath
        (
            HistoryItem historyItem,
            DavItem? downloadFolder,
            ConfigManager configManager
        )
        {
            // return null for null download folder
            if (downloadFolder == null) return null;
            return Path.Join(new[]
            {
                configManager.GetMountDir(),
                DavItem.SymlinkFolder.Name,
                historyItem.Category,
                downloadFolder.Name
            });
        }
    }
}

using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusResponse
{
    [JsonPropertyName("status")]
    public required FullStatusObject Status { get; init; }

    public class FullStatusObject
    {
        [JsonPropertyName("paused")]
        public bool Paused { get; init; }

        [JsonPropertyName("paused_all")]
        public bool PausedAll { get; init; }

        [JsonPropertyName("queue_status")]
        public required string QueueStatus { get; init; }

        [JsonPropertyName("jobs")]
        public int Jobs { get; init; }

        [JsonPropertyName("jobs_active")]
        public int JobsActive { get; init; }

        [JsonPropertyName("max_queue_workers")]
        public int MaxQueueWorkers { get; init; }

        [JsonPropertyName("max_download_connections")]
        public int MaxDownloadConnections { get; init; }

        [JsonPropertyName("adaptive_max_download_connections")]
        public int AdaptiveMaxDownloadConnections { get; init; }

        [JsonPropertyName("active_streams")]
        public int ActiveStreams { get; init; }

        [JsonPropertyName("total_streams_opened")]
        public long TotalStreamsOpened { get; init; }

        [JsonPropertyName("pid")]
        public int ProcessId { get; init; }

        [JsonPropertyName("uptime")]
        public required string Uptime { get; init; }

        [JsonPropertyName("version")]
        public required string Version { get; init; }

        [JsonPropertyName("completedir")]
        public required string CompleteDir { get; init; }

        [JsonPropertyName("downloaddir")]
        public required string DownloadDir { get; init; }

        [JsonPropertyName("speed")]
        public string Speed { get; init; } = "0 ";

        [JsonPropertyName("kbpersec")]
        public string KbPerSec { get; init; } = "0.00";

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; init; } = [];

        [JsonPropertyName("have_warnings")]
        public string HaveWarnings { get; init; } = "0";

        [JsonPropertyName("restart_req")]
        public bool RestartRequired { get; init; }

        [JsonPropertyName("power_options")]
        public bool PowerOptions { get; init; }

        [JsonPropertyName("pp_pause_event")]
        public bool PostProcessingPaused { get; init; }
    }
}

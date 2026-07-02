using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetStatus;

public class GetStatusResponse
{
    [JsonPropertyName("status")]
    public required StatusObject Status { get; init; }

    public class StatusObject
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

        [JsonPropertyName("queue_file_processing_concurrency")]
        public int QueueFileProcessingConcurrency { get; init; }

        [JsonPropertyName("healthcheck_concurrency")]
        public int HealthCheckConcurrency { get; init; }

        [JsonPropertyName("max_streaming_connections")]
        public int MaxStreamingConnections { get; init; }

        [JsonPropertyName("max_total_streaming_connections")]
        public int MaxTotalStreamingConnections { get; init; }

        [JsonPropertyName("active_streams")]
        public int ActiveStreams { get; init; }

        [JsonPropertyName("rclone_invalidations")]
        public RcloneInvalidationStatus RcloneInvalidations { get; init; } = new();

        [JsonPropertyName("cache")]
        public CacheStatus Cache { get; init; } = new();

        [JsonPropertyName("provider_diagnostics")]
        public IReadOnlyList<ProviderDiagnosticStatus> ProviderDiagnostics { get; init; } = [];

        [JsonPropertyName("worker_queues")]
        public WorkerQueueStatus WorkerQueues { get; init; } = new();

        [JsonPropertyName("repair_runs")]
        public RepairRunsStatus RepairRuns { get; init; } = new();

        [JsonPropertyName("total_streams_opened")]
        public long TotalStreamsOpened { get; init; }

        [JsonPropertyName("managed_memory_bytes")]
        public long ManagedMemoryBytes { get; init; }

        [JsonPropertyName("working_set_bytes")]
        public long WorkingSetBytes { get; init; }

        [JsonPropertyName("gc_memory_load_percent")]
        public double GcMemoryLoadPercent { get; init; }

        [JsonPropertyName("process_cpu_cores")]
        public double ProcessCpuCores { get; init; }

        [JsonPropertyName("cpu_pressure_multiplier")]
        public double CpuPressureMultiplier { get; init; }

        [JsonPropertyName("runtime_pressure_multiplier")]
        public double RuntimePressureMultiplier { get; init; }

        [JsonPropertyName("threadpool_threads")]
        public int ThreadPoolThreads { get; init; }

        [JsonPropertyName("threadpool_pending_work_items")]
        public long ThreadPoolPendingWorkItems { get; init; }

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

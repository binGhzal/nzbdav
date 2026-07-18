using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Telemetry;

namespace NzbWebDAV.Api.SabControllers;

public sealed class DatabaseStatus
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("database_bytes")]
    public long DatabaseBytes { get; init; }

    [JsonPropertyName("wal_bytes")]
    public long WalBytes { get; init; }

    [JsonPropertyName("shared_memory_bytes")]
    public long SharedMemoryBytes { get; init; }

    [JsonPropertyName("page_size_bytes")]
    public long PageSizeBytes { get; init; }

    [JsonPropertyName("page_count")]
    public long PageCount { get; init; }

    [JsonPropertyName("freelist_pages")]
    public long FreelistPages { get; init; }

    [JsonPropertyName("freelist_bytes")]
    public long FreelistBytes { get; init; }

    [JsonPropertyName("checkpoint_busy")]
    public long CheckpointBusy { get; init; }

    [JsonPropertyName("wal_frames")]
    public long WalFrames { get; init; }

    [JsonPropertyName("checkpointed_frames")]
    public long CheckpointedFrames { get; init; }

    [JsonPropertyName("checkpoint_backlog_bytes")]
    public long CheckpointBacklogBytes { get; init; }

    [JsonPropertyName("busy_retries")]
    public long BusyRetries { get; init; }

    [JsonPropertyName("lease_retries")]
    public long LeaseRetries { get; init; }

    [JsonPropertyName("query_samples")]
    public long QuerySamples { get; init; }

    [JsonPropertyName("query_p95_ms")]
    public double QueryP95Milliseconds { get; init; }

    [JsonPropertyName("query_p99_ms")]
    public double QueryP99Milliseconds { get; init; }

    [JsonPropertyName("transaction_samples")]
    public long TransactionSamples { get; init; }

    [JsonPropertyName("transaction_p95_ms")]
    public double TransactionP95Milliseconds { get; init; }

    [JsonPropertyName("transaction_p99_ms")]
    public double TransactionP99Milliseconds { get; init; }

    [JsonPropertyName("captured_at")]
    public DateTimeOffset CapturedAt { get; init; }

    public static DatabaseStatus FromSnapshots(
        DatabaseStorageSnapshot storage,
        DatabaseTelemetrySnapshot runtime)
    {
        return new DatabaseStatus
        {
            Provider = storage.Provider,
            DatabaseBytes = storage.DatabaseBytes,
            WalBytes = storage.WalBytes,
            SharedMemoryBytes = storage.SharedMemoryBytes,
            PageSizeBytes = storage.PageSizeBytes,
            PageCount = storage.PageCount,
            FreelistPages = storage.FreelistPages,
            FreelistBytes = checked(storage.FreelistPages * storage.PageSizeBytes),
            CheckpointBusy = storage.CheckpointBusy,
            WalFrames = storage.WalFrames,
            CheckpointedFrames = storage.CheckpointedFrames,
            CheckpointBacklogBytes = storage.CheckpointBacklogBytes,
            BusyRetries = runtime.BusyRetries,
            LeaseRetries = runtime.LeaseRetries,
            QuerySamples = runtime.Query.Count,
            QueryP95Milliseconds = runtime.Query.P95Milliseconds,
            QueryP99Milliseconds = runtime.Query.P99Milliseconds,
            TransactionSamples = runtime.Transaction.Count,
            TransactionP95Milliseconds = runtime.Transaction.P95Milliseconds,
            TransactionP99Milliseconds = runtime.Transaction.P99Milliseconds,
            CapturedAt = storage.CapturedAt
        };
    }
}

public sealed class CriticalPathStatus
{
    [JsonPropertyName("add_file_blob_write")]
    public CriticalPathStageStatus AddFileBlobWrite { get; init; } = new();

    [JsonPropertyName("add_file_nzb_scan")]
    public CriticalPathStageStatus AddFileNzbScan { get; init; } = new();

    [JsonPropertyName("add_file_atomic_commit")]
    public CriticalPathStageStatus AddFileAtomicCommit { get; init; } = new();

    [JsonPropertyName("queue_parse")]
    public CriticalPathStageStatus QueueParse { get; init; } = new();

    [JsonPropertyName("queue_first_segment_discovery")]
    public CriticalPathStageStatus QueueFirstSegmentDiscovery { get; init; } = new();

    [JsonPropertyName("queue_par2_discovery")]
    public CriticalPathStageStatus QueuePar2Discovery { get; init; } = new();

    [JsonPropertyName("queue_processors")]
    public CriticalPathStageStatus QueueProcessors { get; init; } = new();

    [JsonPropertyName("queue_completion")]
    public CriticalPathStageStatus QueueCompletion { get; init; } = new();

    public static CriticalPathStatus FromSnapshot(CriticalPathTelemetrySnapshot snapshot)
    {
        return new CriticalPathStatus
        {
            AddFileBlobWrite = CriticalPathStageStatus.FromSnapshot(snapshot.AddFileBlobWrite),
            AddFileNzbScan = CriticalPathStageStatus.FromSnapshot(snapshot.AddFileNzbScan),
            AddFileAtomicCommit = CriticalPathStageStatus.FromSnapshot(snapshot.AddFileAtomicCommit),
            QueueParse = CriticalPathStageStatus.FromSnapshot(snapshot.QueueParse),
            QueueFirstSegmentDiscovery = CriticalPathStageStatus.FromSnapshot(snapshot.QueueFirstSegmentDiscovery),
            QueuePar2Discovery = CriticalPathStageStatus.FromSnapshot(snapshot.QueuePar2Discovery),
            QueueProcessors = CriticalPathStageStatus.FromSnapshot(snapshot.QueueProcessors),
            QueueCompletion = CriticalPathStageStatus.FromSnapshot(snapshot.QueueCompletion)
        };
    }
}

public sealed class CriticalPathStageStatus
{
    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("failures")]
    public long Failures { get; init; }

    [JsonPropertyName("latency_samples")]
    public int LatencySamples { get; init; }

    [JsonPropertyName("p95_ms")]
    public double P95Milliseconds { get; init; }

    [JsonPropertyName("p99_ms")]
    public double P99Milliseconds { get; init; }

    public static CriticalPathStageStatus FromSnapshot(CriticalPathStageSnapshot snapshot)
    {
        return new CriticalPathStageStatus
        {
            Count = snapshot.Count,
            Failures = snapshot.Failures,
            LatencySamples = snapshot.LatencySamples,
            P95Milliseconds = snapshot.P95Milliseconds,
            P99Milliseconds = snapshot.P99Milliseconds
        };
    }
}

public sealed class RcloneInvalidationStatus
{
    [JsonPropertyName("pending")]
    public int Pending { get; init; }

    [JsonPropertyName("ready")]
    public int Ready { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("oldest_pending_age_seconds")]
    public double? OldestPendingAgeSeconds { get; init; }

    [JsonPropertyName("visibility_fence_required")]
    public bool VisibilityFenceRequired { get; init; }

    [JsonPropertyName("whole_cache_visibility_fence_pending")]
    public bool WholeCacheVisibilityFencePending { get; init; }

    [JsonPropertyName("remote_control_enabled")]
    public bool RemoteControlEnabled { get; init; }

    [JsonPropertyName("host_configured")]
    public bool HostConfigured { get; init; }

    [JsonPropertyName("last_attempt_at")]
    public DateTimeOffset? LastAttemptAt { get; init; }

    [JsonPropertyName("last_successful_configured_call_at")]
    public DateTimeOffset? LastSuccessfulConfiguredCallAt { get; init; }

    [JsonPropertyName("runtime_last_error")]
    public string? RuntimeLastError { get; init; }

    public static RcloneInvalidationStatus FromSnapshots(
        DavDatabaseClient.RcloneInvalidationStats stats,
        RcloneRuntimeSnapshot runtime,
        DateTimeOffset now)
    {
        return new RcloneInvalidationStatus
        {
            Pending = stats.Pending,
            Ready = stats.Ready,
            Failed = stats.Failed,
            MaxAttempts = stats.MaxAttempts,
            LastError = RcloneInvalidationService.GetStatusSafeError(stats.LastError),
            OldestPendingAgeSeconds = stats.OldestPendingAt is null
                ? null
                : Math.Max(0, (now - stats.OldestPendingAt.Value).TotalSeconds),
            VisibilityFenceRequired = runtime.VisibilityFenceRequired,
            WholeCacheVisibilityFencePending =
                runtime.VisibilityFenceRequired
                && (stats.WholeCacheVisibilityFencePending
                    || runtime.WholeCacheVisibilityFencePending),
            RemoteControlEnabled = runtime.RemoteControlEnabled,
            HostConfigured = runtime.HostConfigured,
            LastAttemptAt = runtime.LastAttemptAt,
            LastSuccessfulConfiguredCallAt = runtime.LastSuccessfulConfiguredCallAt,
            RuntimeLastError = runtime.LastError
        };
    }
}

public sealed class ProviderDiagnosticStatus
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("max_connections")]
    public int MaxConnections { get; init; }

    [JsonPropertyName("live_connections")]
    public int LiveConnections { get; init; }

    [JsonPropertyName("idle_connections")]
    public int IdleConnections { get; init; }

    [JsonPropertyName("active_connections")]
    public int ActiveConnections { get; init; }

    [JsonPropertyName("available_connections")]
    public int AvailableConnections { get; init; }

    [JsonPropertyName("ssl")]
    public bool UseSsl { get; init; }

    [JsonPropertyName("configured_ssl")]
    public bool ConfiguredUseSsl { get; init; }

    [JsonPropertyName("implicit_tls")]
    public bool ImplicitTls { get; init; }

    [JsonPropertyName("stat_pipelining_enabled")]
    public bool StatPipeliningEnabled { get; init; }

    [JsonPropertyName("failure_count")]
    public int FailureCount { get; init; }

    [JsonPropertyName("circuit_state")]
    public string CircuitState { get; init; } = "unknown";

    [JsonPropertyName("cooldown_until")]
    public DateTimeOffset? CooldownUntil { get; init; }

    [JsonPropertyName("last_success_at")]
    public DateTimeOffset? LastSuccessAt { get; init; }

    [JsonPropertyName("last_failure_at")]
    public DateTimeOffset? LastFailureAt { get; init; }

    [JsonPropertyName("last_failure_kind")]
    public string? LastFailureKind { get; init; }

    [JsonPropertyName("probe_in_flight")]
    public bool ProbeInFlight { get; init; }

    public static IReadOnlyList<ProviderDiagnosticStatus> FromConfig(UsenetProviderConfig config)
    {
        return config.Providers
            .Select((provider, index) => new ProviderDiagnosticStatus
            {
                Name = $"provider-{index + 1}",
                Host = provider.Host,
                Port = provider.Port,
                Type = provider.Type.ToString(),
                Priority = provider.Priority,
                Role = provider.Priority <= 0 ? "primary" : "backup",
                MaxConnections = provider.MaxConnections,
                AvailableConnections = provider.MaxConnections,
                UseSsl = provider.GetEffectiveUseSsl(),
                ConfiguredUseSsl = provider.UseSsl,
                ImplicitTls = provider.IsImplicitTlsEnabled(),
                StatPipeliningEnabled = provider.IsStatPipeliningEnabled(),
                CircuitState = "unknown"
            })
            .ToList();
    }

    public static IReadOnlyList<ProviderDiagnosticStatus> FromSnapshots(
        IReadOnlyList<ProviderPoolSnapshot> snapshots,
        UsenetProviderConfig config)
    {
        if (snapshots.Count == 0) return FromConfig(config);

        return snapshots
            .Select((snapshot, index) =>
            {
                var provider = index < config.Providers.Count ? config.Providers[index] : null;
                return new ProviderDiagnosticStatus
                {
                    Name = $"provider-{index + 1}",
                    Host = provider?.Host ?? snapshot.Name,
                    Port = provider?.Port ?? 0,
                    Type = snapshot.Type,
                    Priority = snapshot.Priority,
                    Role = snapshot.Role,
                    MaxConnections = snapshot.MaxConnections,
                    LiveConnections = snapshot.LiveConnections,
                    IdleConnections = snapshot.IdleConnections,
                    ActiveConnections = snapshot.ActiveConnections,
                    AvailableConnections = snapshot.AvailableConnections,
                    UseSsl = provider?.GetEffectiveUseSsl() ?? false,
                    ConfiguredUseSsl = provider?.UseSsl ?? false,
                    ImplicitTls = provider?.IsImplicitTlsEnabled() ?? false,
                    StatPipeliningEnabled = snapshot.StatPipeliningEnabled,
                    FailureCount = snapshot.Circuit.ConsecutiveFailures,
                    CircuitState = snapshot.Circuit.CircuitState,
                    CooldownUntil = snapshot.Circuit.CooldownUntil,
                    LastSuccessAt = snapshot.Circuit.LastSuccessAt,
                    LastFailureAt = snapshot.Circuit.LastFailureAt,
                    LastFailureKind = snapshot.Circuit.LastFailureKind,
                    ProbeInFlight = snapshot.Circuit.ProbeInFlight
                };
            })
            .ToList();
    }
}

public sealed class CacheStatus
{
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    [JsonPropertyName("max_bytes")]
    public long MaxBytes { get; init; }

    [JsonPropertyName("hits")]
    public long Hits { get; init; }

    [JsonPropertyName("misses")]
    public long Misses { get; init; }

    [JsonPropertyName("evictions")]
    public long Evictions { get; init; }

    [JsonPropertyName("files")]
    public int Files { get; init; }

    [JsonPropertyName("active_readers")]
    public int ActiveReaders { get; init; }

    [JsonPropertyName("read_ahead_active")]
    public int ReadAheadActive { get; init; }

    [JsonPropertyName("pending_fetches")]
    public int PendingFetches { get; init; }

    [JsonPropertyName("first_byte_reads")]
    public long FirstByteReads { get; init; }

    [JsonPropertyName("first_byte_average_ms")]
    public double FirstByteAverageMilliseconds { get; init; }

    [JsonPropertyName("provider_fetch_errors")]
    public long ProviderFetchErrors { get; init; }

    public static CacheStatus FromSnapshot(SparseSegmentCacheSnapshot snapshot)
    {
        return new CacheStatus
        {
            Bytes = snapshot.Bytes,
            MaxBytes = snapshot.MaxBytes,
            Hits = snapshot.Hits,
            Misses = snapshot.Misses,
            Evictions = snapshot.Evictions,
            Files = snapshot.Files,
            ActiveReaders = snapshot.ActiveReaders,
            ReadAheadActive = snapshot.ReadAheadActive,
            PendingFetches = snapshot.PendingFetches,
            FirstByteReads = snapshot.FirstByteReads,
            FirstByteAverageMilliseconds = snapshot.FirstByteAverageMilliseconds,
            ProviderFetchErrors = snapshot.ProviderFetchErrors
        };
    }
}

public sealed class MountDiagnosticStatus
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("directory")]
    public required string Directory { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("fuse_errors")]
    public long FuseErrors { get; init; }

    [JsonPropertyName("active_operations")]
    public int ActiveOperations { get; init; }

    [JsonPropertyName("waiting_operations")]
    public int WaitingOperations { get; init; }

    [JsonPropertyName("last_invalidation_at")]
    public DateTimeOffset? LastInvalidationAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("cache")]
    public CacheStatus? Cache { get; init; }

    public static MountDiagnosticStatus FromSnapshot(MountStatusSnapshot snapshot)
    {
        return new MountDiagnosticStatus
        {
            Type = snapshot.Type,
            Directory = snapshot.Directory,
            Enabled = snapshot.Enabled,
            Ready = snapshot.Ready,
            State = snapshot.State,
            Message = snapshot.Message,
            FuseErrors = snapshot.FuseErrors,
            ActiveOperations = snapshot.ActiveOperations,
            WaitingOperations = snapshot.WaitingOperations,
            LastInvalidationAt = snapshot.LastInvalidationAt,
            UpdatedAt = snapshot.UpdatedAt,
            Cache = snapshot.Cache == null ? null : CacheStatus.FromSnapshot(snapshot.Cache)
        };
    }
}

public sealed class WorkerQueueStatus
{
    [JsonPropertyName("download_max")]
    public int DownloadMax { get; init; }

    [JsonPropertyName("download_state")]
    public string DownloadState { get; init; } = "idle";

    [JsonPropertyName("download_active")]
    public int DownloadActive { get; init; }

    [JsonPropertyName("download_waiting")]
    public int DownloadWaiting { get; init; }

    [JsonPropertyName("download_ready")]
    public int DownloadReady { get; init; }

    [JsonPropertyName("download_retry")]
    public int DownloadRetry { get; init; }

    [JsonPropertyName("download_quarantined")]
    public int DownloadQuarantined { get; init; }

    [JsonPropertyName("verify_max")]
    public int VerifyMax { get; init; }

    [JsonPropertyName("verify_state")]
    public string VerifyState { get; init; } = "idle";

    [JsonPropertyName("verify_active")]
    public int VerifyActive { get; init; }

    [JsonPropertyName("verify_waiting")]
    public int VerifyWaiting { get; init; }

    [JsonPropertyName("verify_ready")]
    public int VerifyReady { get; init; }

    [JsonPropertyName("verify_retry")]
    public int VerifyRetry { get; init; }

    [JsonPropertyName("verify_quarantined")]
    public int VerifyQuarantined { get; init; }

    [JsonPropertyName("repair_max")]
    public int RepairMax { get; init; }

    [JsonPropertyName("repair_state")]
    public string RepairState { get; init; } = "idle";

    [JsonPropertyName("repair_active")]
    public int RepairActive { get; init; }

    [JsonPropertyName("repair_action_needed")]
    public int RepairActionNeeded { get; init; }

    [JsonPropertyName("repair_ready")]
    public int RepairReady { get; init; }

    [JsonPropertyName("repair_retry")]
    public int RepairRetry { get; init; }

    [JsonPropertyName("repair_quarantined")]
    public int RepairQuarantined { get; init; }

    public static WorkerQueueStatus FromStats
    (
        int downloadActive,
        int downloadWaiting,
        int inlineVerifyActive,
        int inlineVerifyWaiting,
        int maxDownloadWorkers,
        int maxVerifyWorkers,
        int maxRepairWorkers,
        bool downloadsPaused,
        HealthCheckService.WorkerSnapshot healthWorkers,
        DavDatabaseClient.HealthWorkerQueueStats healthQueue,
        DavDatabaseClient.WorkerJobQueueStats durableJobs
    )
    {
        var effectiveDownloadActive = downloadActive;
        var effectiveDownloadReady = durableJobs.Download.Ready;
        var effectiveVerifyActive = healthWorkers.VerifyActive + inlineVerifyActive;
        var effectiveVerifyReady = Math.Max(healthQueue.VerifyReady + inlineVerifyWaiting, durableJobs.Verify.Ready);
        var effectiveRepairActive = healthWorkers.RepairActive;
        return new WorkerQueueStatus
        {
            DownloadMax = maxDownloadWorkers,
            DownloadState = downloadsPaused
                ? "paused"
                : GetLaneState(effectiveDownloadActive, effectiveDownloadReady, durableJobs.Download.Retry, durableJobs.Download.Quarantined, maxDownloadWorkers),
            DownloadActive = effectiveDownloadActive,
            DownloadWaiting = downloadWaiting,
            DownloadReady = durableJobs.Download.Ready,
            DownloadRetry = durableJobs.Download.Retry,
            DownloadQuarantined = durableJobs.Download.Quarantined,
            VerifyMax = maxVerifyWorkers,
            VerifyState = GetLaneState(effectiveVerifyActive, effectiveVerifyReady, durableJobs.Verify.Retry, durableJobs.Verify.Quarantined, maxVerifyWorkers),
            VerifyActive = effectiveVerifyActive,
            VerifyWaiting = inlineVerifyWaiting,
            VerifyReady = effectiveVerifyReady,
            VerifyRetry = durableJobs.Verify.Retry,
            VerifyQuarantined = durableJobs.Verify.Quarantined,
            RepairMax = maxRepairWorkers,
            RepairState = GetLaneState(effectiveRepairActive, durableJobs.Repair.Ready, durableJobs.Repair.Retry, durableJobs.Repair.Quarantined, maxRepairWorkers),
            RepairActive = effectiveRepairActive,
            RepairActionNeeded = healthQueue.RepairActionNeeded,
            RepairReady = durableJobs.Repair.Ready,
            RepairRetry = durableJobs.Repair.Retry,
            RepairQuarantined = durableJobs.Repair.Quarantined
        };
    }

    private static string GetLaneState(int active, int ready, int retry, int quarantined, int max)
    {
        if (max <= 0) return "disabled";
        if (active >= max && ready > 0) return "saturated";
        if (active > 0) return "active";
        if (retry > 0) return "retrying";
        if (ready > 0) return "ready";
        if (quarantined > 0) return "quarantined";
        return "idle";
    }
}

public sealed class RepairRunsStatus
{
    [JsonPropertyName("active")]
    public RepairRunSummaryStatus? Active { get; init; }

    [JsonPropertyName("last")]
    public RepairRunSummaryStatus? Last { get; init; }

    [JsonPropertyName("broken_files")]
    public int BrokenFiles { get; init; }

    [JsonPropertyName("next_due_at")]
    public DateTimeOffset? NextDueAt { get; init; }

    public static RepairRunsStatus FromRuns(RepairRun? active, RepairRun? last, int brokenFiles)
    {
        return new RepairRunsStatus
        {
            Active = active == null ? null : RepairRunSummaryStatus.FromRun(active),
            Last = last == null ? null : RepairRunSummaryStatus.FromRun(last),
            BrokenFiles = brokenFiles,
            NextDueAt = active?.NextDueAt ?? last?.NextDueAt
        };
    }
}

public sealed class RepairRunSummaryStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("checked")]
    public int Checked { get; init; }

    [JsonPropertyName("missing")]
    public int Missing { get; init; }

    [JsonPropertyName("provider_errors")]
    public int ProviderErrors { get; init; }

    [JsonPropertyName("unknown")]
    public int Unknown { get; init; }

    [JsonPropertyName("repaired")]
    public int Repaired { get; init; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; init; }

    [JsonPropertyName("action_needed")]
    public int ActionNeeded { get; init; }

    [JsonPropertyName("broken_files")]
    public int BrokenFiles { get; init; }

    public static RepairRunSummaryStatus FromRun(RepairRun run)
    {
        return new RepairRunSummaryStatus
        {
            Id = run.Id.ToString(),
            Status = run.Status.ToString(),
            Stage = run.Stage,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Total = run.Total,
            Checked = run.Checked,
            Missing = run.Missing,
            ProviderErrors = run.ProviderErrors,
            Unknown = run.Unknown,
            Repaired = run.Repaired,
            Deleted = run.Deleted,
            ActionNeeded = run.ActionNeeded,
            BrokenFiles = run.BrokenFiles
        };
    }
}

public sealed class ArrPrioritizationStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("correlations")]
    public int Correlations { get; init; }

    [JsonPropertyName("stale_correlations")]
    public int StaleCorrelations { get; init; }

    [JsonPropertyName("duplicates")]
    public int Duplicates { get; init; }

    [JsonPropertyName("active_hints")]
    public int ActiveHints { get; init; }

    [JsonPropertyName("stale_hints")]
    public int StaleHints { get; init; }

    public static ArrPrioritizationStatus FromStats
    (
        ArrConfig.PrioritizationOptions options,
        DavDatabaseClient.ArrIntegrationStats stats
    )
    {
        return new ArrPrioritizationStatus
        {
            Enabled = options.Enabled,
            Mode = options.Mode,
            Correlations = stats.TotalCorrelations,
            StaleCorrelations = stats.StaleCorrelations,
            Duplicates = stats.DuplicateCorrelations,
            ActiveHints = stats.ActivePriorityHints,
            StaleHints = stats.StalePriorityHints
        };
    }
}

public sealed class ArrSearchNudgeStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("planned")]
    public int Planned { get; init; }

    [JsonPropertyName("executed")]
    public int Executed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("last_command_at")]
    public DateTimeOffset? LastCommandAt { get; init; }

    public static ArrSearchNudgeStatus FromStats
    (
        ArrConfig.SearchNudgeOptions options,
        DavDatabaseClient.ArrIntegrationStats stats
    )
    {
        return new ArrSearchNudgeStatus
        {
            Enabled = options.Enabled,
            Mode = options.Mode,
            Planned = stats.PlannedSearchNudges,
            Executed = stats.ExecutedSearchNudges,
            Failed = stats.FailedSearchNudges,
            LastCommandAt = stats.LastSearchNudgeAt
        };
    }
}

public sealed class ArrDownloadReportStatus
{
    [JsonPropertyName("lifecycle_states")]
    public IReadOnlyList<ArrLifecycleStateStatus> LifecycleStates { get; init; } = [];

    public static ArrDownloadReportStatus FromStats(DavDatabaseClient.ArrIntegrationStats stats)
    {
        return new ArrDownloadReportStatus
        {
            LifecycleStates = stats.LifecycleStates
                .Select(x => new ArrLifecycleStateStatus { State = x.State, Count = x.Count })
                .ToList()
        };
    }
}

public sealed class ArrImportCommandDiagnosticStatus
{
    [JsonPropertyName("pending")]
    public int Pending { get; init; }

    [JsonPropertyName("waiting_for_invalidation")]
    public int WaitingForInvalidation { get; init; }

    [JsonPropertyName("executing")]
    public int Executing { get; init; }

    [JsonPropertyName("retry")]
    public int Retry { get; init; }

    [JsonPropertyName("dispatched")]
    public int Dispatched { get; init; }

    [JsonPropertyName("no_route")]
    public int NoRoute { get; init; }

    [JsonPropertyName("quarantined")]
    public int Quarantined { get; init; }

    [JsonPropertyName("oldest_active_age_seconds")]
    public long? OldestActiveAgeSeconds { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("last_quarantine_reason")]
    public string? LastQuarantineReason { get; init; }

    public static ArrImportCommandDiagnosticStatus FromStats(
        DavDatabaseClient.ArrImportCommandStats stats,
        DateTimeOffset now)
    {
        return new ArrImportCommandDiagnosticStatus
        {
            Pending = stats.Pending,
            WaitingForInvalidation = stats.WaitingForInvalidation,
            Executing = stats.Executing,
            Retry = stats.Retry,
            Dispatched = stats.Dispatched,
            NoRoute = stats.NoRoute,
            Quarantined = stats.Quarantined,
            OldestActiveAgeSeconds = stats.OldestActiveAt.HasValue
                ? Math.Max(0, (long)(now - stats.OldestActiveAt.Value).TotalSeconds)
                : null,
            LastError = stats.LastError,
            LastQuarantineReason = stats.LastQuarantineReason,
        };
    }
}

public sealed class ArrLifecycleStateStatus
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

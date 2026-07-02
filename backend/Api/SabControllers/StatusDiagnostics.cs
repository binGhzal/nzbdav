using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;

namespace NzbWebDAV.Api.SabControllers;

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

    public static RcloneInvalidationStatus FromStats(DavDatabaseClient.RcloneInvalidationStats stats)
    {
        return new RcloneInvalidationStatus
        {
            Pending = stats.Pending,
            Ready = stats.Ready,
            Failed = stats.Failed,
            MaxAttempts = stats.MaxAttempts,
            LastError = stats.LastError
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

    [JsonPropertyName("max_connections")]
    public int MaxConnections { get; init; }

    [JsonPropertyName("ssl")]
    public bool UseSsl { get; init; }

    [JsonPropertyName("stat_pipelining_enabled")]
    public bool StatPipeliningEnabled { get; init; }

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
                MaxConnections = provider.MaxConnections,
                UseSsl = provider.UseSsl,
                StatPipeliningEnabled = provider.StatPipeliningEnabled
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

    [JsonPropertyName("pending_fetches")]
    public int PendingFetches { get; init; }

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
            PendingFetches = snapshot.PendingFetches
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

    [JsonPropertyName("verify_active")]
    public int VerifyActive { get; init; }

    [JsonPropertyName("verify_ready")]
    public int VerifyReady { get; init; }

    [JsonPropertyName("verify_retry")]
    public int VerifyRetry { get; init; }

    [JsonPropertyName("verify_quarantined")]
    public int VerifyQuarantined { get; init; }

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
        HealthCheckService.WorkerSnapshot healthWorkers,
        DavDatabaseClient.HealthWorkerQueueStats healthQueue,
        DavDatabaseClient.WorkerJobQueueStats durableJobs
    )
    {
        return new WorkerQueueStatus
        {
            DownloadActive = Math.Max(downloadActive, durableJobs.Download.Leased),
            DownloadWaiting = downloadWaiting,
            DownloadReady = durableJobs.Download.Ready,
            DownloadRetry = durableJobs.Download.Retry,
            DownloadQuarantined = durableJobs.Download.Quarantined,
            VerifyActive = Math.Max(healthWorkers.VerifyActive, durableJobs.Verify.Leased),
            VerifyReady = Math.Max(healthQueue.VerifyReady, durableJobs.Verify.Ready),
            VerifyRetry = durableJobs.Verify.Retry,
            VerifyQuarantined = durableJobs.Verify.Quarantined,
            RepairActive = Math.Max(healthWorkers.RepairActive, durableJobs.Repair.Leased),
            RepairActionNeeded = healthQueue.RepairActionNeeded,
            RepairReady = durableJobs.Repair.Ready,
            RepairRetry = durableJobs.Repair.Retry,
            RepairQuarantined = durableJobs.Repair.Quarantined
        };
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

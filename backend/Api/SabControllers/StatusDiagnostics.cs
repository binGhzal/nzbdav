using System.Text.Json.Serialization;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

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

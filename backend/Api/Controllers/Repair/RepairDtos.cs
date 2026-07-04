using System.Text.Json.Serialization;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Repair;

public sealed class RepairRunDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("cancelled_at")]
    public DateTimeOffset? CancelledAt { get; init; }

    [JsonPropertyName("next_due_at")]
    public DateTimeOffset? NextDueAt { get; init; }

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

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    public static RepairRunDto FromModel(RepairRun run)
    {
        return new RepairRunDto
        {
            Id = run.Id.ToString(),
            Status = run.Status.ToString(),
            Stage = run.Stage,
            StartedAt = run.StartedAt,
            UpdatedAt = run.UpdatedAt,
            CompletedAt = run.CompletedAt,
            CancelledAt = run.CancelledAt,
            NextDueAt = run.NextDueAt,
            Total = run.Total,
            Checked = run.Checked,
            Missing = run.Missing,
            ProviderErrors = run.ProviderErrors,
            Unknown = run.Unknown,
            Repaired = run.Repaired,
            Deleted = run.Deleted,
            ActionNeeded = run.ActionNeeded,
            BrokenFiles = run.BrokenFiles,
            Message = run.Message
        };
    }
}

public sealed class RepairBrokenFileDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("repair_run_id")]
    public required string RepairRunId { get; init; }

    [JsonPropertyName("dav_item_id")]
    public required string DavItemId { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    public static RepairBrokenFileDto FromModel(RepairBrokenFile brokenFile)
    {
        return new RepairBrokenFileDto
        {
            Id = brokenFile.Id.ToString(),
            RepairRunId = brokenFile.RepairRunId.ToString(),
            DavItemId = brokenFile.DavItemId.ToString(),
            Path = brokenFile.Path,
            Reason = brokenFile.Reason,
            CreatedAt = brokenFile.CreatedAt
        };
    }
}

public sealed class RepairWorkerQueueDto
{
    [JsonPropertyName("max")]
    public int Max { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "idle";

    [JsonPropertyName("pending")]
    public int Pending { get; init; }

    [JsonPropertyName("retry")]
    public int Retry { get; init; }

    [JsonPropertyName("leased")]
    public int Leased { get; init; }

    [JsonPropertyName("ready")]
    public int Ready { get; init; }

    [JsonPropertyName("quarantined")]
    public int Quarantined { get; init; }

    [JsonPropertyName("completed")]
    public int Completed { get; init; }

    [JsonPropertyName("cancelled")]
    public int Cancelled { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    public static RepairWorkerQueueDto FromStats(DavDatabaseClient.WorkerJobKindStats stats, int max)
    {
        return new RepairWorkerQueueDto
        {
            Max = max,
            State = GetState(stats, max),
            Pending = stats.Pending,
            Retry = stats.Retry,
            Leased = stats.Leased,
            Ready = stats.Ready,
            Quarantined = stats.Quarantined,
            Completed = stats.Completed,
            Cancelled = stats.Cancelled,
            Total = stats.Total
        };
    }

    private static string GetState(DavDatabaseClient.WorkerJobKindStats stats, int max)
    {
        if (max <= 0) return "disabled";
        if (stats.Leased >= max && stats.Ready > 0) return "saturated";
        if (stats.Leased > 0) return "active";
        if (stats.Retry > 0) return "retrying";
        if (stats.Ready > 0) return "ready";
        if (stats.Quarantined > 0) return "quarantined";
        return "idle";
    }
}

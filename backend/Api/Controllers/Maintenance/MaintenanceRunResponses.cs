using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;

namespace NzbWebDAV.Api.Controllers.Maintenance;

public sealed class MaintenanceRunDto
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancellationRequestedAt { get; init; }
    public required int ProgressCurrent { get; init; }
    public int? ProgressTotal { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }

    public static MaintenanceRunDto FromModel(MaintenanceRun run)
    {
        return new MaintenanceRunDto
        {
            Id = run.Id,
            Kind = MaintenanceRunApiValues.ToApiValue(run.Kind),
            Status = MaintenanceRunApiValues.ToApiValue(run.Status),
            RequestedBy = run.RequestedBy,
            CreatedAt = run.CreatedAt,
            StartedAt = run.StartedAt,
            UpdatedAt = run.UpdatedAt,
            CompletedAt = run.CompletedAt,
            CancellationRequestedAt = run.CancellationRequestedAt,
            ProgressCurrent = run.ProgressCurrent,
            ProgressTotal = run.ProgressTotal,
            Message = run.Status == MaintenanceRunStatus.Failed
                ? "Failed."
                : run.Message,
            Error = PublicDiagnosticContract.FromOptional(
                run.Error,
                PublicDiagnosticKind.MaintenanceFailure),
        };
    }
}

public sealed class MaintenanceRunResponse
{
    public required MaintenanceRunDto Run { get; init; }
}

public sealed class MaintenanceRunConflictResponse : BaseApiResponse
{
    public required MaintenanceRunDto ActiveRun { get; init; }
}

public sealed class MaintenanceStatusResponse
{
    public MaintenanceRunDto? ActiveRun { get; init; }
    public MaintenanceRunDto? LastRun { get; init; }
}

public sealed class MaintenanceRunsResponse
{
    public required IReadOnlyList<MaintenanceRunDto> Runs { get; init; }
}

public static class MaintenanceRunApiValues
{
    public static string ToApiValue(MaintenanceRunKind kind) => kind switch
    {
        MaintenanceRunKind.RemoveUnlinkedFiles => "remove-unlinked-files",
        MaintenanceRunKind.RemoveUnlinkedFilesDryRun => "remove-unlinked-files-dry-run",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string ToApiValue(MaintenanceRunStatus status) => status switch
    {
        MaintenanceRunStatus.Queued => "queued",
        MaintenanceRunStatus.Running => "running",
        MaintenanceRunStatus.Completed => "completed",
        MaintenanceRunStatus.Failed => "failed",
        MaintenanceRunStatus.CancellationRequested => "cancellation-requested",
        MaintenanceRunStatus.Cancelled => "cancelled",
        MaintenanceRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static bool TryParseKind(string? value, out MaintenanceRunKind kind)
    {
        kind = value switch
        {
            "remove-unlinked-files" => MaintenanceRunKind.RemoveUnlinkedFiles,
            "remove-unlinked-files-dry-run" => MaintenanceRunKind.RemoveUnlinkedFilesDryRun,
            _ => default,
        };
        return value is "remove-unlinked-files"
            or "remove-unlinked-files-dry-run";
    }
}

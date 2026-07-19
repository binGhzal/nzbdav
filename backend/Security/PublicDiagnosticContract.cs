using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Security;

public enum PublicDiagnosticKind
{
    QueueFailure,
    MaintenanceFailure,
    SearchNudgeFailure,
    ArrImportFailure,
    RcloneFailure,
    MountFailure,
    RepairFailure,
    HealthFailure,
    WorkerFailure,
    ArrConnectionFailure,
}

public static class PublicDiagnosticContract
{
    public const string ArrImportFailureMessage = "ARR import failed.";
    public const string HealthMissingRepairQueued =
        "Missing articles were confirmed; automatic repair was queued.";
    public const string HealthMissingReviewRequired =
        "Missing articles were confirmed; operator review is required.";
    public const string HealthMetadataMissingRepairQueued =
        "File metadata is missing; automatic repair was queued.";
    public const string HealthMetadataMissingReviewRequired =
        "File metadata is missing; operator review is required.";
    public const string HealthVerificationInconclusive =
        "Article availability could not be confirmed; retry is scheduled before repair.";
    public const string HealthProviderUnavailable =
        "Article provider is temporarily unavailable; retry is scheduled before repair.";
    public const string HealthMetadataUnavailable =
        "File metadata is temporarily unavailable; retry is scheduled before repair.";
    public const string HealthHealthy = "File is healthy.";
    public const string RepairSearchInitiated = "Repair and replacement search were initiated.";
    public const string HealthRepairActionRequired = "Operator action is required for file repair.";
    public const string HealthFileRemoved = "Unhealthy file was removed.";
    public const string RepairVerificationQuarantined =
        "Repair verification failed and the worker job was quarantined.";
    public const string RepairVerificationRetryScheduled =
        "Repair verification failed; retry is scheduled.";
    public const string RepairProviderCheckFailed = "Repair provider check failed.";
    public const string RepairPending = "Repair check is pending.";
    public const string RepairChecking = "Repair check is in progress.";
    public const string RepairMissing = "Missing articles were confirmed.";
    public const string RepairInconclusive = "Repair result is inconclusive.";
    public const string RepairFileRemoved = "File was removed during repair.";
    public const string RepairActionRequired = "Operator action is required.";
    public const string RepairCancelled = "Repair was cancelled.";
    public const string RepairRunInProgress = "Repair run is in progress.";
    public const string RepairRunCompleted = "Repair run completed.";
    public const string RepairRunCancelled = "Repair run cancelled by operator.";
    public const string RepairRunNoEligibleFiles =
        "No eligible files found for repair verification.";
    public const string ConfirmedMissingArticles = "confirmed missing articles";
    public const string ConfirmedMissingArticlesOperatorReview =
        "confirmed missing articles; operator review required";
    public const string ConfirmedMissingAfterDownloadOperatorReview =
        "Confirmed missing articles after download; automatic repair is disabled and operator review is required.";
    public const string PostDownloadVerificationFailure =
        "Post-download article verification failed.";

    public static string Message(PublicDiagnosticKind kind) => kind switch
    {
        PublicDiagnosticKind.QueueFailure => "Queue processing failed.",
        PublicDiagnosticKind.MaintenanceFailure => "Maintenance run failed.",
        PublicDiagnosticKind.SearchNudgeFailure => "ARR search nudge failed.",
        PublicDiagnosticKind.ArrImportFailure => ArrImportFailureMessage,
        PublicDiagnosticKind.RcloneFailure => "Rclone operation failed.",
        PublicDiagnosticKind.MountFailure => "Mount operation failed.",
        PublicDiagnosticKind.RepairFailure => "Repair operation failed.",
        PublicDiagnosticKind.HealthFailure => "Health check failed.",
        PublicDiagnosticKind.WorkerFailure => "Background operation failed.",
        PublicDiagnosticKind.ArrConnectionFailure => "ARR connection test failed.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string? FromOptional(string? value, PublicDiagnosticKind kind) =>
        string.IsNullOrWhiteSpace(value) ? null : Message(kind);

    public static string? HistoryFailureDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value == PostDownloadVerificationFailure
            ? value
            : Message(PublicDiagnosticKind.QueueFailure);
    }

    public static string? VerificationQuarantineDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value switch
        {
            ArrImportFailureMessage => ArrImportFailureMessage,
            ConfirmedMissingArticles => ConfirmedMissingArticles,
            ConfirmedMissingArticlesOperatorReview => ConfirmedMissingArticlesOperatorReview,
            ConfirmedMissingAfterDownloadOperatorReview => ConfirmedMissingAfterDownloadOperatorReview,
            _ => ArrImportFailureMessage,
        };
    }

    public static string HealthDetail(
        string? value,
        HealthCheckResult.RepairAction repairAction)
    {
        if (repairAction == HealthCheckResult.RepairAction.Repaired)
            return RepairSearchInitiated;
        if (repairAction == HealthCheckResult.RepairAction.Deleted)
            return HealthFileRemoved;
        return repairAction switch
        {
            HealthCheckResult.RepairAction.ActionNeeded
                when value is HealthMissingRepairQueued
                    or HealthMissingReviewRequired
                    or HealthMetadataMissingRepairQueued
                    or HealthMetadataMissingReviewRequired => value,
            HealthCheckResult.RepairAction.ActionNeeded => HealthRepairActionRequired,
            HealthCheckResult.RepairAction.None
                when value is HealthProviderUnavailable
                    or HealthVerificationInconclusive
                    or HealthMetadataUnavailable => value,
            _ => Message(PublicDiagnosticKind.HealthFailure),
        };
    }

    public static string? ArrImportFailureDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 2048) return ArrImportFailureMessage;
        if (value == ArrImportFailureMessage || ArrImportTopLevelCodes.Contains(value)) return value;

        var atoms = value.Split("; ", StringSplitOptions.None);
        if (atoms.Length == 0 || atoms.Any(atom => !IsArrImportTargetAtom(atom)))
            return ArrImportFailureMessage;
        return value;
    }

    public static string RepairEntryDetail(
        RepairEntryHealth.RepairEntryState state,
        string? value) => state switch
        {
            RepairEntryHealth.RepairEntryState.Pending => RepairPending,
            RepairEntryHealth.RepairEntryState.Checking => RepairChecking,
            RepairEntryHealth.RepairEntryState.Healthy => HealthHealthy,
            RepairEntryHealth.RepairEntryState.Missing
                when value is HealthMissingRepairQueued
                    or HealthMissingReviewRequired
                    or HealthMetadataMissingRepairQueued
                    or HealthMetadataMissingReviewRequired => value,
            RepairEntryHealth.RepairEntryState.Missing => RepairMissing,
            RepairEntryHealth.RepairEntryState.ProviderError
                when value is HealthProviderUnavailable
                    or HealthMetadataUnavailable
                    or RepairVerificationQuarantined
                    or RepairVerificationRetryScheduled => value,
            RepairEntryHealth.RepairEntryState.ProviderError => RepairProviderCheckFailed,
            RepairEntryHealth.RepairEntryState.Unknown
                when value == HealthVerificationInconclusive => value,
            RepairEntryHealth.RepairEntryState.Unknown => RepairInconclusive,
            RepairEntryHealth.RepairEntryState.Repaired => RepairSearchInitiated,
            RepairEntryHealth.RepairEntryState.Deleted => RepairFileRemoved,
            RepairEntryHealth.RepairEntryState.ActionNeeded => RepairActionRequired,
            RepairEntryHealth.RepairEntryState.Cancelled => RepairCancelled,
            _ => Message(PublicDiagnosticKind.RepairFailure),
        };

    public static string RepairRunDetail(RepairRun.RepairRunStatus status, string? value)
    {
        return status switch
        {
            RepairRun.RepairRunStatus.Running => RepairRunInProgress,
            RepairRun.RepairRunStatus.Completed when value == RepairRunNoEligibleFiles => value,
            RepairRun.RepairRunStatus.Completed => RepairRunCompleted,
            RepairRun.RepairRunStatus.Cancelled => RepairRunCancelled,
            RepairRun.RepairRunStatus.Failed => Message(PublicDiagnosticKind.RepairFailure),
            _ => Message(PublicDiagnosticKind.RepairFailure),
        };
    }

    public static string? ImportReceiptDetail(ImportReceiptState state, string? value) => state switch
    {
        ImportReceiptState.NeedsReview => FromOptional(value, PublicDiagnosticKind.ArrImportFailure),
        ImportReceiptState.VerificationQuarantined => VerificationQuarantineDetail(value),
        _ => null,
    };

    private static bool IsArrImportTargetAtom(string atom)
    {
        var parts = atom.Split(':');
        if (parts.Length != 3 || !ArrImportApps.Contains(parts[0])) return false;
        if (parts[1].Length != 16
            || parts[1].Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
            return false;

        var reasons = parts[2].Split('+');
        return reasons.Length is 1 or 2
               && reasons.Distinct(StringComparer.Ordinal).Count() == reasons.Length
               && reasons.All(ArrImportReasonCodes.Contains);
    }

    private static readonly HashSet<string> ArrImportApps = new(StringComparer.Ordinal)
    {
        "sonarr", "radarr", "lidarr", "arr",
    };

    private static readonly HashSet<string> ArrImportTopLevelCodes = new(StringComparer.Ordinal)
    {
        "worker-error",
        "route-missing-correlated-instance",
        "route-no-configured-instances",
        "route-category-owner-missing",
        "route-category-owner-ambiguous",
    };

    private static readonly HashSet<string> ArrImportReasonCodes = new(StringComparer.Ordinal)
    {
        "ownership-timeout", "ownership-http", "ownership-malformed", "ownership-error",
        "queue-timeout", "queue-http", "queue-malformed", "queue-error",
        "direct-timeout", "direct-http", "direct-error",
        "refresh-timeout", "refresh-http", "refresh-malformed", "refresh-error",
        "invalid-command", "dispatch-error", "lease-not-authorized",
        "authorization-timeout", "authorization-error", "policy-refused",
        "unsupported-target", "route-not-correlated", "correlation-missing",
        "correlation-download-id-missing", "correlation-download-id-conflict",
        "correlation-media-identity-invalid", "correlation-media-identity-conflict",
        "queue-incomplete", "queue-type-mismatch", "queue-match-missing",
        "queue-match-duplicate", "queue-protocol-unsupported", "queue-output-path-missing",
        "queue-media-identity-invalid", "queue-media-identity-conflict",
    };
}

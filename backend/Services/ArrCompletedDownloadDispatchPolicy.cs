using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;

namespace NzbWebDAV.Services;

public enum ArrCompletedDownloadClientKind
{
    Unknown,
    Sonarr,
    Radarr,
    Lidarr
}

public enum ArrCompletedDownloadRouteKind
{
    None,
    Correlation,
    CategoryOwnership
}

public enum ArrCompletedDownloadFallbackReason
{
    UnsupportedTarget,
    RouteNotCorrelated,
    CorrelationMissing,
    CorrelationDownloadIdMissing,
    CorrelationDownloadIdConflict,
    CorrelationMediaIdentityInvalid,
    CorrelationMediaIdentityConflict,
    QueueMalformed,
    QueueIncomplete,
    QueueTypeMismatch,
    QueueMatchMissing,
    QueueMatchDuplicate,
    QueueProtocolUnsupported,
    QueueOutputPathMissing,
    QueueMediaIdentityInvalid,
    QueueMediaIdentityConflict
}

public sealed record ArrCompletedDownloadTarget(
    string App,
    ArrCompletedDownloadClientKind ClientKind,
    ArrCompletedDownloadRouteKind RouteKind)
{
    public override string ToString() => "arr-completed-download-target:redacted";
}

public sealed record ArrCompletedDownloadCorrelationFact(
    string? DownloadId,
    int? MovieId = null,
    int? SeriesId = null,
    int? EpisodeId = null)
{
    public override string ToString() => "arr-completed-download-correlation:redacted";
}

public sealed class ArrCompletedDownloadDirectScanRequest
{
    internal ArrCompletedDownloadDirectScanRequest(
        ArrCompletedDownloadClientKind clientKind,
        string commandName,
        string path,
        string downloadClientId)
    {
        ClientKind = clientKind;
        CommandName = commandName;
        Path = path;
        DownloadClientId = downloadClientId;
    }

    public ArrCompletedDownloadClientKind ClientKind { get; }

    public string CommandName { get; }

    public string Path { get; }

    public string DownloadClientId { get; }

    public override string ToString() => ClientKind switch
    {
        ArrCompletedDownloadClientKind.Sonarr => "direct-scan:sonarr",
        ArrCompletedDownloadClientKind.Radarr => "direct-scan:radarr",
        _ => "direct-scan:unsupported"
    };
}

public sealed class ArrCompletedDownloadDispatchPreparation
{
    internal ArrCompletedDownloadDispatchPreparation(
        ArrCompletedDownloadTarget? target,
        IReadOnlyCollection<ArrCompletedDownloadCorrelationFact>? correlations,
        string? downloadId,
        ArrCompletedDownloadFallbackReason? fallbackReason)
    {
        Target = target;
        Correlations = correlations;
        DownloadId = downloadId;
        FallbackReason = fallbackReason;
    }

    internal ArrCompletedDownloadTarget? Target { get; }

    internal IReadOnlyCollection<ArrCompletedDownloadCorrelationFact>? Correlations { get; }

    internal string? DownloadId { get; }

    public ArrCompletedDownloadFallbackReason? FallbackReason { get; }

    public string? FallbackReasonCode => FallbackReason is { } reason
        ? ArrCompletedDownloadDispatchPolicy.GetFallbackReasonCode(reason)
        : null;

    public bool ShouldProbeQueue => FallbackReason is null;

    public override string ToString() => ShouldProbeQueue
        ? "arr-completed-download-preparation:queue-probe"
        : $"arr-completed-download-preparation:refresh-only:{FallbackReasonCode}";
}

public sealed class ArrCompletedDownloadDispatchDecision
{
    private ArrCompletedDownloadDispatchDecision(
        ArrCompletedDownloadDirectScanRequest? directScan,
        ArrCompletedDownloadFallbackReason? fallbackReason)
    {
        DirectScan = directScan;
        FallbackReason = fallbackReason;
    }

    public ArrCompletedDownloadDirectScanRequest? DirectScan { get; }

    public ArrCompletedDownloadFallbackReason? FallbackReason { get; }

    public string? FallbackReasonCode => FallbackReason is { } reason
        ? ArrCompletedDownloadDispatchPolicy.GetFallbackReasonCode(reason)
        : null;

    public bool IsDirectScan => DirectScan is not null;

    internal static ArrCompletedDownloadDispatchDecision Direct(
        ArrCompletedDownloadDirectScanRequest request) => new(request, null);

    internal static ArrCompletedDownloadDispatchDecision Fallback(
        ArrCompletedDownloadFallbackReason reason) => new(null, reason);

    public override string ToString() => IsDirectScan
        ? "arr-completed-download-dispatch:direct-scan"
        : $"arr-completed-download-dispatch:refresh-only:{FallbackReasonCode}";
}

public static class ArrCompletedDownloadDispatchPolicy
{
    public const int MaximumQueueRecords = 5000;

    public static ArrCompletedDownloadDispatchDecision Evaluate<TQueueRecord>(
        ArrCompletedDownloadTarget? target,
        IReadOnlyCollection<ArrCompletedDownloadCorrelationFact>? correlations,
        ArrQueue<TQueueRecord>? queue)
        where TQueueRecord : ArrQueueRecord
    {
        return Evaluate(Prepare(target, correlations), queue);
    }

    public static ArrCompletedDownloadDispatchPreparation Prepare(
        ArrCompletedDownloadTarget? target,
        IReadOnlyCollection<ArrCompletedDownloadCorrelationFact>? correlations)
    {
        if (!IsSupportedTarget(target))
            return PreparationFallback(ArrCompletedDownloadFallbackReason.UnsupportedTarget);

        if (target!.RouteKind != ArrCompletedDownloadRouteKind.Correlation)
            return PreparationFallback(ArrCompletedDownloadFallbackReason.RouteNotCorrelated);

        if (correlations is null || correlations.Count == 0 || correlations.Any(fact => fact is null))
            return PreparationFallback(ArrCompletedDownloadFallbackReason.CorrelationMissing);

        var correlationSnapshot = correlations.ToArray();
        if (correlationSnapshot.Any(fact => string.IsNullOrWhiteSpace(fact.DownloadId)))
            return PreparationFallback(ArrCompletedDownloadFallbackReason.CorrelationDownloadIdMissing);

        var downloadIds = correlationSnapshot
            .Select(fact => fact.DownloadId!)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (downloadIds.Length != 1)
            return PreparationFallback(ArrCompletedDownloadFallbackReason.CorrelationDownloadIdConflict);

        if (HasInvalidCorrelationMediaIdentity(target.ClientKind, correlationSnapshot))
            return PreparationFallback(ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityInvalid);

        if (HasCorrelationMediaIdentityConflict(target.ClientKind, correlationSnapshot))
            return PreparationFallback(ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityConflict);

        return new ArrCompletedDownloadDispatchPreparation(target, correlationSnapshot, downloadIds[0], null);
    }

    public static ArrCompletedDownloadDispatchDecision Evaluate<TQueueRecord>(
        ArrCompletedDownloadDispatchPreparation? preparation,
        ArrQueue<TQueueRecord>? queue)
        where TQueueRecord : ArrQueueRecord
    {
        if (preparation?.FallbackReason is { } fallbackReason)
            return Fallback(fallbackReason);

        if (preparation?.Target is not { } target
            || preparation.Correlations is not { } correlations
            || preparation.DownloadId is not { } downloadId)
        {
            return Fallback(ArrCompletedDownloadFallbackReason.CorrelationMissing);
        }

        if (queue is null || queue.Records is null || queue.Records.Any(record => record is null))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMalformed);

        if (!QueueTypeMatchesTarget<TQueueRecord>(target.ClientKind))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueTypeMismatch);

        if (queue.Page != 1
            || queue.PageSize < 1
            || queue.PageSize > MaximumQueueRecords
            || queue.TotalRecords < 0
            || queue.Records.Count > queue.PageSize
            || queue.Records.Count > MaximumQueueRecords)
        {
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMalformed);
        }

        if (queue.TotalRecords != queue.Records.Count)
            return Fallback(ArrCompletedDownloadFallbackReason.QueueIncomplete);

        var matchingRecords = queue.Records
            .Where(record => string.Equals(record.DownloadId, downloadId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingRecords.Length == 0)
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMatchMissing);
        if (matchingRecords.Length != 1)
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMatchDuplicate);

        var matchingRecord = matchingRecords[0];
        if (!string.Equals(matchingRecord.Protocol, "usenet", StringComparison.Ordinal))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueProtocolUnsupported);

        if (string.IsNullOrWhiteSpace(matchingRecord.OutputPath))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueOutputPathMissing);

        if (HasInvalidQueueMediaIdentity(target.ClientKind, matchingRecord))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMediaIdentityInvalid);

        if (HasQueueMediaIdentityConflict(target.ClientKind, correlations, matchingRecord))
            return Fallback(ArrCompletedDownloadFallbackReason.QueueMediaIdentityConflict);

        var commandName = target.ClientKind == ArrCompletedDownloadClientKind.Sonarr
            ? "DownloadedEpisodesScan"
            : "DownloadedMoviesScan";
        return ArrCompletedDownloadDispatchDecision.Direct(
            new ArrCompletedDownloadDirectScanRequest(
                target.ClientKind,
                commandName,
                matchingRecord.OutputPath,
                downloadId));
    }

    public static string GetFallbackReasonCode(ArrCompletedDownloadFallbackReason reason) => reason switch
    {
        ArrCompletedDownloadFallbackReason.UnsupportedTarget => "unsupported-target",
        ArrCompletedDownloadFallbackReason.RouteNotCorrelated => "route-not-correlated",
        ArrCompletedDownloadFallbackReason.CorrelationMissing => "correlation-missing",
        ArrCompletedDownloadFallbackReason.CorrelationDownloadIdMissing => "correlation-download-id-missing",
        ArrCompletedDownloadFallbackReason.CorrelationDownloadIdConflict => "correlation-download-id-conflict",
        ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityInvalid => "correlation-media-identity-invalid",
        ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityConflict => "correlation-media-identity-conflict",
        ArrCompletedDownloadFallbackReason.QueueMalformed => "queue-malformed",
        ArrCompletedDownloadFallbackReason.QueueIncomplete => "queue-incomplete",
        ArrCompletedDownloadFallbackReason.QueueTypeMismatch => "queue-type-mismatch",
        ArrCompletedDownloadFallbackReason.QueueMatchMissing => "queue-match-missing",
        ArrCompletedDownloadFallbackReason.QueueMatchDuplicate => "queue-match-duplicate",
        ArrCompletedDownloadFallbackReason.QueueProtocolUnsupported => "queue-protocol-unsupported",
        ArrCompletedDownloadFallbackReason.QueueOutputPathMissing => "queue-output-path-missing",
        ArrCompletedDownloadFallbackReason.QueueMediaIdentityInvalid => "queue-media-identity-invalid",
        ArrCompletedDownloadFallbackReason.QueueMediaIdentityConflict => "queue-media-identity-conflict",
        _ => "unknown"
    };

    private static bool IsSupportedTarget(ArrCompletedDownloadTarget? target) => target is
    {
        App: "sonarr",
        ClientKind: ArrCompletedDownloadClientKind.Sonarr
    } or
    {
        App: "radarr",
        ClientKind: ArrCompletedDownloadClientKind.Radarr
    };

    private static bool QueueTypeMatchesTarget<TQueueRecord>(ArrCompletedDownloadClientKind clientKind)
        where TQueueRecord : ArrQueueRecord => clientKind switch
    {
        ArrCompletedDownloadClientKind.Sonarr => typeof(TQueueRecord) == typeof(SonarrQueueRecord),
        ArrCompletedDownloadClientKind.Radarr => typeof(TQueueRecord) == typeof(RadarrQueueRecord),
        _ => false
    };

    private static bool HasCorrelationMediaIdentityConflict(
        ArrCompletedDownloadClientKind clientKind,
        IEnumerable<ArrCompletedDownloadCorrelationFact> correlations) => clientKind switch
    {
        ArrCompletedDownloadClientKind.Sonarr =>
            HasMultiplePositiveValues(correlations.Select(fact => fact.SeriesId))
            || HasMultiplePositiveValues(correlations.Select(fact => fact.EpisodeId)),
        ArrCompletedDownloadClientKind.Radarr =>
            HasMultiplePositiveValues(correlations.Select(fact => fact.MovieId)),
        _ => true
    };

    private static bool HasInvalidCorrelationMediaIdentity(
        ArrCompletedDownloadClientKind clientKind,
        IEnumerable<ArrCompletedDownloadCorrelationFact> correlations) => clientKind switch
    {
        ArrCompletedDownloadClientKind.Sonarr => correlations.Any(
            fact => fact.SeriesId is < 0 || fact.EpisodeId is < 0),
        ArrCompletedDownloadClientKind.Radarr => correlations.Any(fact => fact.MovieId is < 0),
        _ => true
    };

    private static bool HasInvalidQueueMediaIdentity(
        ArrCompletedDownloadClientKind clientKind,
        ArrQueueRecord record) => (clientKind, record) switch
    {
        (ArrCompletedDownloadClientKind.Sonarr, SonarrQueueRecord sonarr) =>
            sonarr.SeriesId is < 0 || sonarr.EpisodeId is < 0,
        (ArrCompletedDownloadClientKind.Radarr, RadarrQueueRecord radarr) => radarr.MovieId is < 0,
        _ => true
    };

    private static bool HasQueueMediaIdentityConflict(
        ArrCompletedDownloadClientKind clientKind,
        IEnumerable<ArrCompletedDownloadCorrelationFact> correlations,
        ArrQueueRecord record) => (clientKind, record) switch
    {
        (ArrCompletedDownloadClientKind.Sonarr, SonarrQueueRecord sonarr) =>
            Conflicts(OnlyPositiveValue(correlations.Select(fact => fact.SeriesId)), sonarr.SeriesId)
            || Conflicts(OnlyPositiveValue(correlations.Select(fact => fact.EpisodeId)), sonarr.EpisodeId),
        (ArrCompletedDownloadClientKind.Radarr, RadarrQueueRecord radarr) =>
            Conflicts(OnlyPositiveValue(correlations.Select(fact => fact.MovieId)), radarr.MovieId),
        _ => true
    };

    private static bool HasMultiplePositiveValues(IEnumerable<int?> values) =>
        values.Where(value => value is > 0).Select(value => value!.Value).Distinct().Take(2).Count() > 1;

    private static int? OnlyPositiveValue(IEnumerable<int?> values) =>
        values.FirstOrDefault(value => value is > 0);

    private static bool Conflicts(int? correlationValue, int? queueValue) =>
        correlationValue is > 0 && queueValue > 0 && correlationValue.Value != queueValue;

    private static ArrCompletedDownloadDispatchDecision Fallback(
        ArrCompletedDownloadFallbackReason reason) =>
        ArrCompletedDownloadDispatchDecision.Fallback(reason);

    private static ArrCompletedDownloadDispatchPreparation PreparationFallback(
        ArrCompletedDownloadFallbackReason reason) =>
        new(null, null, null, reason);
}

using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Arr;

public sealed class ArrValidationResponse
{
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("instance_count")]
    public int InstanceCount { get; init; }

    [JsonPropertyName("queue_items")]
    public int QueueItems { get; init; }

    [JsonPropertyName("queue_items_total")]
    public int QueueItemsTotal { get; init; }

    [JsonPropertyName("ignored_queue_items")]
    public int IgnoredQueueItems { get; init; }

    [JsonPropertyName("history_items")]
    public int HistoryItems { get; init; }

    [JsonPropertyName("correlations")]
    public int Correlations { get; init; }

    [JsonPropertyName("stale_correlations")]
    public int StaleCorrelations { get; init; }

    [JsonPropertyName("correlation_coverage_percent")]
    public int CorrelationCoveragePercent { get; init; }

    [JsonPropertyName("active_priority_hints")]
    public int ActivePriorityHints { get; init; }

    [JsonPropertyName("duplicates")]
    public int Duplicates { get; init; }

    [JsonPropertyName("search_nudges")]
    public ArrSearchNudgeSummaryDto SearchNudges { get; init; } = new();

    [JsonPropertyName("lifecycle_states")]
    public IReadOnlyList<ArrLifecycleStateDto> LifecycleStates { get; init; } = [];

    [JsonPropertyName("issues")]
    public IReadOnlyList<ArrValidationIssueDto> Issues { get; init; } = [];
}

public sealed class ArrSearchNudgeSummaryDto
{
    [JsonPropertyName("planned")]
    public int Planned { get; init; }

    [JsonPropertyName("executed")]
    public int Executed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("last_command_at")]
    public DateTimeOffset? LastCommandAt { get; init; }
}

public sealed class ArrLifecycleStateDto
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed class ArrValidationIssueDto
{
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed class ArrSearchNudgeCommandsResponse
{
    [JsonPropertyName("commands")]
    public IReadOnlyList<ArrSearchNudgeCommandDto> Commands { get; init; } = [];
}

public sealed class ArrSearchNudgeCommandDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("arr_app")]
    public required string ArrApp { get; init; }

    [JsonPropertyName("instance_key")]
    public required string InstanceKey { get; init; }

    [JsonPropertyName("instance_host")]
    public required string InstanceHost { get; init; }

    [JsonPropertyName("command_name")]
    public required string CommandName { get; init; }

    [JsonPropertyName("command_id")]
    public int? CommandId { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<int> Targets { get; init; } = [];

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("score")]
    public int Score { get; init; }

    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("next_allowed_at")]
    public DateTimeOffset NextAllowedAt { get; init; }
}

public sealed class ArrCorrelationsResponse
{
    [JsonPropertyName("correlations")]
    public IReadOnlyList<ArrDownloadCorrelationDto> Correlations { get; init; } = [];
}

public sealed class ArrDownloadCorrelationDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("queue_item_id")]
    public string? QueueItemId { get; init; }

    [JsonPropertyName("history_item_id")]
    public string? HistoryItemId { get; init; }

    [JsonPropertyName("arr_app")]
    public required string ArrApp { get; init; }

    [JsonPropertyName("instance_key")]
    public required string InstanceKey { get; init; }

    [JsonPropertyName("instance_host")]
    public required string InstanceHost { get; init; }

    [JsonPropertyName("download_id")]
    public string? DownloadId { get; init; }

    [JsonPropertyName("media_key")]
    public string? MediaKey { get; init; }

    [JsonPropertyName("movie_id")]
    public int? MovieId { get; init; }

    [JsonPropertyName("series_id")]
    public int? SeriesId { get; init; }

    [JsonPropertyName("episode_id")]
    public int? EpisodeId { get; init; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; init; }

    [JsonPropertyName("artist_id")]
    public int? ArtistId { get; init; }

    [JsonPropertyName("album_id")]
    public int? AlbumId { get; init; }

    [JsonPropertyName("release_title")]
    public string? ReleaseTitle { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("quality")]
    public string? Quality { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("manual_lock")]
    public bool ManualLock { get; init; }

    [JsonPropertyName("is_upgrade")]
    public bool IsUpgrade { get; init; }

    [JsonPropertyName("is_duplicate")]
    public bool IsDuplicate { get; init; }

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset LastSeenAt { get; init; }
}

public sealed class ArrManualCorrelationRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("queue_item_id")]
    public string? QueueItemId { get; init; }

    [JsonPropertyName("history_item_id")]
    public string? HistoryItemId { get; init; }

    [JsonPropertyName("nzo_id")]
    public string? NzoId { get; init; }

    [JsonPropertyName("arr_app")]
    public string? ArrApp { get; init; }

    [JsonPropertyName("instance_key")]
    public string? InstanceKey { get; init; }

    [JsonPropertyName("instance_host")]
    public string? InstanceHost { get; init; }

    [JsonPropertyName("download_id")]
    public string? DownloadId { get; init; }

    [JsonPropertyName("movie_id")]
    public int? MovieId { get; init; }

    [JsonPropertyName("series_id")]
    public int? SeriesId { get; init; }

    [JsonPropertyName("episode_id")]
    public int? EpisodeId { get; init; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; init; }

    [JsonPropertyName("artist_id")]
    public int? ArtistId { get; init; }

    [JsonPropertyName("album_id")]
    public int? AlbumId { get; init; }

    [JsonPropertyName("release_title")]
    public string? ReleaseTitle { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("quality")]
    public string? Quality { get; init; }

    [JsonPropertyName("manual_lock")]
    public bool? ManualLock { get; init; }

    [JsonPropertyName("is_upgrade")]
    public bool? IsUpgrade { get; init; }

    [JsonPropertyName("is_duplicate")]
    public bool? IsDuplicate { get; init; }
}

public sealed class ArrCorrelationEnvelope
{
    [JsonPropertyName("correlation")]
    public required ArrDownloadCorrelationDto Correlation { get; init; }
}

public sealed class ArrEventResponse
{
    [JsonPropertyName("status")]
    public bool Status { get; init; } = true;

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("correlation")]
    public ArrDownloadCorrelationDto? Correlation { get; init; }
}

public static class ArrDtoMapper
{
    public static ArrDownloadCorrelationDto FromCorrelation(ArrDownloadCorrelation correlation) => new()
    {
        Id = correlation.Id.ToString(),
        QueueItemId = correlation.QueueItemId?.ToString(),
        HistoryItemId = correlation.HistoryItemId?.ToString(),
        ArrApp = correlation.ArrApp,
        InstanceKey = correlation.InstanceKey,
        InstanceHost = correlation.InstanceHost,
        DownloadId = correlation.DownloadId,
        MediaKey = correlation.MediaKey,
        MovieId = correlation.MovieId,
        SeriesId = correlation.SeriesId,
        EpisodeId = correlation.EpisodeId,
        SeasonNumber = correlation.SeasonNumber,
        ArtistId = correlation.ArtistId,
        AlbumId = correlation.AlbumId,
        ReleaseTitle = correlation.ReleaseTitle,
        Category = correlation.Category,
        Quality = correlation.Quality,
        Status = correlation.Status,
        Source = correlation.Source,
        ManualLock = correlation.ManualLock,
        IsUpgrade = correlation.IsUpgrade,
        IsDuplicate = correlation.IsDuplicate,
        LastSeenAt = correlation.LastSeenAt
    };
}

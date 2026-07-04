using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.Arr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

public sealed class ArrOperationsService(ConfigManager configManager)
{
    public async Task<ArrValidationResponse> BuildValidationAsync
    (
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        var now = DateTimeOffset.UtcNow;
        var arrConfig = configManager.GetArrConfig();
        var prioritization = configManager.GetArrPrioritizationOptions();
        var searchNudge = configManager.GetArrSearchNudgeOptions();
        var stats = await dbClient.GetArrIntegrationStatsAsync(now, ct).ConfigureAwait(false);
        var queueItems = await dbClient.Ctx.QueueItems.CountAsync(ct).ConfigureAwait(false);
        var historyItems = await dbClient.Ctx.HistoryItems.CountAsync(ct).ConfigureAwait(false);
        var correlatedActiveQueueItems = await dbClient.Ctx.ArrDownloadCorrelations
            .AsNoTracking()
            .Where(x => x.QueueItemId != null)
            .Select(x => x.QueueItemId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);

        var issues = new List<ArrValidationIssueDto>();
        if (arrConfig.GetInstanceCount() == 0)
            issues.Add(Issue("error", "arr_instances_missing", "No ARR instances are configured."));
        if (queueItems > 0 && correlatedActiveQueueItems == 0)
            issues.Add(Issue("warning", "queue_uncorrelated", "Queue has active items but no ARR correlations."));
        if (queueItems > 0 && correlatedActiveQueueItems < queueItems)
            issues.Add(Issue("warning", "queue_partial_correlation", "Some active queue items are not correlated to ARR media."));
        if (stats.StaleCorrelations > 0)
            issues.Add(Issue("warning", "stale_correlations", "ARR correlations have not been refreshed recently."));
        if (stats.DuplicateCorrelations > 0)
            issues.Add(Issue("warning", "duplicate_requests", "ARR duplicate download requests were detected."));
        if (stats.FailedSearchNudges > 0)
            issues.Add(Issue("error", "search_nudge_failures", "One or more ARR search nudge commands failed."));
        if (prioritization.Enabled && prioritization.Mode == "apply" && stats.TotalCorrelations == 0)
            issues.Add(Issue("error", "priority_apply_without_correlation", "Priority apply mode is enabled before correlations are visible."));
        if (searchNudge.Enabled && searchNudge.Mode == "apply" && stats.ExecutedSearchNudges == 0)
            issues.Add(Issue("warning", "search_apply_unproven", "Search apply mode is enabled but no successful commands are recorded yet."));

        return new ArrValidationResponse
        {
            GeneratedAt = now,
            InstanceCount = arrConfig.GetInstanceCount(),
            QueueItems = queueItems,
            HistoryItems = historyItems,
            Correlations = stats.TotalCorrelations,
            StaleCorrelations = stats.StaleCorrelations,
            CorrelationCoveragePercent = queueItems == 0
                ? 100
                : (int)Math.Round((double)correlatedActiveQueueItems / queueItems * 100),
            ActivePriorityHints = stats.ActivePriorityHints,
            Duplicates = stats.DuplicateCorrelations,
            SearchNudges = new ArrSearchNudgeSummaryDto
            {
                Planned = stats.PlannedSearchNudges,
                Executed = stats.ExecutedSearchNudges,
                Failed = stats.FailedSearchNudges,
                LastCommandAt = stats.LastSearchNudgeAt
            },
            LifecycleStates = stats.LifecycleStates
                .Select(x => new ArrLifecycleStateDto { State = x.State, Count = x.Count })
                .ToList(),
            Issues = issues
        };
    }

    public async Task<List<ArrSearchNudgeCommandDto>> GetSearchNudgeCommandsAsync
    (
        DavDatabaseContext dbContext,
        int limit,
        string? status,
        string? arrApp,
        string? mode,
        string? commandName,
        string? search,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = dbContext.ArrSearchNudgeCommands.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(arrApp))
            query = query.Where(x => x.ArrApp == arrApp.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(mode))
            query = query.Where(x => x.Mode == NormalizeMode(mode));
        if (!string.IsNullOrWhiteSpace(commandName))
            query = query.Where(x => x.CommandName == commandName.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.InstanceKey.Contains(term)
                || x.InstanceHost.Contains(term)
                || x.CommandName.Contains(term)
                || x.TargetsJson.Contains(term)
                || x.ReasonsJson.Contains(term)
                || (x.Error != null && x.Error.Contains(term)));
        }

        var commands = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return commands.Select(ToCommandDto).ToList();
    }

    public async Task<ArrSearchNudgeCommandDto> RetrySearchNudgeCommandAsync
    (
        DavDatabaseContext dbContext,
        Guid id,
        CancellationToken ct = default
    )
    {
        var command = await dbContext.ArrSearchNudgeCommands
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false)
            ?? throw new BadHttpRequestException("ARR search nudge command was not found.");
        command.Status = command.Mode == "apply" ? "pending_apply" : "planned";
        command.Error = null;
        command.CommandId = null;
        command.CompletedAt = null;
        command.NextAllowedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToCommandDto(command);
    }

    public async Task<int> ClearSearchNudgeCommandsAsync
    (
        DavDatabaseContext dbContext,
        string? status,
        CancellationToken ct = default
    )
    {
        var query = dbContext.ArrSearchNudgeCommands.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);
        return await query.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<ArrDownloadCorrelationDto>> GetCorrelationsAsync
    (
        DavDatabaseContext dbContext,
        int limit,
        string? search,
        string? arrApp,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = dbContext.ArrDownloadCorrelations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(arrApp))
            query = query.Where(x => x.ArrApp == arrApp.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                (x.ReleaseTitle != null && x.ReleaseTitle.Contains(term))
                || (x.DownloadId != null && x.DownloadId.Contains(term))
                || (x.MediaKey != null && x.MediaKey.Contains(term))
                || (x.Category != null && x.Category.Contains(term)));
        }

        var correlations = await query
            .OrderByDescending(x => x.LastSeenAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return correlations.Select(ArrDtoMapper.FromCorrelation).ToList();
    }

    public async Task<ArrDownloadCorrelationDto> UpsertManualCorrelationAsync
    (
        DavDatabaseContext dbContext,
        ArrManualCorrelationRequest request,
        CancellationToken ct = default
    )
    {
        var now = DateTimeOffset.UtcNow;
        var id = ParseOptionalGuid(request.Id, "id");
        var correlation = id == null
            ? null
            : await dbContext.ArrDownloadCorrelations.FirstOrDefaultAsync(x => x.Id == id.Value, ct)
                .ConfigureAwait(false);

        if (id != null && correlation == null)
            throw new BadHttpRequestException("ARR correlation was not found.");

        if (correlation == null)
        {
            correlation = new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                CreatedAt = now
            };
            dbContext.ArrDownloadCorrelations.Add(correlation);
        }

        ApplyManualCorrelation(correlation, request, now);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return ArrDtoMapper.FromCorrelation(correlation);
    }

    public async Task DeleteCorrelationAsync
    (
        DavDatabaseContext dbContext,
        Guid id,
        CancellationToken ct = default
    )
    {
        var deleted = await dbContext.ArrDownloadCorrelations
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        if (deleted == 0)
            throw new BadHttpRequestException("ARR correlation was not found.");
    }

    public async Task<ArrEventResponse> IngestCustomScriptEventAsync
    (
        DavDatabaseContext dbContext,
        string app,
        IReadOnlyDictionary<string, string> payload,
        CancellationToken ct = default
    )
    {
        app = NormalizeApp(app);
        var now = DateTimeOffset.UtcNow;
        var instanceHost = Get(payload, "instance_host", "instancehost", $"{app}_host", $"{app}_url") ?? "";
        var instanceKey = Get(payload, "instance_key", "instancekey")
                          ?? (!string.IsNullOrWhiteSpace(instanceHost)
                              ? ArrIntegration.GetInstanceKey(app, instanceHost)
                              : $"{app}:custom-script");
        var eventType = Get(payload, "event_type", "eventtype", "event", $"{app}_eventtype")
                        ?? "custom-script";
        if (eventType.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return new ArrEventResponse
            {
                EventType = eventType,
                Correlation = null
            };
        }

        var downloadId = Get(payload, "download_id", "downloadid", "nzo_id", "nzoid", $"{app}_download_id");
        var nzoId = Get(payload, "nzo_id", "nzoid", "download_id", "downloadid");
        var queueItemId = ParseOptionalGuid(Get(payload, "queue_item_id", "queueitemid"), "queue_item_id");
        var historyItemId = ParseOptionalGuid(Get(payload, "history_item_id", "historyitemid"), "history_item_id");
        if (queueItemId == null && historyItemId == null && Guid.TryParse(nzoId, out var parsedNzoId))
        {
            if (await dbContext.QueueItems.AnyAsync(x => x.Id == parsedNzoId, ct).ConfigureAwait(false))
                queueItemId = parsedNzoId;
            else if (await dbContext.HistoryItems.AnyAsync(x => x.Id == parsedNzoId, ct).ConfigureAwait(false))
                historyItemId = parsedNzoId;
        }

        var media = ExtractMediaIdentity(app, payload);
        var mediaKey = BuildMediaKey(app, media.MovieId, media.SeriesId, media.EpisodeId, media.SeasonNumber, media.ArtistId, media.AlbumId);
        var correlation = await FindCorrelationAsync(dbContext, app, instanceKey, downloadId, mediaKey, queueItemId, historyItemId, ct)
            .ConfigureAwait(false);
        if (correlation == null)
        {
            correlation = new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                ArrApp = app,
                InstanceKey = instanceKey,
                InstanceHost = instanceHost,
                CreatedAt = now
            };
            dbContext.ArrDownloadCorrelations.Add(correlation);
        }

        correlation.ArrApp = app;
        correlation.InstanceKey = instanceKey;
        correlation.InstanceHost = instanceHost;
        correlation.QueueItemId = queueItemId;
        correlation.HistoryItemId = historyItemId;
        ApplyCorrelationIdentity(
            correlation,
            source: "custom-script",
            downloadId,
            mediaKey,
            media.MovieId,
            media.SeriesId,
            media.EpisodeId,
            media.SeasonNumber,
            media.ArtistId,
            media.AlbumId);
        correlation.ReleaseTitle = Get(payload, "release_title", "releasetitle", "source_title", "sourcetitle", $"{app}_release_title", $"{app}_source_title");
        correlation.Category = Get(payload, "category", "cat", $"{app}_category");
        correlation.Quality = Get(payload, "quality", "quality_name", $"{app}_quality", $"{app}_qualityversion");
        correlation.Status = eventType;
        correlation.IsUpgrade = ParseBool(Get(payload, "is_upgrade", "isupgrade", "upgrade"));
        correlation.UpdatedAt = now;
        correlation.LastSeenAt = now;

        dbContext.ArrDownloadLifecycleEvents.Add(new ArrDownloadLifecycleEvent
        {
            Id = Guid.NewGuid(),
            QueueItemId = queueItemId,
            HistoryItemId = historyItemId,
            ArrApp = app,
            InstanceKey = instanceKey,
            DownloadId = downloadId,
            MediaKey = mediaKey,
            State = MapEventState(eventType),
            StateReason = eventType,
            CreatedAt = now
        });

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ArrEventResponse
        {
            EventType = eventType,
            Correlation = ArrDtoMapper.FromCorrelation(correlation)
        };
    }

    public async Task<bool> HasRejectableDuplicateAsync
    (
        DavDatabaseContext dbContext,
        string fileName,
        string jobName,
        string category,
        CancellationToken ct = default
    )
    {
        var normalizedJob = ArrIntegration.NormalizeTitle(jobName);
        var activeCandidates = await dbContext.QueueItems
            .AsNoTracking()
            .Where(x => x.Category == category)
            .Select(x => new { x.FileName, x.JobName })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var activeDuplicate = activeCandidates.Any(x =>
            x.FileName == fileName
            || x.JobName == jobName
            || ArrIntegration.NormalizeTitle(x.JobName) == normalizedJob);
        if (activeDuplicate) return true;

        var recentCutoff = DateTime.UtcNow.AddHours(-24);
        var recentCandidates = await dbContext.HistoryItems
            .AsNoTracking()
            .Where(x => x.Category == category)
            .Where(x => x.CreatedAt >= recentCutoff)
            .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .Select(x => new { x.FileName, x.JobName })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return recentCandidates.Any(x =>
            x.FileName == fileName
            || x.JobName == jobName
            || ArrIntegration.NormalizeTitle(x.JobName) == normalizedJob);
    }

    public static string NormalizeDuplicateBehavior(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value is "increment" or "mark-failed" or "reject" ? value : "increment";
    }

    private static void ApplyManualCorrelation
    (
        ArrDownloadCorrelation correlation,
        ArrManualCorrelationRequest request,
        DateTimeOffset now
    )
    {
        var app = NormalizeApp(request.ArrApp ?? correlation.ArrApp);
        var instanceHost = request.InstanceHost ?? correlation.InstanceHost;
        correlation.ArrApp = app;
        correlation.InstanceHost = instanceHost ?? "";
        correlation.InstanceKey = request.InstanceKey
                                  ?? (!string.IsNullOrWhiteSpace(instanceHost)
                                      ? ArrIntegration.GetInstanceKey(app, instanceHost)
                                      : correlation.InstanceKey.ToNullIfEmpty() ?? $"{app}:manual");
        correlation.QueueItemId = ParseOptionalGuid(request.QueueItemId ?? request.NzoId, "queue_item_id")
                                  ?? correlation.QueueItemId;
        correlation.HistoryItemId = ParseOptionalGuid(request.HistoryItemId, "history_item_id")
                                    ?? correlation.HistoryItemId;
        correlation.DownloadId = request.DownloadId ?? correlation.DownloadId;
        correlation.MovieId = request.MovieId ?? correlation.MovieId;
        correlation.SeriesId = request.SeriesId ?? correlation.SeriesId;
        correlation.EpisodeId = request.EpisodeId ?? correlation.EpisodeId;
        correlation.SeasonNumber = request.SeasonNumber ?? correlation.SeasonNumber;
        correlation.ArtistId = request.ArtistId ?? correlation.ArtistId;
        correlation.AlbumId = request.AlbumId ?? correlation.AlbumId;
        correlation.MediaKey = BuildMediaKey(
            app,
            correlation.MovieId,
            correlation.SeriesId,
            correlation.EpisodeId,
            correlation.SeasonNumber,
            correlation.ArtistId,
            correlation.AlbumId);
        correlation.ReleaseTitle = request.ReleaseTitle ?? correlation.ReleaseTitle;
        correlation.Category = request.Category ?? correlation.Category;
        correlation.Quality = request.Quality ?? correlation.Quality;
        correlation.IsUpgrade = request.IsUpgrade ?? correlation.IsUpgrade;
        correlation.IsDuplicate = request.IsDuplicate ?? correlation.IsDuplicate;
        correlation.Source = "manual";
        correlation.ManualLock = request.ManualLock ?? true;
        correlation.Status = "manual";
        correlation.UpdatedAt = now;
        correlation.LastSeenAt = now;
    }

    public static string NormalizeMode(string? mode)
    {
        mode = mode?.Trim().ToLowerInvariant();
        return mode is "apply" ? "apply" : "report";
    }

    private static ArrSearchNudgeCommandDto ToCommandDto(ArrSearchNudgeCommand command)
    {
        return new ArrSearchNudgeCommandDto
        {
            Id = command.Id.ToString(),
            ArrApp = command.ArrApp,
            InstanceKey = command.InstanceKey,
            InstanceHost = command.InstanceHost,
            CommandName = command.CommandName,
            CommandId = command.CommandId,
            Targets = DeserializeList<int>(command.TargetsJson),
            Mode = command.Mode,
            Status = command.Status,
            Score = command.Score,
            Reasons = DeserializeList<string>(command.ReasonsJson),
            Error = command.Error,
            CreatedAt = command.CreatedAt,
            CompletedAt = command.CompletedAt,
            NextAllowedAt = command.NextAllowedAt
        };
    }

    private static async Task<ArrDownloadCorrelation?> FindCorrelationAsync
    (
        DavDatabaseContext dbContext,
        string app,
        string instanceKey,
        string? downloadId,
        string? mediaKey,
        Guid? queueItemId,
        Guid? historyItemId,
        CancellationToken ct
    )
    {
        if (queueItemId != null)
        {
            var byQueue = await dbContext.ArrDownloadCorrelations
                .FirstOrDefaultAsync(x => x.QueueItemId == queueItemId, ct)
                .ConfigureAwait(false);
            if (byQueue != null) return byQueue;
        }

        if (historyItemId != null)
        {
            var byHistory = await dbContext.ArrDownloadCorrelations
                .FirstOrDefaultAsync(x => x.HistoryItemId == historyItemId, ct)
                .ConfigureAwait(false);
            if (byHistory != null) return byHistory;
        }

        if (!string.IsNullOrWhiteSpace(downloadId))
        {
            var byDownload = await dbContext.ArrDownloadCorrelations
                .FirstOrDefaultAsync(x => x.ArrApp == app && x.InstanceKey == instanceKey && x.DownloadId == downloadId, ct)
                .ConfigureAwait(false);
            if (byDownload != null) return byDownload;
        }

        if (!string.IsNullOrWhiteSpace(mediaKey))
            return await dbContext.ArrDownloadCorrelations
                .FirstOrDefaultAsync(x => x.ArrApp == app && x.InstanceKey == instanceKey && x.MediaKey == mediaKey, ct)
                .ConfigureAwait(false);

        return null;
    }

    private static List<T> DeserializeList<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ArrValidationIssueDto Issue(string severity, string code, string message) => new()
    {
        Severity = severity,
        Code = code,
        Message = message
    };

    private static string NormalizeApp(string app)
    {
        app = app.Trim().ToLowerInvariant();
        return app is "radarr" or "sonarr" or "lidarr"
            ? app
            : throw new BadHttpRequestException("ARR app must be radarr, sonarr, or lidarr.");
    }

    private static Guid? ParseOptionalGuid(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out var id)
            ? id
            : throw new BadHttpRequestException($"{field} must be a valid GUID.");
    }

    private static ArrMediaIdentity ExtractMediaIdentity(string app, IReadOnlyDictionary<string, string> payload)
    {
        return new ArrMediaIdentity(
            MovieId: GetInt(payload, "movie_id", "movieid", $"{app}_movie_id"),
            SeriesId: GetInt(payload, "series_id", "seriesid", $"{app}_series_id"),
            EpisodeId: GetInt(payload, "episode_id", "episodeid", $"{app}_episode_id"),
            SeasonNumber: GetInt(payload, "season_number", "seasonnumber", $"{app}_seasonnumber"),
            ArtistId: GetInt(payload, "artist_id", "artistid", $"{app}_artist_id"),
            AlbumId: GetInt(payload, "album_id", "albumid", $"{app}_album_id"));
    }

    private static string? BuildMediaKey
    (
        string app,
        int? movieId,
        int? seriesId,
        int? episodeId,
        int? seasonNumber,
        int? artistId,
        int? albumId
    )
    {
        return app switch
        {
            "sonarr" when episodeId is > 0 => $"{app}:episode:{episodeId}",
            "sonarr" when seriesId is > 0 && seasonNumber is not null => $"{app}:series:{seriesId}:season:{seasonNumber}",
            "sonarr" when seriesId is > 0 => $"{app}:series:{seriesId}",
            "radarr" when movieId is > 0 => $"{app}:movie:{movieId}",
            "lidarr" when albumId is > 0 => $"{app}:album:{albumId}",
            "lidarr" when artistId is > 0 => $"{app}:artist:{artistId}",
            _ => null
        };
    }

    private static void ApplyCorrelationIdentity
    (
        ArrDownloadCorrelation correlation,
        string source,
        string? downloadId,
        string? mediaKey,
        int? movieId,
        int? seriesId,
        int? episodeId,
        int? seasonNumber,
        int? artistId,
        int? albumId
    )
    {
        if (correlation.ManualLock) return;

        correlation.Source = source;
        correlation.DownloadId = downloadId;
        correlation.MediaKey = mediaKey;
        correlation.MovieId = movieId;
        correlation.SeriesId = seriesId;
        correlation.EpisodeId = episodeId;
        correlation.SeasonNumber = seasonNumber;
        correlation.ArtistId = artistId;
        correlation.AlbumId = albumId;
    }

    private static string MapEventState(string eventType)
    {
        var normalized = eventType.Trim().ToLowerInvariant();
        if (normalized.Contains("grab")) return "Grabbed";
        if (normalized.Contains("import") || normalized.Contains("download")) return "Imported";
        if (normalized.Contains("rename")) return "Renamed";
        if (normalized.Contains("delete")) return "Deleted";
        if (normalized.Contains("health")) return "Health";
        if (normalized.Contains("manual")) return "ManualInteraction";
        return "Event";
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key.ToLowerInvariant(), out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        var value = Get(values, keys);
        if (value == null) return null;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ArrMediaIdentity
    (
        int? MovieId,
        int? SeriesId,
        int? EpisodeId,
        int? SeasonNumber,
        int? ArtistId,
        int? AlbumId
    );
}

file static class StringExtensions
{
    public static string? ToNullIfEmpty(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

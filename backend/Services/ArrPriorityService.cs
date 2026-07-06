using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ArrPriorityService(ConfigManager configManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = configManager.GetArrPrioritizationOptions();
            try
            {
                if (options.Enabled)
                    await RecomputeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(ex, stoppingToken))
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning("ARR priority recompute failed: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.RecomputeIntervalSeconds), stoppingToken)
                .ConfigureAwait(false);
        }
    }

    public async Task RecomputeAsync(CancellationToken ct = default)
    {
        var arrConfig = configManager.GetArrConfig();
        var options = configManager.GetArrPrioritizationOptions();
        if (arrConfig.GetInstanceCount() == 0) return;

        var instances = ArrIntegration.GetInstances(arrConfig);
        var metadata = await LoadMetadataAsync(instances, ct).ConfigureAwait(false);
        await using var dbContext = new DavDatabaseContext();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(options.RecomputeIntervalSeconds * 2);
        var queueItems = await dbContext.QueueItems
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queueIds = queueItems.Select(x => x.Id).ToHashSet();
        var correlations = await dbContext.ArrDownloadCorrelations
            .AsNoTracking()
            .Where(x => x.QueueItemId != null && queueIds.Contains(x.QueueItemId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var correlationsByQueue = correlations
            .GroupBy(x => x.QueueItemId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(c => c.LastSeenAt).First());
        var existingHints = await dbContext.QueuePriorityHints
            .Where(x => queueIds.Contains(x.QueueItemId))
            .ToDictionaryAsync(x => x.QueueItemId, ct)
            .ConfigureAwait(false);

        foreach (var queueItem in queueItems)
        {
            correlationsByQueue.TryGetValue(queueItem.Id, out var correlation);
            var decision = Score(queueItem, correlation, metadata);
            var hint = existingHints.GetValueOrDefault(queueItem.Id);
            if (hint is null)
            {
                hint = new QueuePriorityHint { QueueItemId = queueItem.Id };
                dbContext.QueuePriorityHints.Add(hint);
            }

            hint.Score = decision.Score;
            hint.EffectivePriority = ArrIntegration.ClampAutomaticPriority(
                decision.Priority,
                queueItem.Priority,
                options.MaxAutomaticPriority);
            hint.ApplyToScheduling = options.Mode == "apply";
            hint.ReasonsJson = JsonSerializer.Serialize(decision.Reasons);
            hint.Source = options.Mode == "apply" ? "arr-apply" : "arr-report";
            hint.ComputedAt = now;
            hint.ExpiresAt = expiresAt;
            hint.StaleReason = decision.StaleReason;
        }

        await dbContext.QueuePriorityHints
            .Where(x => !queueIds.Contains(x.QueueItemId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static ArrPriorityDecision Score
    (
        QueueItem queueItem,
        ArrDownloadCorrelation? correlation,
        ArrPriorityMetadata metadata
    )
    {
        var reasons = new List<string>();
        var score = 0;

        if (correlation is null)
        {
            return new ArrPriorityDecision(
                Score: 0,
                Priority: QueueItem.PriorityOption.Normal,
                Reasons: ["uncorrelated"],
                StaleReason: "No matching ARR queue record has been observed.");
        }

        score += 120;
        reasons.Add("arr-correlated");

        if (correlation.IsDuplicate)
        {
            score = Math.Max(0, score - 200);
            reasons.Add("duplicate-request");
        }

        if (correlation.ArrApp == "sonarr" && correlation.EpisodeId is { } episodeId)
            ApplySonarrScore(correlation, episodeId, metadata, reasons, ref score);
        else if (correlation.ArrApp == "radarr" && correlation.MovieId is { } movieId)
            ApplyRadarrScore(correlation, movieId, metadata, reasons, ref score);
        else if (correlation.ArrApp == "lidarr")
        {
            score += 50;
            reasons.Add("lidarr-report-only");
        }

        if (correlation.IsUpgrade && metadata.HasMissingMedia)
        {
            score = Math.Max(0, score - 100);
            reasons.Add("upgrade-behind-missing-media");
        }

        var priority = score >= 250
            ? QueueItem.PriorityOption.High
            : QueueItem.PriorityOption.Normal;
        return new ArrPriorityDecision(
            Score: Math.Clamp(score, 0, 1000),
            Priority: priority,
            Reasons: reasons,
            StaleReason: null);
    }

    private static void ApplySonarrScore
    (
        ArrDownloadCorrelation correlation,
        int episodeId,
        ArrPriorityMetadata metadata,
        List<string> reasons,
        ref int score
    )
    {
        var episode = metadata.SonarrMissingEpisodes.GetValueOrDefault((correlation.InstanceKey, episodeId));
        if (episode is null) return;

        var remainingSeries = metadata.SonarrMissingBySeries.GetValueOrDefault((correlation.InstanceKey, episode.SeriesId));
        var remainingSeason = metadata.SonarrMissingBySeason.GetValueOrDefault((
            correlation.InstanceKey,
            episode.SeriesId,
            episode.SeasonNumber));

        var afterQueuedSeries = Math.Max(0, remainingSeries - 1);
        if (afterQueuedSeries == 0)
        {
            score += 300;
            reasons.Add("series-completion");
        }
        else if (afterQueuedSeries <= 3)
        {
            score += 150;
            reasons.Add("series-nearly-complete");
        }
        else if (afterQueuedSeries <= 10)
        {
            score += 100;
            reasons.Add("series-low-missing-count");
        }
        else if (afterQueuedSeries <= 25)
        {
            score += 50;
            reasons.Add("series-moderate-missing-count");
        }

        if (Math.Max(0, remainingSeason - 1) == 0)
        {
            score += 220;
            reasons.Add("season-completion");
        }

        if (episode.AirDateUtc is { } airDate)
        {
            var age = DateTimeOffset.UtcNow - airDate;
            if (age >= TimeSpan.Zero && age <= TimeSpan.FromDays(14))
            {
                score += 250;
                reasons.Add("recently-aired");
            }
            else if (age < TimeSpan.Zero && age >= -TimeSpan.FromDays(1))
            {
                score += 200;
                reasons.Add("airing-soon");
            }
        }
    }

    private static void ApplyRadarrScore
    (
        ArrDownloadCorrelation correlation,
        int movieId,
        ArrPriorityMetadata metadata,
        List<string> reasons,
        ref int score
    )
    {
        var movie = metadata.RadarrMissingMovies.GetValueOrDefault((correlation.InstanceKey, movieId));
        if (movie is null) return;

        if (movie.Collection?.Id is > 0)
        {
            var remainingCollection = metadata.RadarrMissingByCollection.GetValueOrDefault((
                correlation.InstanceKey,
                movie.Collection.Id));
            var afterQueued = Math.Max(0, remainingCollection - 1);
            if (afterQueued == 0)
            {
                score += 300;
                reasons.Add("collection-completion");
            }
            else if (afterQueued <= 2)
            {
                score += 180;
                reasons.Add("collection-nearly-complete");
            }
            else if (afterQueued <= 5)
            {
                score += 100;
                reasons.Add("collection-low-missing-count");
            }
        }

        var releaseDate = movie.PhysicalRelease ?? movie.DigitalRelease ?? movie.InCinemas;
        if (releaseDate is { } date && DateTimeOffset.UtcNow - date <= TimeSpan.FromDays(30))
        {
            score += 180;
            reasons.Add("recent-movie-release");
        }
    }

    private static async Task<ArrPriorityMetadata> LoadMetadataAsync
    (
        IReadOnlyList<ArrInstance> instances,
        CancellationToken ct
    )
    {
        var metadata = new ArrPriorityMetadata();
        foreach (var instance in instances)
        {
            try
            {
                switch (instance.Client)
                {
                    case SonarrClient sonarr:
                    {
                        var missing = await sonarr.GetMissingEpisodesAsync(ct: ct).ConfigureAwait(false);
                        foreach (var episode in missing.Records.Where(x => x.Monitored && !x.HasFile))
                        {
                            metadata.SonarrMissingEpisodes[(instance.InstanceKey, episode.Id)] = episode;
                            metadata.SonarrMissingBySeries[(instance.InstanceKey, episode.SeriesId)] =
                                metadata.SonarrMissingBySeries.GetValueOrDefault((instance.InstanceKey, episode.SeriesId)) + 1;
                            metadata.SonarrMissingBySeason[(
                                instance.InstanceKey,
                                episode.SeriesId,
                                episode.SeasonNumber)] =
                                metadata.SonarrMissingBySeason.GetValueOrDefault((
                                    instance.InstanceKey,
                                    episode.SeriesId,
                                    episode.SeasonNumber)) + 1;
                        }

                        break;
                    }
                    case RadarrClient radarr:
                    {
                        var missing = await radarr.GetMissingMoviesAsync(ct: ct).ConfigureAwait(false);
                        foreach (var movie in missing.Records.Where(x => x.Monitored && !x.HasFile))
                        {
                            metadata.RadarrMissingMovies[(instance.InstanceKey, movie.Id)] = movie;
                            if (movie.Collection?.Id is > 0)
                                metadata.RadarrMissingByCollection[(instance.InstanceKey, movie.Collection.Id)] =
                                    metadata.RadarrMissingByCollection.GetValueOrDefault((
                                        instance.InstanceKey,
                                        movie.Collection.Id)) + 1;
                        }

                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Debug("Could not fetch ARR priority metadata from {Host}: {Message}", instance.Host, ex.Message);
            }
        }

        return metadata;
    }
}

public sealed record ArrPriorityDecision
(
    int Score,
    QueueItem.PriorityOption Priority,
    IReadOnlyList<string> Reasons,
    string? StaleReason
);

public sealed class ArrPriorityMetadata
{
    public Dictionary<(string InstanceKey, int EpisodeId), SonarrMissingEpisode> SonarrMissingEpisodes { get; } = [];
    public Dictionary<(string InstanceKey, int SeriesId), int> SonarrMissingBySeries { get; } = [];
    public Dictionary<(string InstanceKey, int SeriesId, int SeasonNumber), int> SonarrMissingBySeason { get; } = [];
    public Dictionary<(string InstanceKey, int MovieId), RadarrMissingMovie> RadarrMissingMovies { get; } = [];
    public Dictionary<(string InstanceKey, int CollectionId), int> RadarrMissingByCollection { get; } = [];

    public bool HasMissingMedia =>
        SonarrMissingEpisodes.Count > 0 || RadarrMissingMovies.Count > 0;
}

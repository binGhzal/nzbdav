using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ArrSearchNudgeService(ConfigManager configManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = configManager.GetArrSearchNudgeOptions();
            try
            {
                if (options.Enabled)
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning("ARR search nudge failed: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken)
                .ConfigureAwait(false);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var arrConfig = configManager.GetArrConfig();
        var options = configManager.GetArrSearchNudgeOptions();
        if (arrConfig.GetInstanceCount() == 0) return;

        await using var dbContext = new DavDatabaseContext();
        var activeMediaKeys = await GetActiveMediaKeysAsync(dbContext, ct).ConfigureAwait(false);

        foreach (var instance in ArrIntegration.GetInstances(arrConfig))
        {
            if (instance.App == "lidarr")
                continue;

            var availableSlots = await GetAvailableCommandSlotsAsync(dbContext, instance, options, ct)
                .ConfigureAwait(false);
            if (availableSlots <= 0) continue;

            var commandName = instance.Client switch
            {
                SonarrClient => "EpisodeSearch",
                RadarrClient => "MoviesSearch",
                _ => null
            };
            if (commandName is null) continue;

            var cooldownTargetIds = await GetCooldownTargetIdsAsync(
                    dbContext,
                    instance,
                    commandName,
                    ct)
                .ConfigureAwait(false);

            var candidate = instance.Client switch
            {
                SonarrClient sonarr => await BuildSonarrCandidateAsync(
                    instance,
                    sonarr,
                    options,
                    activeMediaKeys,
                    cooldownTargetIds,
                    ct).ConfigureAwait(false),
                RadarrClient radarr => await BuildRadarrCandidateAsync(
                    instance,
                    radarr,
                    options,
                    activeMediaKeys,
                    cooldownTargetIds,
                    ct).ConfigureAwait(false),
                _ => null
            };
            if (candidate is null || candidate.TargetIds.Count == 0) continue;

            var now = DateTimeOffset.UtcNow;
            var cooldownActive = await dbContext.ArrSearchNudgeCommands
                .AsNoTracking()
                .AnyAsync(x => x.CooldownKey == candidate.CooldownKey && x.NextAllowedAt > now, ct)
                .ConfigureAwait(false);
            if (cooldownActive) continue;

            var commandRow = new ArrSearchNudgeCommand
            {
                Id = Guid.NewGuid(),
                ArrApp = instance.App,
                InstanceKey = instance.InstanceKey,
                InstanceHost = instance.Host,
                CommandName = candidate.CommandName,
                TargetsJson = JsonSerializer.Serialize(candidate.TargetIds),
                Mode = options.Mode,
                Status = "planned",
                CooldownKey = candidate.CooldownKey,
                Score = candidate.Score,
                ReasonsJson = JsonSerializer.Serialize(candidate.Reasons),
                CreatedAt = now,
                NextAllowedAt = now.AddSeconds(options.CooldownSeconds)
            };
            dbContext.ArrSearchNudgeCommands.Add(commandRow);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (options.Mode != "apply") continue;

            try
            {
                var command = instance.Client switch
                {
                    SonarrClient sonarr => await sonarr.SearchEpisodesAsync(candidate.TargetIds).WaitAsync(ct)
                        .ConfigureAwait(false),
                    RadarrClient radarr => await radarr.SearchMoviesAsync(candidate.TargetIds).WaitAsync(ct)
                        .ConfigureAwait(false),
                    _ => null
                };

                commandRow.CommandId = command?.Id;
                commandRow.Status = "executed";
                commandRow.CompletedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                commandRow.Status = "failed";
                commandRow.Error = ex.Message;
                commandRow.CompletedAt = DateTimeOffset.UtcNow;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> GetActiveMediaKeysAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        var queueIds = await dbContext.QueueItems
            .AsNoTracking()
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queueIdSet = queueIds.ToHashSet();
        var recentHistoryCutoff = DateTime.UtcNow.AddHours(-24);

        return (await dbContext.ArrDownloadCorrelations
                .AsNoTracking()
                .Where(x => x.MediaKey != null)
                .Where(x => x.QueueItemId != null && queueIdSet.Contains(x.QueueItemId.Value)
                            || x.HistoryItemId != null
                               && dbContext.HistoryItems.Any(h =>
                                   h.Id == x.HistoryItemId
                                   && h.CreatedAt >= recentHistoryCutoff))
                .Select(x => x.MediaKey!)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();
    }

    private static async Task<int> GetAvailableCommandSlotsAsync
    (
        DavDatabaseContext dbContext,
        ArrInstance instance,
        ArrConfig.SearchNudgeOptions options,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var hourAgo = now.AddHours(-1);
        var recentCommands = await dbContext.ArrSearchNudgeCommands
            .AsNoTracking()
            .Where(x => x.ArrApp == instance.App && x.InstanceKey == instance.InstanceKey)
            .Where(x => x.CreatedAt >= hourAgo)
            .CountAsync(ct)
            .ConfigureAwait(false);
        if (recentCommands >= options.MaxCommandsPerHour) return 0;

        var running = await dbContext.ArrSearchNudgeCommands
            .AsNoTracking()
            .Where(x => x.ArrApp == instance.App && x.InstanceKey == instance.InstanceKey)
            .Where(x => x.Status == "planned" && x.Mode == "apply" && x.CompletedAt == null)
            .CountAsync(ct)
            .ConfigureAwait(false);
        if (running >= options.ConcurrentCommandsPerInstance) return 0;

        return Math.Min(options.MaxCommandsPerHour - recentCommands, options.ConcurrentCommandsPerInstance - running);
    }

    private static async Task<HashSet<int>> GetCooldownTargetIdsAsync
    (
        DavDatabaseContext dbContext,
        ArrInstance instance,
        string commandName,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var targetPayloads = await dbContext.ArrSearchNudgeCommands
            .AsNoTracking()
            .Where(x => x.ArrApp == instance.App && x.InstanceKey == instance.InstanceKey)
            .Where(x => x.CommandName == commandName)
            .Where(x => x.NextAllowedAt > now)
            .Select(x => x.TargetsJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = new HashSet<int>();
        foreach (var payload in targetPayloads)
        {
            try
            {
                foreach (var id in JsonSerializer.Deserialize<List<int>>(payload) ?? [])
                    ids.Add(id);
            }
            catch (JsonException)
            {
                Log.Warning("Skipping malformed ARR search nudge target payload for {InstanceKey}", instance.InstanceKey);
            }
        }

        return ids;
    }

    private static async Task<SearchNudgeCandidate?> BuildSonarrCandidateAsync
    (
        ArrInstance instance,
        SonarrClient sonarr,
        ArrConfig.SearchNudgeOptions options,
        HashSet<string> activeMediaKeys,
        HashSet<int> cooldownTargetIds,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var missing = await sonarr.GetMissingEpisodesAsync().WaitAsync(ct).ConfigureAwait(false);
        var monitored = missing.Records
            .Where(x => x.Monitored && !x.HasFile)
            .Where(x => x.AirDateUtc is null || x.AirDateUtc <= now.AddDays(1))
            .Where(x => !activeMediaKeys.Contains($"sonarr:episode:{x.Id}"))
            .Where(x => !cooldownTargetIds.Contains(x.Id))
            .ToList();
        if (monitored.Count == 0) return null;

        var bySeries = monitored.GroupBy(x => x.SeriesId).ToDictionary(x => x.Key, x => x.Count());
        var bySeason = monitored.GroupBy(x => (x.SeriesId, x.SeasonNumber)).ToDictionary(x => x.Key, x => x.Count());
        var candidates = monitored
            .Select(episode => new
            {
                Episode = episode,
                Score = ScoreSonarrSearchCandidate(episode, bySeries, bySeason, out var reasons),
                Reasons = reasons
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Episode.AirDateUtc ?? DateTimeOffset.MaxValue)
            .Take(options.SonarrBatchSize)
            .ToList();

        if (candidates.Count == 0) return null;
        return new SearchNudgeCandidate(
            CommandName: "EpisodeSearch",
            TargetIds: candidates.Select(x => x.Episode.Id).ToList(),
            CooldownKey: $"{instance.InstanceKey}:sonarr:episodes:{string.Join(",", candidates.Select(x => x.Episode.Id))}",
            Score: candidates.Max(x => x.Score),
            Reasons: candidates.SelectMany(x => x.Reasons).Distinct().ToList());
    }

    private static async Task<SearchNudgeCandidate?> BuildRadarrCandidateAsync
    (
        ArrInstance instance,
        RadarrClient radarr,
        ArrConfig.SearchNudgeOptions options,
        HashSet<string> activeMediaKeys,
        HashSet<int> cooldownTargetIds,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var missing = await radarr.GetMissingMoviesAsync().WaitAsync(ct).ConfigureAwait(false);
        var monitored = missing.Records
            .Where(x => x.Monitored && !x.HasFile)
            .Where(x =>
            {
                var releaseDate = x.PhysicalRelease ?? x.DigitalRelease ?? x.InCinemas;
                return releaseDate is null || releaseDate <= now;
            })
            .Where(x => !activeMediaKeys.Contains($"radarr:movie:{x.Id}"))
            .Where(x => !cooldownTargetIds.Contains(x.Id))
            .ToList();
        if (monitored.Count == 0) return null;

        var byCollection = monitored
            .Where(x => x.Collection?.Id is > 0)
            .GroupBy(x => x.Collection!.Id)
            .ToDictionary(x => x.Key, x => x.Count());
        var candidates = monitored
            .Select(movie => new
            {
                Movie = movie,
                Score = ScoreRadarrSearchCandidate(movie, byCollection, out var reasons),
                Reasons = reasons
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Movie.PhysicalRelease ?? x.Movie.DigitalRelease ?? x.Movie.InCinemas ?? DateTimeOffset.MaxValue)
            .Take(options.RadarrBatchSize)
            .ToList();

        if (candidates.Count == 0) return null;
        return new SearchNudgeCandidate(
            CommandName: "MoviesSearch",
            TargetIds: candidates.Select(x => x.Movie.Id).ToList(),
            CooldownKey: $"{instance.InstanceKey}:radarr:movies:{string.Join(",", candidates.Select(x => x.Movie.Id))}",
            Score: candidates.Max(x => x.Score),
            Reasons: candidates.SelectMany(x => x.Reasons).Distinct().ToList());
    }

    private static int ScoreSonarrSearchCandidate
    (
        SonarrMissingEpisode episode,
        Dictionary<int, int> bySeries,
        Dictionary<(int SeriesId, int SeasonNumber), int> bySeason,
        out List<string> reasons
    )
    {
        reasons = [];
        var score = 0;
        var remainingSeries = bySeries.GetValueOrDefault(episode.SeriesId);
        var remainingSeason = bySeason.GetValueOrDefault((episode.SeriesId, episode.SeasonNumber));
        if (remainingSeries <= 1)
        {
            score += 300;
            reasons.Add("series-completion");
        }
        else if (remainingSeries <= 3)
        {
            score += 150;
            reasons.Add("series-nearly-complete");
        }
        if (remainingSeason <= 1)
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

        return Math.Clamp(score, 0, 1000);
    }

    private static int ScoreRadarrSearchCandidate
    (
        RadarrMissingMovie movie,
        Dictionary<int, int> byCollection,
        out List<string> reasons
    )
    {
        reasons = [];
        var score = 0;
        if (movie.Collection?.Id is > 0)
        {
            var remaining = byCollection.GetValueOrDefault(movie.Collection.Id);
            if (remaining <= 1)
            {
                score += 300;
                reasons.Add("collection-completion");
            }
            else if (remaining <= 2)
            {
                score += 180;
                reasons.Add("collection-nearly-complete");
            }
            else if (remaining <= 5)
            {
                score += 100;
                reasons.Add("collection-low-missing-count");
            }
        }

        var releaseDate = movie.PhysicalRelease ?? movie.DigitalRelease ?? movie.InCinemas;
        if (releaseDate is { } date)
        {
            var age = DateTimeOffset.UtcNow - date;
            if (age >= TimeSpan.Zero && age <= TimeSpan.FromDays(30))
            {
                score += 180;
                reasons.Add("recent-movie-release");
            }
        }

        return Math.Clamp(score, 0, 1000);
    }

    private sealed record SearchNudgeCandidate
    (
        string CommandName,
        List<int> TargetIds,
        string CooldownKey,
        int Score,
        List<string> Reasons
    );
}

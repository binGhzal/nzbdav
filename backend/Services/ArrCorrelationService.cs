using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.LidarrModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ArrCorrelationService(ConfigManager configManager) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning("ARR correlation refresh failed: {Message}", ex.Message);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var arrConfig = configManager.GetArrConfig();
        if (arrConfig.GetInstanceCount() == 0) return;

        foreach (var instance in ArrIntegration.GetInstances(arrConfig))
            await RefreshInstanceAsync(instance, ct).ConfigureAwait(false);
    }

    private static async Task RefreshInstanceAsync(ArrInstance instance, CancellationToken ct)
    {
        var records = await GetQueueRecordsAsync(instance, ct).ConfigureAwait(false);
        if (records.Count == 0) return;

        await using var dbContext = new DavDatabaseContext();
        var queueItems = await dbContext.QueueItems
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var recentHistory = await dbContext.HistoryItems
            .AsNoTracking()
            .Where(x => x.CreatedAt >= DateTime.UtcNow.AddDays(-14))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var queueById = queueItems.ToDictionary(x => x.Id);
        var historyById = recentHistory.ToDictionary(x => x.Id);
        var duplicateMediaKeys = records
            .Select(x => ArrIntegration.GetMediaKey(instance.App, x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x!)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet();

        foreach (var record in records)
        {
            var now = DateTimeOffset.UtcNow;
            var match = MatchRecord(record, queueItems, recentHistory, queueById, historyById);
            var mediaKey = ArrIntegration.GetMediaKey(instance.App, record);
            var existing = await FindExistingCorrelationAsync(dbContext, instance, record, match, mediaKey, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                existing = new ArrDownloadCorrelation
                {
                    Id = Guid.NewGuid(),
                    ArrApp = instance.App,
                    InstanceKey = instance.InstanceKey,
                    InstanceHost = instance.Host,
                    CreatedAt = now
                };
                dbContext.ArrDownloadCorrelations.Add(existing);
            }

            ApplyRecord(existing, instance, record, match, mediaKey, duplicateMediaKeys.Contains(mediaKey ?? ""), now);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<ArrQueueRecord>> GetQueueRecordsAsync(ArrInstance instance, CancellationToken ct)
    {
        try
        {
            return instance.Client switch
            {
                SonarrClient sonarr => (await sonarr.GetSonarrQueueAsync().WaitAsync(ct).ConfigureAwait(false))
                    .Records.Cast<ArrQueueRecord>().ToList(),
                RadarrClient radarr => (await radarr.GetRadarrQueueAsync().WaitAsync(ct).ConfigureAwait(false))
                    .Records.Cast<ArrQueueRecord>().ToList(),
                LidarrClient lidarr => (await lidarr.GetLidarrQueueAsync().WaitAsync(ct).ConfigureAwait(false))
                    .Records.Cast<ArrQueueRecord>().ToList(),
                _ => []
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Debug("Could not poll {App} queue at {Host}: {Message}", instance.App, instance.Host, ex.Message);
            return [];
        }
    }

    private static async Task<ArrDownloadCorrelation?> FindExistingCorrelationAsync
    (
        DavDatabaseContext dbContext,
        ArrInstance instance,
        ArrQueueRecord record,
        QueueHistoryMatch match,
        string? mediaKey,
        CancellationToken ct
    )
    {
        var query = dbContext.ArrDownloadCorrelations
            .Where(x => x.ArrApp == instance.App && x.InstanceKey == instance.InstanceKey);

        if (!string.IsNullOrWhiteSpace(record.DownloadId))
        {
            var existingByDownloadId = await query
                .FirstOrDefaultAsync(x => x.DownloadId == record.DownloadId, ct)
                .ConfigureAwait(false);
            if (existingByDownloadId is not null) return existingByDownloadId;
        }

        if (match.QueueItemId is not null)
        {
            var existingByQueue = await query
                .FirstOrDefaultAsync(x => x.QueueItemId == match.QueueItemId, ct)
                .ConfigureAwait(false);
            if (existingByQueue is not null) return existingByQueue;
        }

        if (!string.IsNullOrWhiteSpace(mediaKey))
        {
            var existingByMedia = await query
                .FirstOrDefaultAsync(x => x.MediaKey == mediaKey && x.QueueRecordId == record.Id, ct)
                .ConfigureAwait(false);
            if (existingByMedia is not null) return existingByMedia;
        }

        return null;
    }

    private static QueueHistoryMatch MatchRecord
    (
        ArrQueueRecord record,
        List<QueueItem> queueItems,
        List<HistoryItem> historyItems,
        Dictionary<Guid, QueueItem> queueById,
        Dictionary<Guid, HistoryItem> historyById
    )
    {
        if (Guid.TryParse(record.DownloadId, out var downloadGuid))
        {
            if (queueById.ContainsKey(downloadGuid)) return new QueueHistoryMatch(downloadGuid, null);
            if (historyById.ContainsKey(downloadGuid)) return new QueueHistoryMatch(null, downloadGuid);
        }

        var title = ArrIntegration.NormalizeTitle(record.Title);
        if (title.Length == 0) return new QueueHistoryMatch(null, null);

        var queueMatch = queueItems.FirstOrDefault(q => IsTitleMatch(title, q.JobName, q.FileName));
        if (queueMatch is not null) return new QueueHistoryMatch(queueMatch.Id, null);

        var historyMatch = historyItems.FirstOrDefault(h => IsTitleMatch(title, h.JobName, h.FileName));
        return new QueueHistoryMatch(null, historyMatch?.Id);
    }

    private static bool IsTitleMatch(string normalizedArrTitle, string jobName, string fileName)
    {
        var normalizedJob = ArrIntegration.NormalizeTitle(jobName);
        var normalizedFile = ArrIntegration.NormalizeTitle(Path.GetFileNameWithoutExtension(fileName));
        if (normalizedJob.Length == 0 && normalizedFile.Length == 0) return false;
        return IsOneTitleMatch(normalizedArrTitle, normalizedJob)
               || IsOneTitleMatch(normalizedArrTitle, normalizedFile);
    }

    private static bool IsOneTitleMatch(string normalizedArrTitle, string normalizedLocalTitle)
    {
        if (normalizedArrTitle.Length == 0 || normalizedLocalTitle.Length == 0) return false;
        return normalizedArrTitle == normalizedLocalTitle
               || normalizedArrTitle.Contains(normalizedLocalTitle)
               || normalizedLocalTitle.Contains(normalizedArrTitle);
    }

    private static void ApplyRecord
    (
        ArrDownloadCorrelation correlation,
        ArrInstance instance,
        ArrQueueRecord record,
        QueueHistoryMatch match,
        string? mediaKey,
        bool isDuplicate,
        DateTimeOffset now
    )
    {
        correlation.QueueItemId = match.QueueItemId;
        correlation.HistoryItemId = match.HistoryItemId;
        correlation.InstanceHost = instance.Host;
        correlation.QueueRecordId = record.Id;
        correlation.ReleaseTitle = record.Title;
        correlation.Indexer = record.Indexer;
        correlation.DownloadClient = record.DownloadClient;
        correlation.Quality = record.Quality?.GetRawText();
        correlation.CustomFormatsJson = record.CustomFormats?.GetRawText();
        correlation.Status = record.Status;
        correlation.TrackedDownloadStatus = record.TrackedDownloadStatus;
        correlation.TrackedDownloadState = record.TrackedDownloadState;
        correlation.IsDuplicate = isDuplicate;
        correlation.IsUpgrade = IsUpgradeRecord(record);
        correlation.UpdatedAt = now;
        correlation.LastSeenAt = now;

        if (correlation.ManualLock) return;

        correlation.Source = "auto";
        correlation.DownloadId = record.DownloadId;
        correlation.MediaKey = mediaKey;

        switch (record)
        {
            case SonarrQueueRecord sonarr:
                correlation.SeriesId = sonarr.SeriesId > 0 ? sonarr.SeriesId : null;
                correlation.EpisodeId = sonarr.EpisodeId > 0 ? sonarr.EpisodeId : null;
                correlation.SeasonNumber = sonarr.SeasonNumber > 0 ? sonarr.SeasonNumber : null;
                correlation.EpisodeIdsJson = sonarr.EpisodeId > 0 ? JsonSerializer.Serialize(new[] { sonarr.EpisodeId }) : null;
                break;
            case RadarrQueueRecord radarr:
                correlation.MovieId = radarr.MovieId > 0 ? radarr.MovieId : null;
                break;
            case LidarrQueueRecord lidarr:
                correlation.ArtistId = lidarr.ArtistId > 0 ? lidarr.ArtistId : null;
                correlation.AlbumId = lidarr.AlbumId > 0 ? lidarr.AlbumId : null;
                break;
        }
    }

    private static bool IsUpgradeRecord(ArrQueueRecord record)
    {
        var messages = record.StatusMessages
            .SelectMany(x => x.Messages)
            .Select(x => x.ToLowerInvariant());
        return messages.Any(x => x.Contains("upgrade") || x.Contains("custom format"));
    }

    private readonly record struct QueueHistoryMatch(Guid? QueueItemId, Guid? HistoryItemId);
}

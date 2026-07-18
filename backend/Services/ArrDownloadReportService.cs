using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ArrDownloadReportService
{
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(30);
    private readonly Func<IEnumerable<ArrClient>> _getArrClients;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefreshByInstance = new();

    public ArrDownloadReportService(ConfigManager configManager)
        : this(configManager, () => configManager.GetArrConfig().GetArrClients())
    {
    }

    public ArrDownloadReportService(
        ConfigManager configManager,
        Func<IEnumerable<ArrClient>> getArrClients)
    {
        _getArrClients = getArrClients;
    }

    public async Task RecordQueueLifecycleAsync
    (
        DavDatabaseClient dbClient,
        QueueItem queueItem,
        string state,
        string? reason = null,
        CancellationToken ct = default,
        bool saveChanges = true
    )
    {
        var correlations = await dbClient.Ctx.ArrDownloadCorrelations
            .AsNoTracking()
            .Where(x => x.QueueItemId == queueItem.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (correlations.Count == 0)
        {
            dbClient.Ctx.ArrDownloadLifecycleEvents.Add(new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                QueueItemId = queueItem.Id,
                ArrApp = "unknown",
                InstanceKey = "unknown",
                State = state,
                StateReason = reason,
                CreatedAt = DateTimeOffset.UtcNow
            });
            if (saveChanges)
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        foreach (var correlation in correlations)
        {
            dbClient.Ctx.ArrDownloadLifecycleEvents.Add(new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                QueueItemId = queueItem.Id,
                ArrApp = correlation.ArrApp,
                InstanceKey = correlation.InstanceKey,
                DownloadId = correlation.DownloadId,
                MediaKey = correlation.MediaKey,
                State = state,
                StateReason = reason,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (saveChanges)
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordHistoryLifecycleAsync
    (
        DavDatabaseClient dbClient,
        HistoryItem historyItem,
        string state,
        string? reason = null,
        CancellationToken ct = default,
        bool saveChanges = true
    )
    {
        var correlations = await dbClient.Ctx.ArrDownloadCorrelations
            .Where(x => x.QueueItemId == historyItem.Id || x.HistoryItemId == historyItem.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var correlation in correlations)
        {
            correlation.QueueItemId = null;
            correlation.HistoryItemId = historyItem.Id;
            correlation.UpdatedAt = DateTimeOffset.UtcNow;
            dbClient.Ctx.ArrDownloadLifecycleEvents.Add(new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                HistoryItemId = historyItem.Id,
                ArrApp = correlation.ArrApp,
                InstanceKey = correlation.InstanceKey,
                DownloadId = correlation.DownloadId,
                MediaKey = correlation.MediaKey,
                State = state,
                StateReason = reason,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (correlations.Count == 0)
        {
            dbClient.Ctx.ArrDownloadLifecycleEvents.Add(new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                HistoryItemId = historyItem.Id,
                ArrApp = "unknown",
                InstanceKey = "unknown",
                State = state,
                StateReason = reason,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (saveChanges)
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> StageCompletionRefreshAsync(
        DavDatabaseClient dbClient,
        HistoryItem historyItem,
        IEnumerable<string> requiredInvalidationPaths,
        CancellationToken ct = default)
    {
        var exists = dbClient.Ctx.ArrImportCommands.Local.Any(x => x.HistoryItemId == historyItem.Id)
                     || await dbClient.Ctx.ArrImportCommands
                         .AsNoTracking()
                         .AnyAsync(x => x.HistoryItemId == historyItem.Id, ct)
                         .ConfigureAwait(false);
        if (exists) return false;

        var now = DateTimeOffset.UtcNow;
        var paths = requiredInvalidationPaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        dbClient.Ctx.ArrImportCommands.Add(new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyItem.Id,
            Category = historyItem.Category,
            RequiredInvalidationPathsJson = JsonSerializer.Serialize(paths),
            Status = ArrImportCommandStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
        });
        return true;
    }

    public async Task RefreshMonitoredDownloadsDebouncedAsync
    (
        string category,
        CancellationToken ct = default
    )
    {
        foreach (var arrClient in _getArrClients())
            await RefreshMonitoredDownloadsAsync(arrClient, category, bypassDebounce: false, ct).ConfigureAwait(false);
    }

    public async Task RefreshMonitoredDownloadsOnCompletionAsync(
        string category,
        CancellationToken ct = default)
    {
        foreach (var arrClient in _getArrClients())
            await RefreshMonitoredDownloadsAsync(arrClient, category, bypassDebounce: true, ct).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloadsAsync
    (
        ArrClient arrClient,
        string category,
        bool bypassDebounce,
        CancellationToken ct
    )
    {
        try
        {
            var instanceKey = ArrIntegration.GetInstanceKey(GetAppName(arrClient), arrClient.Host);
            var now = DateTimeOffset.UtcNow;
            if (!bypassDebounce
                && _lastRefreshByInstance.TryGetValue(instanceKey, out var lastRefresh)
                && now - lastRefresh < RefreshDebounce)
                return;

            var downloadClients = await arrClient.GetDownloadClientsAsync(ct).ConfigureAwait(false);
            if (downloadClients.All(x => !string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase)))
                return;

            var queueCount = await arrClient.GetQueueCountAsync(ct).ConfigureAwait(false);
            if (queueCount >= 300) return;

            await arrClient.RefreshMonitoredDownloads(ct).ConfigureAwait(false);
            _lastRefreshByInstance[instanceKey] = now;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Debug("Could not refresh monitored downloads for Arr instance `{Host}`: {Message}", arrClient.Host, e.Message);
        }
    }

    private static string GetAppName(ArrClient client) => client switch
    {
        SonarrClient => "sonarr",
        RadarrClient => "radarr",
        LidarrClient => "lidarr",
        _ => "arr"
    };
}

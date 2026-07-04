using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.LidarrModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

internal static class ArrIntegration
{
    public static IReadOnlyList<ArrInstance> GetInstances(ArrConfig config)
    {
        var instances = new List<ArrInstance>();
        instances.AddRange(config.SonarrInstances.Select(x => new ArrInstance(
            "sonarr", GetInstanceKey("sonarr", x.Host), x.Host, new SonarrClient(x.Host, x.ApiKey))));
        instances.AddRange(config.RadarrInstances.Select(x => new ArrInstance(
            "radarr", GetInstanceKey("radarr", x.Host), x.Host, new RadarrClient(x.Host, x.ApiKey))));
        instances.AddRange(config.LidarrInstances.Select(x => new ArrInstance(
            "lidarr", GetInstanceKey("lidarr", x.Host), x.Host, new LidarrClient(x.Host, x.ApiKey))));
        return instances;
    }

    public static string GetInstanceKey(string app, string host)
    {
        host = host.Trim().TrimEnd('/').ToLowerInvariant();
        var raw = $"{app}:{host}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        return $"{app}:{hash}";
    }

    public static string? GetMediaKey(string app, ArrQueueRecord record)
    {
        return record switch
        {
            SonarrQueueRecord sonarr when sonarr.EpisodeId > 0 => $"{app}:episode:{sonarr.EpisodeId}",
            SonarrQueueRecord sonarr when sonarr.SeriesId > 0 => $"{app}:series:{sonarr.SeriesId}:season:{sonarr.SeasonNumber}",
            RadarrQueueRecord radarr when radarr.MovieId > 0 => $"{app}:movie:{radarr.MovieId}",
            LidarrQueueRecord lidarr when lidarr.AlbumId > 0 => $"{app}:album:{lidarr.AlbumId}",
            LidarrQueueRecord lidarr when lidarr.ArtistId > 0 => $"{app}:artist:{lidarr.ArtistId}",
            _ => null
        };
    }

    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
        }

        return builder.ToString();
    }

    public static string? JsonOrNull<T>(T? value)
    {
        if (value == null) return null;
        return JsonSerializer.Serialize(value);
    }

    public static QueueItem.PriorityOption ClampAutomaticPriority
    (
        QueueItem.PriorityOption computed,
        QueueItem.PriorityOption manual,
        int configuredMax
    )
    {
        if (manual == QueueItem.PriorityOption.Paused) return QueueItem.PriorityOption.Paused;
        if (manual == QueueItem.PriorityOption.Force) return QueueItem.PriorityOption.Force;
        var max = (QueueItem.PriorityOption)Math.Clamp(
            configuredMax,
            (int)QueueItem.PriorityOption.Low,
            (int)QueueItem.PriorityOption.High);
        var capped = (QueueItem.PriorityOption)Math.Min((int)computed, (int)max);
        return (QueueItem.PriorityOption)Math.Max((int)capped, (int)manual);
    }
}

internal sealed record ArrInstance(string App, string InstanceKey, string Host, ArrClient Client);

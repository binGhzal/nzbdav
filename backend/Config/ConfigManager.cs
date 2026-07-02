using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetVariable("NZBDAV_VERSION") ?? "unknown";
    private const int MaxAutoQueueCpuConcurrency = 16;
    private const int MaxQueueFileProcessingConcurrency = 64;
    private const int MaxHealthCheckConcurrency = 64;

    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value.ToNullIfEmpty() : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = GetConfigValue(configName);
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = GetConfigValue("rclone.mount-dir")
                       ?? EnvironmentUtil.GetVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return GetConfigValue("api.key")
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = GetConfigValue("api.categories")
                    ?? EnvironmentUtil.GetVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return GetConfigValue("api.manual-category")
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return GetConfigValue("webdav.user")
               ?? EnvironmentUtil.GetVariable("WEBDAV_USER")
               ?? EnvironmentUtil.GetVariable("NZBDAV_ADMIN_USERNAME")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = GetConfigValue("webdav.pass");
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetVariable("WEBDAV_PASSWORD")
                   ?? EnvironmentUtil.GetVariable("NZBDAV_ADMIN_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = GetConfigValue("api.ensure-importable-video");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("webdav.show-hidden-files");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return GetConfigValue("media.library-dir");
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            GetConfigValue("usenet.max-download-connections")
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public bool IsQueuePaused()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("queue.paused");
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public bool IsAdaptiveConnectionCountEnabled()
    {
        var defaultValue = true;
        var configValue = GetConfigValue("usenet.adaptive-connections-enabled");
        return configValue != null ? bool.Parse(configValue) : defaultValue;
    }

    public int GetAdaptiveMaxDownloadConnections()
    {
        var maxDownloadConnections = Math.Max(1, GetMaxDownloadConnections());
        if (!IsAdaptiveConnectionCountEnabled()) return maxDownloadConnections;

        var providerConnections = Math.Max(1, GetUsenetProviderConfig().TotalPooledConnections);
        var cpuBasedConnections = Math.Clamp(Environment.ProcessorCount * 4, 4, 64);
        var automaticTarget = Math.Min(maxDownloadConnections, Math.Min(providerConnections, cpuBasedConnections));
        return ApplyRuntimePressureLimit(automaticTarget);
    }

    public int GetAdaptiveMaxStreamingConnections()
    {
        var maxStreamingConnections = Math.Max(1, GetMaxStreamingConnections());
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(maxStreamingConnections)
            : maxStreamingConnections;
    }

    public int GetMaxStreamingConnections()
    {
        var configValue = int.Parse(GetConfigValue("usenet.max-streaming-connections") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, 64);

        var articleBufferSize = Math.Max(1, GetArticleBufferSize());
        return Math.Min(articleBufferSize, Math.Max(1, GetMaxDownloadConnections()));
    }

    public int GetMaxTotalStreamingConnections()
    {
        var configValue = int.Parse(GetConfigValue("usenet.max-total-streaming-connections") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, 128);

        var perStreamConnections = Math.Max(1, GetMaxStreamingConnections());
        var cpuBasedLimit = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var downloadConnections = Math.Max(1, GetMaxDownloadConnections());
        return Math.Clamp(
            Math.Min(Math.Min(perStreamConnections * 2, cpuBasedLimit), downloadConnections),
            1,
            128
        );
    }

    public int GetAdaptiveMaxTotalStreamingConnections()
    {
        var maxTotalStreamingConnections = GetMaxTotalStreamingConnections();
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(maxTotalStreamingConnections)
            : maxTotalStreamingConnections;
    }

    public int GetMaxConcurrentQueueDownloads()
    {
        var configValue = int.Parse(GetConfigValue("queue.max-concurrent-downloads") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, 16);

        var downloadConnections = Math.Max(1, GetMaxDownloadConnections());
        var downloadBasedLimit = Math.Clamp(downloadConnections / 8 + 1, 1, 4);
        var cpuBasedLimit = Math.Clamp(Environment.ProcessorCount / 4 + 1, 1, 4);
        return Math.Min(downloadBasedLimit, cpuBasedLimit);
    }

    public int GetAdaptiveMaxConcurrentQueueDownloads()
    {
        var maxConcurrentQueueDownloads = GetMaxConcurrentQueueDownloads();
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(maxConcurrentQueueDownloads)
            : maxConcurrentQueueDownloads;
    }

    public int GetQueueFileProcessingConcurrency()
    {
        var configValue = int.Parse(GetConfigValue("queue.file-processing-concurrency") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxQueueFileProcessingConcurrency);

        var queueWorkers = Math.Max(1, GetMaxConcurrentQueueDownloads());
        var perJobCpuBudget = (int)Math.Ceiling(GetQueueCpuConcurrencyBudget() / (double)queueWorkers);
        var downloadConnections = Math.Max(1, GetMaxDownloadConnections());
        return Math.Clamp(
            Math.Min(perJobCpuBudget, downloadConnections),
            1,
            MaxQueueFileProcessingConcurrency
        );
    }

    public int GetAdaptiveQueueFileProcessingConcurrency()
    {
        var queueFileProcessingConcurrency = GetQueueFileProcessingConcurrency();
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(queueFileProcessingConcurrency)
            : queueFileProcessingConcurrency;
    }

    public long GetArticleCacheMaxBytes()
    {
        var megabytes = long.Parse(
            GetConfigValue("usenet.article-cache-max-megabytes")
            ?? "256"
        );
        return Math.Max(1, megabytes) * 1024 * 1024;
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            GetConfigValue("usenet.article-buffer-size")
            ?? "8"
        );
    }

    private static int ApplyRuntimePressureLimit(int configuredLimit)
    {
        var multiplier = Math.Min(GetMemoryPressureMultiplier(), GetThreadPoolPressureMultiplier());
        return Math.Max(1, (int)Math.Floor(configuredLimit * multiplier));
    }

    private static double GetMemoryPressureMultiplier()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        var thresholdBytes = memoryInfo.HighMemoryLoadThresholdBytes;
        if (thresholdBytes <= 0) return 1.00;

        var pressure = memoryInfo.MemoryLoadBytes / (double)thresholdBytes;
        return pressure switch
        {
            >= 0.95 => 0.25,
            >= 0.90 => 0.50,
            >= 0.80 => 0.75,
            _ => 1.00
        };
    }

    private static double GetThreadPoolPressureMultiplier()
    {
        var pendingWorkItems = ThreadPool.PendingWorkItemCount;
        if (pendingWorkItems <= 0) return 1.00;

        var cpuCount = Math.Max(1, Environment.ProcessorCount);
        return pendingWorkItems switch
        {
            var pending when pending >= cpuCount * 64L => 0.50,
            var pending when pending >= cpuCount * 32L => 0.75,
            _ => 1.00
        };
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = GetConfigValue("usenet.streaming-priority");
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public TimeSpan GetStreamingSegmentTimeout()
    {
        var seconds = int.Parse(
            GetConfigValue("usenet.streaming-segment-timeout")
            ?? "8"
        );
        return TimeSpan.FromSeconds(seconds);
    }

    public int GetStreamingSegmentRetries()
    {
        return int.Parse(
            GetConfigValue("usenet.streaming-segment-retries")
            ?? "3"
        );
    }

    public int GetHealthCheckConcurrency()
    {
        return int.Parse(
            GetConfigValue("repair.healthcheck-concurrency")
            ?? "50"
        );
    }

    public int GetAdaptiveHealthCheckConcurrency()
    {
        var healthCheckConcurrency = Math.Min(
            Math.Max(1, GetHealthCheckConcurrency()),
            GetHealthCheckCpuConcurrencyLimit()
        );
        healthCheckConcurrency = Math.Min(healthCheckConcurrency, Math.Max(1, GetMaxDownloadConnections()));
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(healthCheckConcurrency)
            : healthCheckConcurrency;
    }

    private static int GetQueueCpuConcurrencyBudget()
    {
        return Math.Clamp(Environment.ProcessorCount, 2, MaxAutoQueueCpuConcurrency);
    }

    private static int GetHealthCheckCpuConcurrencyLimit()
    {
        return Math.Clamp(Environment.ProcessorCount * 2, 2, MaxHealthCheckConcurrency);
    }

    public int GetConnectionIdleTimeoutSeconds()
    {
        return int.Parse(
            GetConfigValue("usenet.connection-idle-timeout-seconds")
            ?? "120"
        );
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = GetConfigValue("webdav.enforce-readonly");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    /// <summary>
    /// Master switch for NNTP STAT pipelining. When disabled, STAT health checks always run
    /// one-command-per-round-trip regardless of any provider's individual pipelining setting.
    /// </summary>
    public bool GetNntpPipeliningEnabled()
    {
        var defaultValue = true;
        var configValue = GetConfigValue("usenet.nntp-pipelining.enabled");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    /// <summary>
    /// The maximum number of STAT commands sent back-to-back before reading their responses.
    /// A value of 1 (or less) effectively disables pipelining. Bounded to a conservative range:
    /// the benefit flattens out well before 150 and higher depths only add provider risk for
    /// fractions of a second.
    /// </summary>
    public int GetNntpPipeliningDepth()
    {
        var defaultValue = 50;
        var configValue = GetConfigValue("usenet.nntp-pipelining.depth");
        var depth = configValue != null ? int.Parse(configValue) : defaultValue;
        return Math.Clamp(depth, 1, 150);
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("webdav.preview-par2-files");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = GetConfigValue("api.ignore-history-limit");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("repair.enable");
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        var configValue = GetConfigValue<UsenetProviderConfig>("usenet.providers");
        if (configValue != null) return configValue;

        var envValue = EnvironmentUtil.GetVariable("NZBDAV_USENET_PROVIDERS_JSON");
        return envValue == null ? defaultValue : JsonSerializer.Deserialize<UsenetProviderConfig>(envValue) ?? defaultValue;
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("rclone.rc-enabled");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return GetConfigValue("api.user-agent")
               ?? EnvironmentUtil.GetVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("db.is-startup-vacuum-enabled");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("api.nzb-backup-enabled");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return GetConfigValue("api.nzb-backup-location");
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = GetConfigValue("maintenance.remove-orphaned-schedule-enabled");
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = GetConfigValue("maintenance.remove-orphaned-schedule-time");
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}

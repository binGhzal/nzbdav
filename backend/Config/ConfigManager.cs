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
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = GetConfigValue("webdav.pass");
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetVariable("WEBDAV_PASSWORD");
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

    public int GetArticleBufferSize()
    {
        return int.Parse(
            GetConfigValue("usenet.article-buffer-size")
            ?? "40"
        );
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
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
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

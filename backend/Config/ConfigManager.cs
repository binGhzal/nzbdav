using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetVariable("NZBDAV_VERSION") ?? "unknown";
    private const int MaxQueueFileProcessingConcurrency = 256;
    private const int MaxAutoQueueDownloads = 16;
    private const int MaxManualQueueDownloads = 128;
    private const int MaxManualWorkerJobsPerKind = 128;
    private const int MaxStreamingConnectionsPerStream = 256;
    private const int MaxTotalStreamingConnections = 512;
    private const int MaxHealthCheckConcurrency = 64;
    private const int MaxPostDownloadVerificationConcurrency = 512;
    private const string CgroupCpuStatPath = "/sys/fs/cgroup/cpu.stat";
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions ConfigJsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, string> _config = new();
    private readonly object _runtimePressureLock = new();
    private DateTimeOffset _lastCpuSampleAt = DateTimeOffset.UtcNow;
    private double _lastCpuUsageSeconds = GetCurrentCpuUsageSeconds();
    private double _lastProcessCpuCores;
    private double _lastCpuPressureMultiplier = 1.00;
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

    private string? GetFirstConfigValue(params string[] configNames)
    {
        foreach (var configName in configNames)
        {
            var value = GetConfigValue(configName);
            if (value != null) return value;
        }

        return null;
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = GetConfigValue(configName);
        return TryDeserializeConfig<T>(rawValue);
    }

    private static T? TryDeserializeConfig<T>(string? rawValue)
    {
        if (rawValue == null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(rawValue, ConfigJsonSerializerOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private int GetIntConfigValue(string configName, int defaultValue)
    {
        var rawValue = GetConfigValue(configName);
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private int GetPositiveIntConfigValue(string configName, int defaultValue)
    {
        var value = GetIntConfigValue(configName, defaultValue);
        return value > 0 ? value : defaultValue;
    }

    private long GetLongConfigValue(string configName, long defaultValue)
    {
        var rawValue = GetConfigValue(configName);
        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private bool GetBoolConfigValue(string configName, bool defaultValue)
    {
        var rawValue = GetConfigValue(configName);
        return bool.TryParse(rawValue, out var parsed) ? parsed : defaultValue;
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        var changedConfig = new Dictionary<string, string>();
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                if (!_config.TryGetValue(configItem.ConfigName, out var existingValue))
                {
                    if (IsEffectivelyUnsetConfigValue(configItem.ConfigName, configItem.ConfigValue))
                        continue;

                    _config[configItem.ConfigName] = configItem.ConfigValue;
                    changedConfig[configItem.ConfigName] = configItem.ConfigValue;
                    continue;
                }

                if (AreConfigValuesEquivalent(configItem.ConfigName, existingValue, configItem.ConfigValue))
                    continue;

                _config[configItem.ConfigName] = configItem.ConfigValue;
                changedConfig[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        if (changedConfig.Count > 0)
            OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public static bool AreConfigValuesEquivalent(string configName, string? existingValue, string? newValue)
    {
        if (string.Equals(existingValue, newValue, StringComparison.Ordinal)) return true;
        if (IsEffectivelyUnsetConfigValue(configName, existingValue)
            && IsEffectivelyUnsetConfigValue(configName, newValue))
            return true;
        if (existingValue is null || newValue is null) return false;

        if (configName == "rclone.host")
            return NormalizeEndpointConfigOrNull(existingValue) == NormalizeEndpointConfigOrNull(newValue);

        if (IsMountTypeConfig(configName))
            return NormalizeMountTypeOrNull(existingValue) == NormalizeMountTypeOrNull(newValue);

        if (IsMountDirectoryConfig(configName))
            return NormalizeMountDirOrNull(existingValue) == NormalizeMountDirOrNull(newValue);

        if (IsBooleanConfig(configName)
            && bool.TryParse(existingValue, out var existingBool)
            && bool.TryParse(newValue, out var newBool))
            return existingBool == newBool;

        if (IsIntegerConfig(configName)
            && int.TryParse(existingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingInt)
            && int.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newInt))
            return existingInt == newInt;

        if (IsLongConfig(configName)
            && long.TryParse(existingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingLong)
            && long.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newLong))
            return existingLong == newLong;

        if (IsJsonConfig(configName))
            return AreJsonValuesEquivalent(existingValue, newValue);

        return false;
    }

    public static bool IsUnsetConfigValue(string? value)
    {
        return value == "";
    }

    public static bool IsEffectivelyUnsetConfigValue(string configName, string? value)
    {
        if (value is null || IsUnsetConfigValue(value)) return true;
        if (configName == "rclone.host"
            || IsMountTypeConfig(configName)
            || IsMountDirectoryConfig(configName)
            || IsRcloneOptionalCredentialConfig(configName))
            return string.IsNullOrWhiteSpace(value);
        return false;
    }

    private static string? NormalizeEndpointConfigOrNull(string? value)
    {
        if (value is null) return null;
        var normalized = value.Trim().TrimEnd('/');
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsMountDirectoryConfig(string configName)
    {
        return configName is "rclone.mount-dir" or "Mount:Directory" or "mount.directory";
    }

    private static bool IsMountTypeConfig(string configName)
    {
        return configName is "Mount:Type" or "mount.type";
    }

    private static string? NormalizeMountTypeOrNull(string? mountType)
    {
        if (mountType is null) return null;
        mountType = mountType.Trim().ToLowerInvariant();
        if (mountType.Length == 0) return null;
        return mountType is "rclone" or "dfs" or "none" ? mountType : "rclone";
    }

    private static bool IsRcloneOptionalCredentialConfig(string configName)
    {
        return configName is "rclone.user" or "rclone.pass";
    }

    private static string? NormalizeOptionalTextConfigOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsBooleanConfig(string configName)
    {
        return configName is
            "api.ensure-importable-video" or
            "webdav.show-hidden-files" or
            "queue.paused" or
            "usenet.adaptive-connections-enabled" or
            "webdav.enforce-readonly" or
            "webdav.preview-par2-files" or
            "api.ignore-history-limit" or
            "repair.enable" or
            "rclone.rc-enabled" or
            "db.is-startup-vacuum-enabled" or
            "api.nzb-backup-enabled" or
            "maintenance.remove-orphaned-schedule-enabled" or
            "Cache:Enabled" or
            "cache.enabled" or
            "usenet.nntp-pipelining.enabled";
    }

    private static bool IsIntegerConfig(string configName)
    {
        return configName is
            "usenet.max-download-connections" or
            "usenet.max-streaming-connections" or
            "usenet.max-total-streaming-connections" or
            "queue.max-concurrent-downloads" or
            "queue.max-concurrent-verify" or
            "queue.max-concurrent-repair" or
            "queue.file-processing-concurrency" or
            "usenet.article-buffer-size" or
            "usenet.streaming-priority" or
            "usenet.streaming-segment-timeout" or
            "usenet.streaming-segment-retries" or
            "repair.healthcheck-concurrency" or
            "repair.connection-budget-percent" or
            "usenet.connection-idle-timeout-seconds" or
            "usenet.nntp-pipelining.depth" or
            "maintenance.remove-orphaned-schedule-time";
    }

    private static bool IsLongConfig(string configName)
    {
        return configName is
            "usenet.article-cache-max-megabytes" or
            "Cache:MaxBytes" or
            "cache.max-bytes" or
            "Cache:ChunkBytes" or
            "cache.chunk-bytes" or
            "Cache:ReadAheadBytes" or
            "cache.read-ahead-bytes";
    }

    private static bool IsJsonConfig(string configName)
    {
        return configName is "usenet.providers" or "arr.instances";
    }

    private static bool AreJsonValuesEquivalent(string existingValue, string newValue)
    {
        try
        {
            using var existingJson = JsonDocument.Parse(existingValue);
            using var newJson = JsonDocument.Parse(newValue);
            return JsonElementsEqual(existingJson.RootElement, newJson.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;

        return left.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectsEqual(left, right),
            JsonValueKind.Array => JsonArraysEqual(left, right),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static bool JsonObjectsEqual(JsonElement left, JsonElement right)
    {
        var rightProperties = right.EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        var leftCount = 0;
        foreach (var leftProperty in left.EnumerateObject())
        {
            leftCount++;
            if (!rightProperties.TryGetValue(leftProperty.Name, out var rightValue))
                return false;
            if (!JsonElementsEqual(leftProperty.Value, rightValue))
                return false;
        }

        return leftCount == rightProperties.Count;
    }

    private static bool JsonArraysEqual(JsonElement left, JsonElement right)
    {
        var leftArray = left.EnumerateArray();
        var rightArray = right.EnumerateArray();
        using var leftEnumerator = leftArray.GetEnumerator();
        using var rightEnumerator = rightArray.GetEnumerator();

        while (true)
        {
            var leftHasNext = leftEnumerator.MoveNext();
            var rightHasNext = rightEnumerator.MoveNext();
            if (leftHasNext != rightHasNext) return false;
            if (!leftHasNext) return true;
            if (!JsonElementsEqual(leftEnumerator.Current, rightEnumerator.Current)) return false;
        }
    }

    public string GetRcloneMountDir()
    {
        return NormalizeMountDirOrNull(GetConfigValue("rclone.mount-dir"))
               ?? NormalizeMountDirOrNull(EnvironmentUtil.GetVariable("MOUNT_DIR"))
               ?? "/mnt/nzbdav";
    }

    public string GetMountType()
    {
        var mountType = GetFirstConfigValue("Mount:Type", "mount.type")
                        ?? EnvironmentUtil.GetVariable("MOUNT_TYPE")
                        ?? "rclone";
        mountType = mountType.Trim().ToLowerInvariant();
        return mountType is "rclone" or "dfs" or "none" ? mountType : "rclone";
    }

    public string GetMountDir()
    {
        return NormalizeMountDirOrNull(GetFirstConfigValue("Mount:Directory", "mount.directory"))
               ?? NormalizeMountDirOrNull(EnvironmentUtil.GetVariable("MOUNT_DIRECTORY"))
               ?? GetRcloneMountDir();
    }

    private static string? NormalizeMountDirOrNull(string? mountDir)
    {
        if (mountDir is null) return null;
        var normalized = NormalizeMountDir(mountDir);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeMountDir(string mountDir)
    {
        mountDir = mountDir.Trim();
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
        return GetBoolConfigValue("api.ensure-importable-video", true);
    }

    public bool ShowHiddenWebdavFiles()
    {
        return GetBoolConfigValue("webdav.show-hidden-files", false);
    }

    public string? GetLibraryDir()
    {
        return GetConfigValue("media.library-dir");
    }

    public int GetMaxDownloadConnections()
    {
        var providerFallback = GetUsenetProviderConfig().TotalPooledConnections;
        var value = GetIntConfigValue(
            "usenet.max-download-connections",
            providerFallback);
        return value > 0 ? value : providerFallback;
    }

    public bool IsQueuePaused()
    {
        return GetBoolConfigValue("queue.paused", false);
    }

    public bool IsAdaptiveConnectionCountEnabled()
    {
        return GetBoolConfigValue("usenet.adaptive-connections-enabled", false);
    }

    public int GetAdaptiveMaxDownloadConnections()
    {
        var manualFallback = Math.Max(1, GetMaxDownloadConnections());
        if (!IsAdaptiveConnectionCountEnabled()) return manualFallback;

        return ApplyRuntimePressureLimit(GetAutomaticDownloadConnectionBudget());
    }

    public int GetAdaptiveMaxStreamingConnections()
    {
        var maxStreamingConnections = Math.Max(1, GetMaxStreamingConnections());
        return ApplyRuntimePressureLimit(maxStreamingConnections);
    }

    public int GetMaxStreamingConnections()
    {
        var configValue = GetIntConfigValue("usenet.max-streaming-connections", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxStreamingConnectionsPerStream);

        var articleBufferSize = Math.Max(1, GetArticleBufferSize());
        return Math.Min(articleBufferSize, GetAutomaticDownloadConnectionBudget());
    }

    public int GetMaxTotalStreamingConnections()
    {
        var configValue = GetIntConfigValue("usenet.max-total-streaming-connections", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxTotalStreamingConnections);

        var perStreamConnections = Math.Max(1, GetMaxStreamingConnections());
        var cpuBasedLimit = GetStreamingCpuConcurrencyLimit();
        var downloadConnections = GetAutomaticDownloadConnectionBudget();
        var activeStreamFanoutLimit = perStreamConnections * Math.Max(2, Environment.ProcessorCount);
        return Math.Clamp(
            Math.Min(Math.Min(activeStreamFanoutLimit, cpuBasedLimit), downloadConnections),
            1,
            MaxTotalStreamingConnections
        );
    }

    public int GetAdaptiveMaxTotalStreamingConnections()
    {
        var maxTotalStreamingConnections = GetMaxTotalStreamingConnections();
        return ApplyRuntimePressureLimit(maxTotalStreamingConnections);
    }

    public int GetMaxConcurrentQueueDownloads()
    {
        var configValue = GetIntConfigValue("queue.max-concurrent-downloads", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxManualQueueDownloads);

        return GetAutomaticMaxConcurrentQueueDownloads(GetMaxDownloadConnections());
    }

    public int GetAdaptiveMaxConcurrentQueueDownloads()
    {
        var maxConcurrentDownloads = GetMaxConcurrentQueueDownloads();
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(maxConcurrentDownloads)
            : maxConcurrentDownloads;
    }

    public int GetMaxConcurrentVerifyJobs()
    {
        var configValue = GetIntConfigValue("queue.max-concurrent-verify", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxManualWorkerJobsPerKind);

        return GetAutomaticVerifyItemConcurrency(GetAdaptiveHealthCheckConcurrency());
    }

    public int GetAdaptiveMaxConcurrentVerifyJobs()
    {
        var maxConcurrentVerifyJobs = GetMaxConcurrentVerifyJobs();
        return ApplyRuntimePressureLimit(maxConcurrentVerifyJobs);
    }

    public int GetMaxConcurrentRepairJobs()
    {
        var configValue = GetIntConfigValue("queue.max-concurrent-repair", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxManualWorkerJobsPerKind);

        return GetAutomaticRepairItemConcurrency(GetAdaptiveHealthCheckConcurrency());
    }

    public int GetAdaptiveMaxConcurrentRepairJobs()
    {
        var maxConcurrentRepairJobs = GetMaxConcurrentRepairJobs();
        return ApplyRuntimePressureLimit(maxConcurrentRepairJobs);
    }

    public int GetQueueFileProcessingConcurrency()
    {
        var configValue = GetIntConfigValue("queue.file-processing-concurrency", 0);
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxQueueFileProcessingConcurrency);

        return GetAutomaticQueueFileProcessingConcurrency(GetMaxDownloadConnections());
    }

    public int GetAdaptiveQueueFileProcessingConcurrency()
    {
        return IsAdaptiveConnectionCountEnabled()
            ? GetAutomaticQueueFileProcessingConcurrency(GetAdaptiveMaxDownloadConnections())
            : GetQueueFileProcessingConcurrency();
    }

    public long GetArticleCacheMaxBytes()
    {
        var megabytes = GetLongConfigValue("usenet.article-cache-max-megabytes", 256);
        return Math.Max(1, megabytes) * 1024 * 1024;
    }

    public long GetArticleCacheMaxBytesPerQueueWorker()
    {
        var totalBudget = GetArticleCacheMaxBytes();
        var workers = Math.Max(1, GetAdaptiveMaxConcurrentQueueDownloads());
        return Math.Max(1, totalBudget / workers);
    }

    public int GetArticleBufferSize()
    {
        return GetPositiveIntConfigValue("usenet.article-buffer-size", 8);
    }

    public int GetAdaptiveArticleBufferSize()
    {
        return ApplyRuntimePressureLimit(Math.Max(1, GetArticleBufferSize()));
    }

    public SparseSegmentCacheOptions GetSparseSegmentCacheOptions()
    {
        return new SparseSegmentCacheOptions
        {
            Enabled = GetBoolConfig(true, "Cache:Enabled", "cache.enabled"),
            Directory = GetFirstConfigValue("Cache:Directory", "cache.directory") ?? "/config/cache/segments",
            MaxBytes = GetLongConfig(64L * 1024 * 1024 * 1024, "Cache:MaxBytes", "cache.max-bytes"),
            ChunkBytes = (int)Math.Clamp(
                GetLongConfig(4L * 1024 * 1024, "Cache:ChunkBytes", "cache.chunk-bytes"),
                64L * 1024,
                64L * 1024 * 1024),
            ReadAheadBytes = (int)Math.Clamp(
                GetLongConfig(8L * 1024 * 1024, "Cache:ReadAheadBytes", "cache.read-ahead-bytes"),
                0,
                512L * 1024 * 1024),
            IdleTtl = GetTimeSpanConfig(TimeSpan.FromMinutes(10), "Cache:IdleTtl", "cache.idle-ttl"),
            NoProgressTimeout = GetTimeSpanConfig(TimeSpan.FromMinutes(2), "Cache:NoProgressTimeout", "cache.no-progress-timeout"),
        };
    }

    private bool GetBoolConfig(bool defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private long GetLongConfig(long defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private TimeSpan GetTimeSpanConfig(TimeSpan defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        if (raw == null) return defaultValue;
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : defaultValue;
    }

    private int ApplyRuntimePressureLimit(int configuredLimit)
    {
        var multiplier = GetRuntimePressureSnapshot().EffectiveMultiplier;
        return Math.Max(1, (int)Math.Floor(configuredLimit * multiplier));
    }

    public RuntimePressureSnapshot GetRuntimePressureSnapshot()
    {
        var cpuPressure = GetProcessCpuPressure();
        var memoryMultiplier = GetMemoryPressureMultiplier();
        var threadPoolMultiplier = GetThreadPoolPressureMultiplier();
        var effectiveMultiplier = Math.Min(cpuPressure.Multiplier, Math.Min(memoryMultiplier, threadPoolMultiplier));
        return new RuntimePressureSnapshot(
            ProcessCpuCores: cpuPressure.ProcessCpuCores,
            CpuPressureMultiplier: cpuPressure.Multiplier,
            MemoryPressureMultiplier: memoryMultiplier,
            ThreadPoolPressureMultiplier: threadPoolMultiplier,
            EffectiveMultiplier: effectiveMultiplier
        );
    }

    private (double ProcessCpuCores, double Multiplier) GetProcessCpuPressure()
    {
        lock (_runtimePressureLock)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastCpuSampleAt;
            if (elapsed < CpuSampleInterval)
                return (_lastProcessCpuCores, _lastCpuPressureMultiplier);

            var cpuUsageSeconds = GetCurrentCpuUsageSeconds();
            var cpuSeconds = Math.Max(0, cpuUsageSeconds - _lastCpuUsageSeconds);
            var processCpuCores = elapsed.TotalSeconds > 0
                ? cpuSeconds / elapsed.TotalSeconds
                : _lastProcessCpuCores;

            _lastCpuSampleAt = now;
            _lastCpuUsageSeconds = cpuUsageSeconds;
            _lastProcessCpuCores = processCpuCores;
            _lastCpuPressureMultiplier = GetCpuPressureMultiplier(processCpuCores);
            return (_lastProcessCpuCores, _lastCpuPressureMultiplier);
        }
    }

    private static double GetCurrentCpuUsageSeconds()
    {
        return TryReadCgroupCpuUsageSeconds(CgroupCpuStatPath) ?? GetCurrentProcessCpuUsageSeconds();
    }

    private static double GetCurrentProcessCpuUsageSeconds()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.TotalSeconds;
    }

    public static double? TryReadCgroupCpuUsageSeconds(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("usage_usec ", StringComparison.Ordinal)) continue;
                var value = line["usage_usec ".Length..].Trim();
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usageUsec)
                    ? Math.Max(0, usageUsec) / 1_000_000d
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        return null;
    }

    public static double GetCpuPressureMultiplier(double processCpuCores)
    {
        var targetCores = GetAdaptiveCpuTargetCores();
        var ratio = processCpuCores / targetCores;
        return ratio switch
        {
            >= 3.00 => 0.25,
            >= 2.00 => 0.35,
            >= 1.15 => 0.50,
            >= 1.00 => 0.60,
            >= 0.80 => 0.75,
            >= 0.65 => 0.85,
            _ => 1.00
        };
    }

    private static double GetAdaptiveCpuTargetCores()
    {
        var value = EnvironmentUtil.GetVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES");
        if (double.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            return Math.Clamp(parsed, 0.25, Math.Max(0.25, Environment.ProcessorCount));

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        if (processorCount == 1) return 0.75;

        return Math.Min(1.75, processorCount);
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

    public sealed record RuntimePressureSnapshot(
        double ProcessCpuCores,
        double CpuPressureMultiplier,
        double MemoryPressureMultiplier,
        double ThreadPoolPressureMultiplier,
        double EffectiveMultiplier
    );

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var numericalValue = GetIntConfigValue("usenet.streaming-priority", 80);
        if (numericalValue < 0) numericalValue = 80;
        return new SemaphorePriorityOdds() { HighPriorityOdds = Math.Clamp(numericalValue, 0, 100) };
    }

    public TimeSpan GetStreamingSegmentTimeout()
    {
        var seconds = GetPositiveIntConfigValue("usenet.streaming-segment-timeout", 8);
        return TimeSpan.FromSeconds(seconds);
    }

    public int GetStreamingSegmentRetries()
    {
        var retries = GetIntConfigValue("usenet.streaming-segment-retries", 3);
        return retries >= 0 ? retries : 3;
    }

    public int GetHealthCheckConcurrency()
    {
        return GetPositiveIntConfigValue("repair.healthcheck-concurrency", 50);
    }

    public int GetAdaptiveHealthCheckConcurrency()
    {
        var automaticConcurrency = Math.Min(
            GetHealthCheckCpuConcurrencyLimit(),
            Math.Min(GetAutomaticDownloadConnectionBudget(), GetRepairConnectionBudget()));
        var healthCheckConcurrency = IsAdaptiveConnectionCountEnabled()
            ? automaticConcurrency
            : Math.Min(Math.Max(1, GetHealthCheckConcurrency()), automaticConcurrency);

        return ApplyRuntimePressureLimit(healthCheckConcurrency);
    }

    public int GetAdaptivePostDownloadVerificationConcurrency()
    {
        var automaticConcurrency = Math.Clamp(
            GetPostDownloadVerificationConnectionBudget(),
            1,
            MaxPostDownloadVerificationConcurrency);
        return ApplyRuntimePressureLimit(automaticConcurrency);
    }

    public int GetRepairConnectionBudget()
    {
        var rawPercent = GetIntConfigValue("repair.connection-budget-percent", 20);
        var percent = Math.Clamp(rawPercent, 1, 100);
        var totalConnections = Math.Max(1, GetAutomaticDownloadConnectionBudget());
        return Math.Clamp(
            (int)Math.Ceiling(totalConnections * (percent / 100d)),
            1,
            totalConnections
        );
    }

    private static int GetAutomaticMaxConcurrentQueueDownloads(int downloadConnections)
    {
        var connectionBased = (Math.Max(1, downloadConnections) + 24) / 25;
        var coreBased = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(Math.Min(connectionBased, coreBased), 1, MaxAutoQueueDownloads);
    }

    private static int GetAutomaticVerifyItemConcurrency(int segmentConcurrency)
    {
        var workerCeiling = Math.Clamp(Environment.ProcessorCount, 1, MaxAutoQueueDownloads);
        return Math.Clamp((Math.Max(1, segmentConcurrency) + 1) / 2, 1, workerCeiling);
    }

    private static int GetAutomaticRepairItemConcurrency(int segmentConcurrency)
    {
        return Math.Clamp(Math.Max(1, segmentConcurrency) / 2, 1, 4);
    }

    private static int GetAutomaticQueueFileProcessingConcurrency(int downloadConnections)
    {
        return Math.Clamp(Math.Max(1, downloadConnections), 1, MaxQueueFileProcessingConcurrency);
    }

    private int GetAutomaticDownloadConnectionBudget()
    {
        var configuredProviderConnections = GetUsenetProviderConfig().ConfiguredPooledConnections;
        if (IsAdaptiveConnectionCountEnabled() && configuredProviderConnections > 0)
            return configuredProviderConnections;

        return Math.Max(1, GetMaxDownloadConnections());
    }

    private int GetPostDownloadVerificationConnectionBudget()
    {
        var configuredProviderConnections = GetUsenetProviderConfig().ConfiguredPooledConnections;
        return configuredProviderConnections > 0
            ? configuredProviderConnections
            : Math.Max(1, GetMaxDownloadConnections());
    }

    private static int GetHealthCheckCpuConcurrencyLimit()
    {
        return Math.Clamp(Environment.ProcessorCount * 2, 2, MaxHealthCheckConcurrency);
    }

    private static int GetStreamingCpuConcurrencyLimit()
    {
        return Math.Clamp(Environment.ProcessorCount * 4, 4, 64);
    }

    public int GetConnectionIdleTimeoutSeconds()
    {
        return GetPositiveIntConfigValue("usenet.connection-idle-timeout-seconds", 120);
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        return GetBoolConfigValue("webdav.enforce-readonly", true);
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
        return GetBoolConfigValue("usenet.nntp-pipelining.enabled", true);
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
        var depth = GetIntConfigValue("usenet.nntp-pipelining.depth", defaultValue);
        return Math.Clamp(depth, 1, 150);
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        return GetBoolConfigValue("webdav.preview-par2-files", false);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        return GetBoolConfigValue("api.ignore-history-limit", true);
    }

    public bool IsRepairJobEnabled()
    {
        var isRepairJobEnabled = GetBoolConfigValue("repair.enable", false);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public ArrConfig.PrioritizationOptions GetArrPrioritizationOptions()
    {
        var options = GetArrConfig().Prioritization ?? new ArrConfig.PrioritizationOptions();
        options.Mode = NormalizeArrMode(options.Mode);
        options.RecomputeIntervalSeconds = Math.Clamp(options.RecomputeIntervalSeconds, 30, 3600);
        options.MaxAutomaticPriority = Math.Clamp(
            options.MaxAutomaticPriority,
            (int)QueueItem.PriorityOption.Low,
            (int)QueueItem.PriorityOption.High);
        return options;
    }

    public ArrConfig.SearchNudgeOptions GetArrSearchNudgeOptions()
    {
        var options = GetArrConfig().SearchNudge ?? new ArrConfig.SearchNudgeOptions();
        options.Mode = NormalizeArrMode(options.Mode);
        options.IntervalSeconds = Math.Clamp(options.IntervalSeconds, 300, 86400);
        options.CooldownSeconds = Math.Clamp(options.CooldownSeconds, 300, 604800);
        options.MaxCommandsPerHour = Math.Clamp(options.MaxCommandsPerHour, 1, 200);
        options.SonarrBatchSize = Math.Clamp(options.SonarrBatchSize, 1, 100);
        options.RadarrBatchSize = Math.Clamp(options.RadarrBatchSize, 1, 50);
        options.ConcurrentCommandsPerInstance = Math.Clamp(options.ConcurrentCommandsPerInstance, 1, 4);
        return options;
    }

    private static string NormalizeArrMode(string? mode)
    {
        mode = mode?.Trim().ToLowerInvariant();
        return mode is "apply" ? "apply" : "report";
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        var configValue = ParseUsenetProviderConfig(GetConfigValue("usenet.providers"));
        if (configValue != null) return configValue;

        var envValue = EnvironmentUtil.GetVariable("NZBDAV_USENET_PROVIDERS_JSON");
        return ParseUsenetProviderConfig(envValue) ?? defaultValue;
    }

    private static UsenetProviderConfig? ParseUsenetProviderConfig(string? rawValue)
    {
        if (rawValue == null) return null;
        try
        {
            using var document = JsonDocument.Parse(rawValue);
            var root = document.RootElement;
            var providerElements = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToArray()
                : TryGetProperty(root, out var providersProperty, "Providers", "providers")
                    && providersProperty.ValueKind == JsonValueKind.Array
                    ? providersProperty.EnumerateArray().ToArray()
                    : null;
            if (providerElements == null) return null;

            return new UsenetProviderConfig
            {
                Providers = providerElements
                    .Select(ParseUsenetProvider)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToList()
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static UsenetProviderConfig.ConnectionDetails? ParseUsenetProvider(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var host = TryGetString(element, out var parsedHost, "Host", "host")
            ? parsedHost.Trim()
            : "";
        var user = TryGetString(element, out var parsedUser, "User", "user", "Username", "username")
            ? parsedUser
            : "";
        var pass = TryGetString(element, out var parsedPass, "Pass", "pass", "Password", "password")
            ? parsedPass
            : "";
        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(user)
            || string.IsNullOrWhiteSpace(pass))
            return null;

        var port = TryGetInt(element, out var parsedPort, "Port", "port")
            ? parsedPort
            : 563;
        if (port <= 0) return null;

        var maxConnections = TryGetInt(
                element,
                out var parsedMaxConnections,
                "MaxConnections",
                "maxConnections",
                "max_connections",
                "connections")
            ? parsedMaxConnections
            : 50;
        if (maxConnections <= 0) return null;

        var useSsl = TryGetBool(element, out var parsedUseSsl, "UseSsl", "useSsl", "use_ssl", "ssl")
            ? parsedUseSsl
            : UsenetProviderConfig.ConnectionDetails.IsImplicitTlsPort(port);
        var priority = TryGetInt(element, out var parsedPriority, "Priority", "priority")
            ? parsedPriority
            : 100;
        var statPipeliningEnabled = TryGetNullableBool(
            element,
            "StatPipeliningEnabled",
            "statPipeliningEnabled",
            "stat_pipelining_enabled");

        return new UsenetProviderConfig.ConnectionDetails
        {
            Type = TryGetProviderType(element, out var providerType) ? providerType : ProviderType.Pooled,
            Host = host,
            Port = port,
            UseSsl = useSsl,
            User = user,
            Pass = pass,
            MaxConnections = maxConnections,
            Priority = priority,
            StatPipeliningEnabled = statPipeliningEnabled
        };
    }

    private static bool TryGetProviderType(JsonElement element, out ProviderType providerType)
    {
        providerType = ProviderType.Pooled;
        if (!TryGetProperty(element, out var property, "Type", "type")) return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue)
            && Enum.IsDefined(typeof(ProviderType), numericValue))
        {
            providerType = (ProviderType)numericValue;
            return true;
        }

        if (property.ValueKind != JsonValueKind.String) return false;
        var rawValue = property.GetString();
        if (Enum.TryParse<ProviderType>(rawValue, ignoreCase: true, out var parsed))
        {
            providerType = parsed;
            return true;
        }

        var normalized = rawValue?.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out providerType);
    }

    private static bool TryGetString(JsonElement element, out string value, params string[] propertyNames)
    {
        value = "";
        if (!TryGetProperty(element, out var property, propertyNames)) return false;
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number
            || property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.ToString();
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement element, out int value, params string[] propertyNames)
    {
        value = 0;
        if (!TryGetProperty(element, out var property, propertyNames)) return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt32(out value);

        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetBool(JsonElement element, out bool value, params string[] propertyNames)
    {
        value = false;
        if (!TryGetProperty(element, out var property, propertyNames)) return false;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && bool.TryParse(property.GetString(), out value);
    }

    private static bool? TryGetNullableBool(JsonElement element, params string[] propertyNames)
    {
        return TryGetBool(element, out var value, propertyNames) ? value : null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement property, params string[] propertyNames)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (propertyNames.Any(propertyName =>
                    string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    public string GetDuplicateNzbBehavior()
    {
        return ArrOperationsService.NormalizeDuplicateBehavior(GetConfigValue("api.duplicate-nzb-behavior"));
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

    public string GetSymlinkTargetMode()
    {
        var value = GetFirstConfigValue("api.symlink-target-mode", "rclone.symlink-target-mode")
                    ?? EnvironmentUtil.GetVariable("NZBDAV_SYMLINK_TARGET_MODE")
                    ?? "absolute";
        value = value.Trim().ToLowerInvariant();
        return value == "relative" ? "relative" : "absolute";
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
        return GetBoolConfigValue("rclone.rc-enabled", false);
    }

    public string? GetRcloneHost()
    {
        return NormalizeEndpointConfigOrNull(GetConfigValue("rclone.host"));
    }

    public string? GetRcloneUser()
    {
        return NormalizeOptionalTextConfigOrNull(GetConfigValue("rclone.user"));
    }

    public string? GetRclonePass()
    {
        return NormalizeOptionalTextConfigOrNull(GetConfigValue("rclone.pass"));
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
        return GetBoolConfigValue("db.is-startup-vacuum-enabled", false);
    }

    public bool IsNzbBackupEnabled()
    {
        return GetBoolConfigValue("api.nzb-backup-enabled", false);
    }

    public string? GetNzbBackupLocation()
    {
        return GetConfigValue("api.nzb-backup-location");
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        return GetBoolConfigValue("maintenance.remove-orphaned-schedule-enabled", false);
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

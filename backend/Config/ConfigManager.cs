using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
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
    private const string CgroupCpuStatPath = "/sys/fs/cgroup/cpu.stat";
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromSeconds(5);

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
        return NormalizeMountDir(mountDir);
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
        var mountDir = GetFirstConfigValue("Mount:Directory", "mount.directory")
                       ?? EnvironmentUtil.GetVariable("MOUNT_DIRECTORY")
                       ?? GetRcloneMountDir();
        return NormalizeMountDir(mountDir);
    }

    private static string NormalizeMountDir(string mountDir)
    {
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
            ?? GetUsenetProviderConfig().TotalPooledConnections.ToString(CultureInfo.InvariantCulture)
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
        var manualFallback = Math.Max(1, GetMaxDownloadConnections());
        if (!IsAdaptiveConnectionCountEnabled()) return manualFallback;

        return ApplyRuntimePressureLimit(GetAutomaticDownloadConnectionBudget());
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
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxStreamingConnectionsPerStream);

        var articleBufferSize = Math.Max(1, GetArticleBufferSize());
        return Math.Min(articleBufferSize, GetAutomaticDownloadConnectionBudget());
    }

    public int GetMaxTotalStreamingConnections()
    {
        var configValue = int.Parse(GetConfigValue("usenet.max-total-streaming-connections") ?? "0");
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
        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(maxTotalStreamingConnections)
            : maxTotalStreamingConnections;
    }

    public int GetMaxConcurrentQueueDownloads()
    {
        var configValue = int.Parse(GetConfigValue("queue.max-concurrent-downloads") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxManualQueueDownloads);

        return GetAutomaticMaxConcurrentQueueDownloads(GetMaxDownloadConnections());
    }

    public int GetAdaptiveMaxConcurrentQueueDownloads()
    {
        var configuredMax = int.Parse(GetConfigValue("queue.max-concurrent-downloads") ?? "0");
        if (!IsAdaptiveConnectionCountEnabled() || configuredMax > 0) return GetMaxConcurrentQueueDownloads();

        return ApplyRuntimePressureLimit(GetAutomaticMaxConcurrentQueueDownloads(GetAdaptiveMaxDownloadConnections()));
    }

    public int GetMaxConcurrentVerifyJobs()
    {
        var configValue = int.Parse(GetConfigValue("queue.max-concurrent-verify") ?? "0");
        if (configValue > 0)
            return Math.Min(
                Math.Clamp(configValue, 1, MaxManualWorkerJobsPerKind),
                Math.Max(1, GetAdaptiveHealthCheckConcurrency()));

        return GetAutomaticHealthCheckItemConcurrency(GetAdaptiveHealthCheckConcurrency());
    }

    public int GetAdaptiveMaxConcurrentVerifyJobs()
    {
        var configuredMax = int.Parse(GetConfigValue("queue.max-concurrent-verify") ?? "0");
        if (!IsAdaptiveConnectionCountEnabled() || configuredMax > 0) return GetMaxConcurrentVerifyJobs();

        return ApplyRuntimePressureLimit(GetAutomaticHealthCheckItemConcurrency(GetAdaptiveHealthCheckConcurrency()));
    }

    public int GetMaxConcurrentRepairJobs()
    {
        var configValue = int.Parse(GetConfigValue("queue.max-concurrent-repair") ?? "0");
        if (configValue > 0) return Math.Clamp(configValue, 1, MaxManualWorkerJobsPerKind);

        return GetAutomaticHealthCheckItemConcurrency(GetAdaptiveHealthCheckConcurrency());
    }

    public int GetAdaptiveMaxConcurrentRepairJobs()
    {
        var configuredMax = int.Parse(GetConfigValue("queue.max-concurrent-repair") ?? "0");
        if (!IsAdaptiveConnectionCountEnabled() || configuredMax > 0) return GetMaxConcurrentRepairJobs();

        return ApplyRuntimePressureLimit(GetAutomaticHealthCheckItemConcurrency(GetAdaptiveHealthCheckConcurrency()));
    }

    public int GetQueueFileProcessingConcurrency()
    {
        var configValue = int.Parse(GetConfigValue("queue.file-processing-concurrency") ?? "0");
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
        var megabytes = long.Parse(
            GetConfigValue("usenet.article-cache-max-megabytes")
            ?? "256"
        );
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
        return int.Parse(
            GetConfigValue("usenet.article-buffer-size")
            ?? "8"
        );
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
            NoProgressTimeout = GetTimeSpanConfig(TimeSpan.FromSeconds(30), "Cache:NoProgressTimeout", "cache.no-progress-timeout"),
        };
    }

    private bool GetBoolConfig(bool defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        return raw == null ? defaultValue : bool.Parse(raw);
    }

    private long GetLongConfig(long defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        return raw == null ? defaultValue : long.Parse(raw, CultureInfo.InvariantCulture);
    }

    private TimeSpan GetTimeSpanConfig(TimeSpan defaultValue, params string[] names)
    {
        var raw = GetFirstConfigValue(names);
        if (raw == null) return defaultValue;
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return TimeSpan.FromSeconds(double.Parse(raw, CultureInfo.InvariantCulture));
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

        return Math.Clamp(processorCount * 0.75, 2.0, processorCount);
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
        var automaticConcurrency = Math.Min(
            GetHealthCheckCpuConcurrencyLimit(),
            Math.Min(GetAutomaticDownloadConnectionBudget(), GetRepairConnectionBudget()));
        var healthCheckConcurrency = IsAdaptiveConnectionCountEnabled()
            ? automaticConcurrency
            : Math.Min(Math.Max(1, GetHealthCheckConcurrency()), automaticConcurrency);

        return IsAdaptiveConnectionCountEnabled()
            ? ApplyRuntimePressureLimit(healthCheckConcurrency)
            : healthCheckConcurrency;
    }

    public int GetRepairConnectionBudget()
    {
        var rawPercent = int.Parse(GetConfigValue("repair.connection-budget-percent") ?? "20");
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

    private static int GetAutomaticHealthCheckItemConcurrency(int segmentConcurrency)
    {
        return Math.Clamp(segmentConcurrency / 2, 1, 4);
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
        var configValue = GetConfigValue<UsenetProviderConfig>("usenet.providers");
        if (configValue != null) return configValue;

        var envValue = EnvironmentUtil.GetVariable("NZBDAV_USENET_PROVIDERS_JSON");
        return envValue == null ? defaultValue : JsonSerializer.Deserialize<UsenetProviderConfig>(envValue) ?? defaultValue;
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

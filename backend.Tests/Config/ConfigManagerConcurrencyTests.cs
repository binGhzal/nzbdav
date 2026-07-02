using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public class ConfigManagerConcurrencyTests
{
    [Fact]
    public void AutoQueueFileProcessingConcurrencyCanUseAllDownloadConnections()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.max-concurrent-downloads", "0"),
            ("queue.file-processing-concurrency", "0")
        );

        var concurrency = configManager.GetQueueFileProcessingConcurrency();

        Assert.Equal(configManager.GetMaxDownloadConnections(), concurrency);
    }

    [Fact]
    public void ExplicitQueueFileProcessingConcurrencyIsPreservedWithinSafetyLimit()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.file-processing-concurrency", "128")
        );

        Assert.Equal(128, configManager.GetQueueFileProcessingConcurrency());
    }

    [Fact]
    public void ExplicitQueueFileProcessingConcurrencyCannotExceedSafetyLimit()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "400"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.file-processing-concurrency", "512")
        );

        Assert.Equal(256, configManager.GetQueueFileProcessingConcurrency());
    }

    [Fact]
    public void AdaptiveQueueSizingIgnoresLowManualFallbacks()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("queue.max-concurrent-downloads", "1"),
            ("queue.file-processing-concurrency", "2")
        );

        Assert.Equal(200, configManager.GetAdaptiveMaxDownloadConnections());
        Assert.Equal(4, configManager.GetAdaptiveMaxConcurrentQueueDownloads());
        Assert.Equal(200, configManager.GetAdaptiveQueueFileProcessingConcurrency());
    }

    [Fact]
    public void HealthCheckConcurrencyIsCpuBoundInsteadOfDownloadConnectionBound()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200")
        );

        var concurrency = configManager.GetAdaptiveHealthCheckConcurrency();

        Assert.True(concurrency < configManager.GetMaxDownloadConnections());
        Assert.InRange(concurrency, 1, 64);
    }

    [Fact]
    public void HealthCheckConcurrencyDoesNotExceedDownloadConnections()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200")
        );

        Assert.Equal(8, configManager.GetAdaptiveHealthCheckConcurrency());
    }

    [Fact]
    public void AutoTotalStreamingConnectionsAreCpuBoundInsteadOfPerStreamMultiplied()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.article-buffer-size", "40"),
            ("usenet.max-streaming-connections", "0"),
            ("usenet.max-total-streaming-connections", "0")
        );

        var totalStreamingConnections = configManager.GetMaxTotalStreamingConnections();

        Assert.True(totalStreamingConnections < configManager.GetMaxStreamingConnections());
        Assert.InRange(totalStreamingConnections, 1, 8);
    }

    [Fact]
    public void ExplicitTotalStreamingConnectionsAreClamped()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.max-total-streaming-connections", "200")
        );

        Assert.Equal(128, configManager.GetMaxTotalStreamingConnections());
    }

    [Fact]
    public void CpuPressureMultiplierBacksOffSustainedBackendCpuBurn()
    {
        var original = Environment.GetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES");
        try
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", "1");

            Assert.Equal(1.00, ConfigManager.GetCpuPressureMultiplier(0.50));
            Assert.Equal(0.50, ConfigManager.GetCpuPressureMultiplier(1.20));
            Assert.Equal(0.35, ConfigManager.GetCpuPressureMultiplier(2.10));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", original);
        }
    }

    [Fact]
    public void CgroupCpuUsageParserReadsUsageUsec()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nzbdav-cpu-{Guid.NewGuid():N}.stat");
        try
        {
            File.WriteAllText(path, "usage_usec 1250000\nuser_usec 900000\nsystem_usec 350000\n");

            Assert.Equal(1.25, ConfigManager.TryReadCgroupCpuUsageSeconds(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CgroupCpuUsageParserIgnoresMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nzbdav-missing-cpu-{Guid.NewGuid():N}.stat");

        Assert.Null(ConfigManager.TryReadCgroupCpuUsageSeconds(path));
    }

    private static ConfigManager CreateConfigManager(params (string Name, string Value)[] values)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(values
            .Select(x => new ConfigItem { ConfigName = x.Name, ConfigValue = x.Value })
            .ToList());
        return configManager;
    }

    private static string CreateProviderConfig(params int[] maxConnections)
    {
        var providers = maxConnections.Select((maxConnection, index) => new UsenetProviderConfig.ConnectionDetails
        {
            Type = ProviderType.Pooled,
            Host = $"news{index}.example.invalid",
            Port = 563,
            UseSsl = true,
            User = "user",
            Pass = "pass",
            MaxConnections = maxConnection,
        });

        return System.Text.Json.JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers = providers.ToList(),
        });
    }
}

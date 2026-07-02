using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ConfigManagerConcurrencyTests
{
    [Fact]
    public void AutoQueueFileProcessingConcurrencyIsCpuBoundInsteadOfDownloadConnectionBound()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.max-concurrent-downloads", "0"),
            ("queue.file-processing-concurrency", "0")
        );

        var concurrency = configManager.GetQueueFileProcessingConcurrency();

        Assert.True(concurrency < configManager.GetMaxDownloadConnections());
        Assert.InRange(concurrency, 1, 16);
    }

    [Fact]
    public void ExplicitQueueFileProcessingConcurrencyIsClamped()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.file-processing-concurrency", "128")
        );

        Assert.Equal(64, configManager.GetQueueFileProcessingConcurrency());
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

    private static ConfigManager CreateConfigManager(params (string Name, string Value)[] values)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(values
            .Select(x => new ConfigItem { ConfigName = x.Name, ConfigValue = x.Value })
            .ToList());
        return configManager;
    }
}

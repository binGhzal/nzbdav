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
    public void AdaptiveConnectionCountDefaultsToDisabled()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200")
        );

        Assert.False(configManager.IsAdaptiveConnectionCountEnabled());
        Assert.Equal(200, configManager.GetAdaptiveMaxDownloadConnections());
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
    public void AdaptiveQueueSizingHonorsExplicitDownloadWorkerCap()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("queue.max-concurrent-downloads", "1"),
            ("queue.file-processing-concurrency", "2")
        );

        Assert.Equal(200, configManager.GetAdaptiveMaxDownloadConnections());
        Assert.Equal(1, configManager.GetAdaptiveMaxConcurrentQueueDownloads());
        Assert.Equal(200, configManager.GetAdaptiveQueueFileProcessingConcurrency());
    }

    [Fact]
    public void AdaptiveDownloadConnectionsUseProviderCapacityWithSeparateDownloadWorkerCap()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("queue.max-concurrent-downloads", "1"),
            ("queue.file-processing-concurrency", "2")
        );

        Assert.Equal(200, configManager.GetAdaptiveMaxDownloadConnections());
        Assert.Equal(1, configManager.GetAdaptiveMaxConcurrentQueueDownloads());
        Assert.Equal(200, configManager.GetAdaptiveQueueFileProcessingConcurrency());
    }

    [Fact]
    public void AutomaticQueueWorkersScaleWithConnectionsAndCores()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.max-concurrent-downloads", "0")
        );

        Assert.Equal(GetExpectedAutomaticQueueWorkers(200), configManager.GetMaxConcurrentQueueDownloads());
    }

    [Fact]
    public void ExplicitQueueWorkersCanExceedOldWebUiLimit()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.max-concurrent-downloads", "40")
        );

        Assert.Equal(40, configManager.GetMaxConcurrentQueueDownloads());
    }

    [Fact]
    public void InvalidConnectionAndQueueLimitsFallBackWithoutThrowing()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "not-a-number"),
            ("usenet.providers", CreateProviderConfig(12)),
            ("usenet.max-streaming-connections", "bad"),
            ("usenet.max-total-streaming-connections", "bad"),
            ("queue.max-concurrent-downloads", "bad"),
            ("queue.max-concurrent-verify", "bad"),
            ("queue.max-concurrent-repair", "bad"),
            ("queue.file-processing-concurrency", "bad"),
            ("repair.healthcheck-concurrency", "bad"),
            ("repair.connection-budget-percent", "bad")
        );

        Assert.Equal(12, configManager.GetMaxDownloadConnections());
        Assert.InRange(configManager.GetMaxStreamingConnections(), 1, 12);
        Assert.InRange(configManager.GetMaxTotalStreamingConnections(), 1, 12);
        Assert.Equal(GetExpectedAutomaticQueueWorkers(12), configManager.GetMaxConcurrentQueueDownloads());
        Assert.InRange(configManager.GetAdaptiveMaxConcurrentVerifyJobs(), 1, 4);
        Assert.InRange(configManager.GetAdaptiveMaxConcurrentRepairJobs(), 1, 4);
        Assert.Equal(12, configManager.GetQueueFileProcessingConcurrency());
        Assert.InRange(configManager.GetAdaptiveHealthCheckConcurrency(), 1, 12);
        Assert.Equal(3, configManager.GetRepairConnectionBudget());
    }

    [Fact]
    public void NegativeRuntimeNumericConfigUsesSafePositiveFallbacks()
    {
        var configManager = CreateConfigManager(
            ("usenet.providers", CreateProviderConfig(12)),
            ("usenet.max-download-connections", "-50"),
            ("usenet.article-buffer-size", "-8"),
            ("usenet.streaming-priority", "-10"),
            ("usenet.streaming-segment-timeout", "-5"),
            ("usenet.streaming-segment-retries", "-2"),
            ("usenet.connection-idle-timeout-seconds", "-30")
        );

        Assert.Equal(12, configManager.GetMaxDownloadConnections());
        Assert.Equal(8, configManager.GetArticleBufferSize());
        Assert.Equal(80, configManager.GetStreamingPriority().HighPriorityOdds);
        Assert.Equal(TimeSpan.FromSeconds(8), configManager.GetStreamingSegmentTimeout());
        Assert.Equal(3, configManager.GetStreamingSegmentRetries());
        Assert.Equal(120, configManager.GetConnectionIdleTimeoutSeconds());
    }

    [Fact]
    public void RuntimePressureReducesStreamingFanoutWhenAdaptiveConnectionsAreDisabled()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.article-buffer-size", "8"),
            ("usenet.max-streaming-connections", "8"),
            ("usenet.max-total-streaming-connections", "64"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "100")
        );
        InjectCpuPressure(configManager, 0.50);

        Assert.Equal(4, configManager.GetAdaptiveArticleBufferSize());
        Assert.Equal(4, configManager.GetAdaptiveMaxStreamingConnections());
        Assert.Equal(32, configManager.GetAdaptiveMaxTotalStreamingConnections());
        Assert.Equal(100, configManager.GetAdaptivePostDownloadVerificationConcurrency());
        Assert.Equal(
            Math.Max(1, (int)Math.Floor(Math.Min(Math.Clamp(Environment.ProcessorCount * 2, 2, 64), 200) * 0.50)),
            configManager.GetAdaptiveHealthCheckConcurrency());
    }

    [Fact]
    public void AdaptiveArticleBufferIsCappedByPerStreamMemoryBudget()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.article-buffer-size", "400")
        );

        var expectedCeiling = Math.Clamp(Environment.ProcessorCount, 4, 32);

        Assert.Equal(400, configManager.GetArticleBufferSize());
        Assert.Equal(expectedCeiling, configManager.GetAdaptiveArticleBufferSize());
    }

    [Fact]
    public void RuntimePressureReducesCappedArticleBuffer()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.article-buffer-size", "400")
        );
        InjectCpuPressure(configManager, 0.50);

        var expectedCeiling = Math.Clamp(Environment.ProcessorCount, 4, 32);

        Assert.Equal(Math.Max(1, (int)Math.Floor(expectedCeiling * 0.50)), configManager.GetAdaptiveArticleBufferSize());
    }

    [Fact]
    public void StreamingPriorityCannotCreateNegativeLowPriorityOdds()
    {
        var configManager = CreateConfigManager(("usenet.streaming-priority", "250"));

        var priority = configManager.GetStreamingPriority();

        Assert.Equal(100, priority.HighPriorityOdds);
        Assert.Equal(0, priority.LowPriorityOdds);
    }

    [Fact]
    public void InvalidBooleanTogglesFallBackWithoutThrowing()
    {
        var configManager = CreateConfigManager(
            ("api.ensure-importable-video", "not-bool"),
            ("webdav.show-hidden-files", "not-bool"),
            ("queue.paused", "not-bool"),
            ("usenet.adaptive-connections-enabled", "not-bool"),
            ("webdav.enforce-readonly", "not-bool"),
            ("webdav.preview-par2-files", "not-bool"),
            ("api.ignore-history-limit", "not-bool"),
            ("repair.enable", "not-bool"),
            ("rclone.rc-enabled", "not-bool"),
            ("db.is-startup-vacuum-enabled", "not-bool"),
            ("api.nzb-backup-enabled", "not-bool"),
            ("maintenance.remove-orphaned-schedule-enabled", "not-bool")
        );

        Assert.True(configManager.IsEnsureImportableVideoEnabled());
        Assert.False(configManager.ShowHiddenWebdavFiles());
        Assert.False(configManager.IsQueuePaused());
        Assert.False(configManager.IsAdaptiveConnectionCountEnabled());
        Assert.True(configManager.IsEnforceReadonlyWebdavEnabled());
        Assert.False(configManager.IsPreviewPar2FilesEnabled());
        Assert.True(configManager.IsIgnoreSabHistoryLimitEnabled());
        Assert.False(configManager.IsRepairJobEnabled());
        Assert.False(configManager.IsRcloneRemoteControlEnabled());
        Assert.False(configManager.IsDatabaseStartupVacuumEnabled());
        Assert.False(configManager.IsNzbBackupEnabled());
        Assert.False(configManager.IsRemoveOrphanedFilesScheduleEnabled());
    }

    [Fact]
    public void InvalidJsonConfigFallsBackWithoutThrowing()
    {
        var configManager = CreateConfigManager(
            ("usenet.providers", "{not-json"),
            ("arr.instances", "{not-json"),
            ("usenet.max-download-connections", "not-a-number")
        );

        Assert.Empty(configManager.GetUsenetProviderConfig().Providers);
        Assert.Equal(1, configManager.GetMaxDownloadConnections());
        Assert.Equal(0, configManager.GetArrConfig().GetInstanceCount());
        Assert.False(configManager.IsRepairJobEnabled());
    }

    [Fact]
    public void InvalidProviderJsonEnvironmentConfigFallsBackWithoutThrowing()
    {
        var original = Environment.GetEnvironmentVariable("NZBDAV_USENET_PROVIDERS_JSON");
        try
        {
            Environment.SetEnvironmentVariable("NZBDAV_USENET_PROVIDERS_JSON", "{not-json");
            var configManager = CreateConfigManager();

            Assert.Empty(configManager.GetUsenetProviderConfig().Providers);
            Assert.Equal(1, configManager.GetMaxDownloadConnections());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_USENET_PROVIDERS_JSON", original);
        }
    }

    [Fact]
    public void ProviderEnvironmentJsonAcceptsEnvExampleAliases()
    {
        var original = Environment.GetEnvironmentVariable("NZBDAV_USENET_PROVIDERS_JSON");
        try
        {
            Environment.SetEnvironmentVariable(
                "NZBDAV_USENET_PROVIDERS_JSON",
                """
                [{"name":"primary","type":"pooled","host":"news.example.invalid","port":563,"useSsl":true,"username":"user","password":"pass","connections":50,"priority":0,"stat_pipelining_enabled":true}]
                """);
            var configManager = CreateConfigManager();

            var provider = Assert.Single(configManager.GetUsenetProviderConfig().Providers);
            Assert.Equal(ProviderType.Pooled, provider.Type);
            Assert.Equal("news.example.invalid", provider.Host);
            Assert.Equal(563, provider.Port);
            Assert.True(provider.UseSsl);
            Assert.Equal("user", provider.User);
            Assert.Equal("pass", provider.Pass);
            Assert.Equal(50, provider.MaxConnections);
            Assert.Equal(0, provider.Priority);
            Assert.True(provider.IsStatPipeliningEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_USENET_PROVIDERS_JSON", original);
        }
    }

    [Fact]
    public void ProviderDatabaseJsonAcceptsLowerCamelCaseAliases()
    {
        var configManager = CreateConfigManager(
            ("usenet.providers",
                """
                {"providers":[{"type":"pooled","host":"news.example.invalid","port":"563","ssl":"true","user":"user","pass":"pass","max_connections":"75","priority":"10","statPipeliningEnabled":false}]}
                """));

        var provider = Assert.Single(configManager.GetUsenetProviderConfig().Providers);
        Assert.Equal(ProviderType.Pooled, provider.Type);
        Assert.Equal("news.example.invalid", provider.Host);
        Assert.Equal(563, provider.Port);
        Assert.True(provider.UseSsl);
        Assert.Equal("user", provider.User);
        Assert.Equal("pass", provider.Pass);
        Assert.Equal(75, provider.MaxConnections);
        Assert.Equal(10, provider.Priority);
        Assert.False(provider.IsStatPipeliningEnabled());
    }

    [Fact]
    public void ExplicitWorkerCapsSeparateDownloadVerifyAndRepairLanes()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "50"),
            ("queue.max-concurrent-downloads", "3"),
            ("queue.max-concurrent-verify", "10"),
            ("queue.max-concurrent-repair", "7")
        );

        Assert.Equal(3, configManager.GetAdaptiveMaxConcurrentQueueDownloads());
        Assert.Equal(10, configManager.GetAdaptiveMaxConcurrentVerifyJobs());
        Assert.Equal(7, configManager.GetAdaptiveMaxConcurrentRepairJobs());
    }

    [Fact]
    public void ExplicitVerifyWorkerCapIsNotClampedByRepairConnectionBudget()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "100"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "10"),
            ("queue.max-concurrent-verify", "12")
        );

        Assert.Equal(10, configManager.GetRepairConnectionBudget());
        Assert.Equal(12, configManager.GetAdaptiveMaxConcurrentVerifyJobs());
    }

    [Fact]
    public void AutomaticVerifyAndRepairWorkersUseIndependentLaneDefaults()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "50"),
            ("queue.max-concurrent-verify", "0"),
            ("queue.max-concurrent-repair", "0")
        );

        Assert.Equal(2, configManager.GetAdaptiveMaxConcurrentVerifyJobs());
        Assert.Equal(2, configManager.GetAdaptiveMaxConcurrentRepairJobs());
    }

    [Fact]
    public void AutomaticVerifyWorkersScalePastLegacyFourWorkerCeilingOnHighCapacityHosts()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "100"),
            ("queue.max-concurrent-verify", "0"),
            ("queue.max-concurrent-repair", "0")
        );

        var verifyWorkers = configManager.GetAdaptiveMaxConcurrentVerifyJobs();

        var expectedWorkerCeiling = Math.Clamp(Environment.ProcessorCount, 1, 16);
        Assert.Equal(expectedWorkerCeiling, verifyWorkers);
        if (Environment.ProcessorCount > 4)
            Assert.True(verifyWorkers > 4);
        Assert.InRange(verifyWorkers, 1, 16);
        Assert.Equal(4, configManager.GetAdaptiveMaxConcurrentRepairJobs());
    }

    [Fact]
    public void QueueWorkerArticleCacheUsesGlobalBudgetShare()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("queue.max-concurrent-downloads", "16"),
            ("usenet.article-cache-max-megabytes", "256")
        );

        Assert.Equal(16L * 1024 * 1024, configManager.GetArticleCacheMaxBytesPerQueueWorker());
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
    public void HealthCheckConcurrencyUsesDefaultRepairConnectionBudget()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200")
        );

        Assert.Equal(2, configManager.GetAdaptiveHealthCheckConcurrency());
        Assert.Equal(2, configManager.GetRepairConnectionBudget());
    }

    [Fact]
    public void PostDownloadVerificationUsesDownloadConnectionBudgetInsteadOfRepairBudget()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200")
        );

        Assert.Equal(2, configManager.GetAdaptiveHealthCheckConcurrency());
        Assert.Equal(2, configManager.GetRepairConnectionBudget());
        Assert.Equal(8, configManager.GetAdaptivePostDownloadVerificationConcurrency());
    }

    [Fact]
    public void PostDownloadVerificationCanUseConfiguredProviderConnectionBudgetAboveCpuBound()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "20")
        );

        Assert.Equal(40, configManager.GetRepairConnectionBudget());
        Assert.Equal(200, configManager.GetAdaptivePostDownloadVerificationConcurrency());
    }

    [Fact]
    public void AdaptivePostDownloadVerificationUsesProviderCapacityWhenManualFallbackIsLow()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("repair.healthcheck-concurrency", "1"),
            ("repair.connection-budget-percent", "20")
        );

        Assert.True(configManager.GetAdaptivePostDownloadVerificationConcurrency() > Environment.ProcessorCount * 2);
        Assert.Equal(200, configManager.GetAdaptivePostDownloadVerificationConcurrency());
    }

    [Fact]
    public void PostDownloadVerificationIgnoresLowHealthCheckFallbackWhenAdaptiveDisabled()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "80"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "12"),
            ("repair.connection-budget-percent", "25")
        );

        Assert.Equal(20, configManager.GetRepairConnectionBudget());
        Assert.Equal(80, configManager.GetAdaptivePostDownloadVerificationConcurrency());
    }

    [Fact]
    public void PostDownloadVerificationUsesConfiguredProviderCapacityWhenAdaptiveDisabled()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "1")
        );

        Assert.Equal(200, configManager.GetAdaptivePostDownloadVerificationConcurrency());
    }

    [Fact]
    public void HealthCheckConcurrencyCanUseConfiguredRepairConnectionBudget()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "8"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("repair.healthcheck-concurrency", "200"),
            ("repair.connection-budget-percent", "50")
        );

        Assert.Equal(4, configManager.GetAdaptiveHealthCheckConcurrency());
        Assert.Equal(4, configManager.GetRepairConnectionBudget());
    }

    [Fact]
    public void AdaptiveHealthCheckConcurrencyUsesProviderCapacityWhenManualFallbackIsLow()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("repair.healthcheck-concurrency", "64")
        );

        var expected = Math.Min(Math.Min(40, Environment.ProcessorCount * 2), 64);
        Assert.Equal(expected, configManager.GetAdaptiveHealthCheckConcurrency());
    }

    [Fact]
    public void AdaptiveHealthCheckConcurrencyDoesNotLetLowFallbackCapProviderCapacity()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("repair.healthcheck-concurrency", "1"),
            ("repair.connection-budget-percent", "100")
        );

        Assert.True(configManager.GetAdaptiveHealthCheckConcurrency() > 1);
    }

    [Fact]
    public void ProviderConfigsWithoutExplicitStatPipeliningUseFastDefault()
    {
        var configManager = CreateConfigManager(("usenet.providers", CreateProviderConfig(50)));

        var provider = Assert.Single(configManager.GetUsenetProviderConfig().Providers);
        Assert.True(provider.IsStatPipeliningEnabled());
    }

    [Fact]
    public void ExplicitProviderStatPipeliningDisableIsPreserved()
    {
        var configManager = CreateConfigManager(
            ("usenet.providers", CreateProviderConfigWithPipelining(false)));

        var provider = Assert.Single(configManager.GetUsenetProviderConfig().Providers);
        Assert.False(provider.IsStatPipeliningEnabled());
    }

    [Fact]
    public void AutoTotalStreamingConnectionsAreCpuAndConnectionBound()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.article-buffer-size", "40"),
            ("usenet.max-streaming-connections", "0"),
            ("usenet.max-total-streaming-connections", "0")
        );

        var totalStreamingConnections = configManager.GetMaxTotalStreamingConnections();

        Assert.Equal(GetExpectedAutomaticStreamingConnectionBudget(200), totalStreamingConnections);
        Assert.InRange(totalStreamingConnections, 1, GetExpectedAutomaticStreamingConnectionBudget(200));
    }

    [Fact]
    public void AdaptiveTotalStreamingConnectionsUseProviderCapacityWhenManualFallbackIsLow()
    {
        if (Environment.ProcessorCount < 3) return;

        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "15"),
            ("usenet.providers", CreateProviderConfig(100, 100)),
            ("usenet.adaptive-connections-enabled", "true"),
            ("usenet.article-buffer-size", "40"),
            ("usenet.max-streaming-connections", "0"),
            ("usenet.max-total-streaming-connections", "0")
        );

        var totalStreamingConnections = configManager.GetMaxTotalStreamingConnections();

        Assert.True(totalStreamingConnections > 10);
        Assert.InRange(totalStreamingConnections, 1, 200);
    }

    [Fact]
    public void ExplicitTotalStreamingConnectionsAreClamped()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.max-total-streaming-connections", "1024")
        );

        Assert.Equal(512, configManager.GetMaxTotalStreamingConnections());
    }

    [Fact]
    public void ExplicitStreamingConnectionsCanUseHighOperatorOverrides()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "200"),
            ("usenet.adaptive-connections-enabled", "false"),
            ("usenet.max-streaming-connections", "200"),
            ("usenet.max-total-streaming-connections", "200")
        );

        Assert.Equal(200, configManager.GetMaxStreamingConnections());
        Assert.Equal(200, configManager.GetMaxTotalStreamingConnections());
    }

    [Fact]
    public void DefaultCpuPressureTargetAllowsOneCoreOnMulticoreHosts()
    {
        if (Environment.ProcessorCount < 2) return;

        var original = Environment.GetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES");
        try
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", null);

            Assert.Equal(1.00, ConfigManager.GetCpuPressureMultiplier(1.00));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", original);
        }
    }

    [Fact]
    public void DefaultCpuPressureTargetBacksOffAboveSustainedMulticoreBurn()
    {
        if (Environment.ProcessorCount < 2) return;

        var original = Environment.GetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES");
        try
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", null);

            Assert.True(ConfigManager.GetCpuPressureMultiplier(2.20) < 1.00);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_ADAPTIVE_CPU_TARGET_CORES", original);
        }
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

    [Fact]
    public void MountTypeAndDirectoryUseGenericConfigAliases()
    {
        var configManager = CreateConfigManager(
            ("Mount:Type", "dfs"),
            ("Mount:Directory", "/mnt/dfs/"),
            ("rclone.mount-dir", "/mnt/rclone"));

        Assert.Equal("dfs", configManager.GetMountType());
        Assert.Equal("/mnt/dfs", configManager.GetMountDir());
        Assert.Equal("/mnt/rclone", configManager.GetRcloneMountDir());
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForIdenticalValues()
    {
        var configManager = CreateConfigManager(("usenet.providers", CreateProviderConfig(8)));
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = CreateProviderConfig(8)
            }
        ]);

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void UpdateValuesRaisesChangedEventOnlyForRealValueChanges()
    {
        var unchangedProviderConfig = CreateProviderConfig(8);
        var configManager = CreateConfigManager(
            ("usenet.providers", unchangedProviderConfig),
            ("rclone.host", "http://rclone:5572"));
        Dictionary<string, string>? changedConfig = null;
        configManager.OnConfigChanged += (_, args) => changedConfig = args.ChangedConfig;

        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = unchangedProviderConfig
            },
            new ConfigItem
            {
                ConfigName = "rclone.host",
                ConfigValue = "http://rclone:5573"
            }
        ]);

        Assert.NotNull(changedConfig);
        Assert.Equal(["rclone.host"], changedConfig.Keys);
        Assert.Equal("http://rclone:5573", changedConfig["rclone.host"]);
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForSemanticallyEquivalentValues()
    {
        var compactProviderConfig = CreateProviderConfig(8);
        var prettyProviderConfig = CreateProviderConfig([8], writeIndented: true);
        var configManager = CreateConfigManager(
            ("usenet.providers", compactProviderConfig),
            ("rclone.rc-enabled", "true"));
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = prettyProviderConfig
            },
            new ConfigItem
            {
                ConfigName = "rclone.rc-enabled",
                ConfigValue = "True"
            }
        ]);

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForSemanticallyEquivalentNumericValues()
    {
        var configManager = CreateConfigManager(
            ("usenet.max-download-connections", "80"),
            ("queue.max-concurrent-downloads", "4"),
            ("repair.healthcheck-concurrency", "12"));
        Dictionary<string, string>? changedConfig = null;
        configManager.OnConfigChanged += (_, args) => changedConfig = args.ChangedConfig;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "usenet.max-download-connections", ConfigValue = "080" },
            new ConfigItem { ConfigName = "queue.max-concurrent-downloads", ConfigValue = " 4 " },
            new ConfigItem { ConfigName = "repair.healthcheck-concurrency", ConfigValue = "+12" }
        ]);

        Assert.Null(changedConfig);
        Assert.Equal(80, configManager.GetMaxDownloadConnections());
        Assert.Equal(4, configManager.GetMaxConcurrentQueueDownloads());
        Assert.Equal(12, configManager.GetHealthCheckConcurrency());
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForSemanticallyEquivalentMountTypes()
    {
        var configManager = CreateConfigManager(("Mount:Type", "rclone"));
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = " RCLONE " }
        ]);

        Assert.Equal(0, eventCount);
        Assert.Equal("rclone", configManager.GetMountType());
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForUnsetOptionalValues()
    {
        var configManager = new ConfigManager();
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "" },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "" },
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = "" }
        ]);

        Assert.Equal(0, eventCount);
        Assert.Null(configManager.GetRcloneHost());
        Assert.Null(configManager.GetRcloneUser());
        Assert.Null(configManager.GetRclonePass());
        Assert.Equal("/mnt/nzbdav", configManager.GetRcloneMountDir());
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForWhitespaceOnlyRcloneEndpointOrMountPath()
    {
        var configManager = new ConfigManager();
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "   " },
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = " \t " }
        ]);

        Assert.Equal(0, eventCount);
        Assert.Null(configManager.GetRcloneHost());
        Assert.Equal("/mnt/nzbdav", configManager.GetRcloneMountDir());
    }

    [Fact]
    public void UpdateValuesDoesNotRaiseChangedEventForWhitespaceOnlyRcloneCredentials()
    {
        var configManager = new ConfigManager();
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "   " },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = " \t " }
        ]);

        Assert.Equal(0, eventCount);
        Assert.Null(configManager.GetRcloneUser());
        Assert.Null(configManager.GetRclonePass());
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
        return CreateProviderConfig(maxConnections, writeIndented: false);
    }

    private static string CreateProviderConfig(int[] maxConnections, bool writeIndented)
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
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = writeIndented });
    }

    private static string CreateProviderConfigWithPipelining(bool enabled)
    {
        return System.Text.Json.JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example.invalid",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = 50,
                    StatPipeliningEnabled = enabled
                }
            ]
        });
    }

    private static int GetExpectedAutomaticQueueWorkers(int downloadConnections)
    {
        var connectionBased = (Math.Max(1, downloadConnections) + 24) / 25;
        var coreBased = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(Math.Min(connectionBased, coreBased), 1, 16);
    }

    private static int GetExpectedAutomaticStreamingConnectionBudget(int downloadConnections)
    {
        var cpuBased = Math.Clamp(Environment.ProcessorCount * 4, 4, 64);
        return Math.Clamp(Math.Min(cpuBased, Math.Max(1, downloadConnections)), 1, 512);
    }

    private static void InjectCpuPressure(ConfigManager configManager, double multiplier)
    {
        SetPrivateField(configManager, "_lastCpuSampleAt", DateTimeOffset.UtcNow);
        SetPrivateField(configManager, "_lastProcessCpuCores", 1d);
        SetPrivateField(configManager, "_lastCpuPressureMultiplier", multiplier);
    }

    private static void SetPrivateField<T>(ConfigManager configManager, string fieldName, T value)
    {
        var field = typeof(ConfigManager).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(configManager, value);
    }

}

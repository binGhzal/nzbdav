using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Repair;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetFullStatus;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class StatusControllerDiagnosticsTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public StatusControllerDiagnosticsTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusControllersExcludePausedDownloadsAndDisableRepairAutomation(bool fullStatus)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var waiting = CreateQueueItem("Waiting", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var paused = CreateQueueItem("Paused", QueueItem.PriorityOption.Paused, now.LocalDateTime);
        var active = CreateQueueItem("Active", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var activeWorker = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Leased,
            active.Id,
            now);
        dbContext.QueueItems.AddRange(waiting, paused, active);
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Pending, waiting.Id, now),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Pending, paused.Id, now),
            activeWorker,
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Retry, Guid.NewGuid(), now));
        await dbContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "test-api-key" },
            new ConfigItem { ConfigName = "queue.max-concurrent-downloads", ConfigValue = "4" },
            new ConfigItem { ConfigName = "queue.max-concurrent-repair", ConfigValue = "4" },
            new ConfigItem { ConfigName = "repair.enable", ConfigValue = "false" }
        ]);
        var websocketManager = new WebsocketManager();
        var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        var laneCoordinator = new QueueWorkLaneCoordinator();
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            laneCoordinator,
            websocketManager,
            new ArrDownloadReportService(configManager));
        AddInProgressDownload(queueManager, active, activeWorker);
        using var healthCheckService = new HealthCheckService(
            configManager,
            usenetClient,
            laneCoordinator,
            websocketManager);
        var context = CreateHttpContext();
        var dbClient = new DavDatabaseClient(dbContext);

        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            var result = fullStatus
                ? await new GetFullStatusController(
                    context,
                    dbClient,
                    configManager,
                    queueManager,
                    new ActiveStreamTracker(),
                    healthCheckService,
                    new MountStatusProvider(),
                    usenetClient).HandleRequest()
                : await new GetStatusController(
                    context,
                    dbClient,
                    configManager,
                    queueManager,
                    new ActiveStreamTracker(),
                    healthCheckService,
                    new MountStatusProvider(),
                    usenetClient).HandleRequest();

            var ok = Assert.IsType<OkObjectResult>(result);
            var diagnostic = fullStatus
                ? Project(Assert.IsType<GetFullStatusResponse>(ok.Value))
                : Project(Assert.IsType<GetStatusResponse>(ok.Value));
            Assert.Equal(1, diagnostic.DownloadWaiting);
            Assert.Equal(1, diagnostic.DownloadReady);
            Assert.Equal("active", diagnostic.DownloadState);
            Assert.Equal(0, diagnostic.MaxRepairWorkers);
            Assert.Equal(0, diagnostic.RepairMax);
            Assert.Equal("disabled", diagnostic.RepairState);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    [Fact]
    public async Task RepairStatusControllerReportsDisabledLaneWhenRepairAutomationIsOff()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.WorkerJobs.Add(CreateWorkerJob(
            WorkerJob.JobKind.Repair,
            WorkerJob.JobStatus.Retry,
            Guid.NewGuid(),
            now));
        await dbContext.SaveChangesAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "queue.max-concurrent-repair", ConfigValue = "4" },
            new ConfigItem { ConfigName = "repair.enable", ConfigValue = "false" }
        ]);
        var context = CreateHttpContext();
        var controller = new RepairStatusController(
            new DavDatabaseClient(dbContext),
            configManager)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            var result = await controller.HandleApiRequest();

            var response = Assert.IsType<RepairStatusResponse>(
                Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(0, response.RepairQueue.Max);
            Assert.Equal("disabled", response.RepairQueue.State);
            Assert.Equal(1, response.RepairQueue.Retry);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    [Fact]
    public async Task ArrImportDiagnosticsExposeQuarantineCountAndReason()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var history = new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = "Quarantined.nzb",
            JobName = "Quarantined",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            FailMessage = "automatic repair disabled"
        };
        var now = DateTimeOffset.UtcNow;
        dbContext.HistoryItems.Add(history);
        dbContext.ArrImportCommands.Add(new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = history.Id,
            Category = history.Category,
            RequiredInvalidationPathsJson = "[]",
            Status = ArrImportCommandStatus.Quarantined,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            NextAttemptAt = now,
            VisibleAt = now,
            CompletedAt = now,
            LastError = "automatic repair disabled after confirmed missing articles"
        });
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetArrImportCommandStatsAsync();
        var diagnostic = NzbWebDAV.Api.SabControllers.ArrImportCommandDiagnosticStatus.FromStats(stats, now);

        Assert.Equal(1, stats.Quarantined);
        Assert.Equal(1, diagnostic.Quarantined);
        Assert.Equal("automatic repair disabled after confirmed missing articles", diagnostic.LastQuarantineReason);
    }

    [Fact]
    public void RcloneInvalidationDiagnosticsExposeBacklogAgeAndConfiguredCallEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        var stats = new DavDatabaseClient.RcloneInvalidationStats(
            Pending: 2,
            Ready: 1,
            Failed: 0,
            WholeCacheVisibilityFencePending: true,
            MaxAttempts: 1,
            LastError: null,
            OldestPendingAt: now.AddSeconds(-12));
        var runtime = new RcloneRuntimeSnapshot(
            VisibilityFenceRequired: true,
            WholeCacheVisibilityFencePending: false,
            VisibilityFenceGeneration: 1,
            RemoteControlEnabled: true,
            HostConfigured: true,
            LastAttemptAt: now.AddSeconds(-1),
            LastSuccessfulConfiguredCallAt: now.AddSeconds(-2),
            LastError: null);

        var diagnostic = RcloneInvalidationStatus.FromSnapshots(stats, runtime, now);

        Assert.Equal(12, diagnostic.OldestPendingAgeSeconds);
        Assert.True(diagnostic.VisibilityFenceRequired);
        Assert.True(diagnostic.WholeCacheVisibilityFencePending);
        Assert.True(diagnostic.RemoteControlEnabled);
        Assert.True(diagnostic.HostConfigured);
        Assert.Equal(now.AddSeconds(-1), diagnostic.LastAttemptAt);
        Assert.Equal(now.AddSeconds(-2), diagnostic.LastSuccessfulConfiguredCallAt);
        Assert.Null(diagnostic.RuntimeLastError);
    }

    [Fact]
    public void RcloneWholeCacheSentinelIsReportedPendingOnlyForTheActiveRcloneTopology()
    {
        var now = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        var stats = new DavDatabaseClient.RcloneInvalidationStats(
            Pending: 1,
            Ready: 1,
            Failed: 0,
            WholeCacheVisibilityFencePending: true,
            MaxAttempts: 0,
            LastError: null,
            OldestPendingAt: now);
        var runtime = new RcloneRuntimeSnapshot(
            VisibilityFenceRequired: false,
            WholeCacheVisibilityFencePending: false,
            VisibilityFenceGeneration: 1,
            RemoteControlEnabled: false,
            HostConfigured: false,
            LastAttemptAt: null,
            LastSuccessfulConfiguredCallAt: null,
            LastError: null);

        var diagnostic = RcloneInvalidationStatus.FromSnapshots(stats, runtime, now);

        Assert.False(diagnostic.WholeCacheVisibilityFencePending);
    }

    private static DiagnosticProjection Project(GetStatusResponse response) => new(
        response.Status.WorkerQueues.DownloadWaiting,
        response.Status.WorkerQueues.DownloadReady,
        response.Status.WorkerQueues.DownloadState,
        response.Status.MaxRepairWorkers,
        response.Status.WorkerQueues.RepairMax,
        response.Status.WorkerQueues.RepairState);

    private static DiagnosticProjection Project(GetFullStatusResponse response) => new(
        response.Status.WorkerQueues.DownloadWaiting,
        response.Status.WorkerQueues.DownloadReady,
        response.Status.WorkerQueues.DownloadState,
        response.Status.MaxRepairWorkers,
        response.Status.WorkerQueues.RepairMax,
        response.Status.WorkerQueues.RepairState);

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        return context;
    }

    private static QueueItem CreateQueueItem(
        string jobName,
        QueueItem.PriorityOption priority,
        DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        FileName = $"{jobName}.nzb",
        JobName = jobName,
        NzbFileSize = 100,
        TotalSegmentBytes = 1024,
        Category = "movies",
        Priority = priority,
        PostProcessing = QueueItem.PostProcessingOption.None
    };

    private static WorkerJob CreateWorkerJob(
        WorkerJob.JobKind kind,
        WorkerJob.JobStatus status,
        Guid targetId,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Status = status,
        TargetId = targetId,
        Priority = 0,
        Attempts = status == WorkerJob.JobStatus.Pending ? 0 : 1,
        CreatedAt = now,
        UpdatedAt = now,
        AvailableAt = now.AddMinutes(-1),
        LeaseExpiresAt = status == WorkerJob.JobStatus.Leased ? now.AddMinutes(5) : null,
        LeaseOwner = status == WorkerJob.JobStatus.Leased ? "worker" : null,
        LeaseToken = status == WorkerJob.JobStatus.Leased ? Guid.NewGuid() : null,
        LeaseGeneration = status == WorkerJob.JobStatus.Leased ? 1 : 0,
        LastError = status == WorkerJob.JobStatus.Retry ? "retry" : null
    };

    private static void AddInProgressDownload(
        QueueManager queueManager,
        QueueItem queueItem,
        WorkerJob workerJob)
    {
        var itemType = typeof(QueueManager).GetNestedType(
            "InProgressQueueItem",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(itemType);
        var inProgress = Activator.CreateInstance(itemType, nonPublic: true);
        Assert.NotNull(inProgress);
        itemType.GetProperty("QueueItem")!.SetValue(inProgress, queueItem);
        itemType.GetProperty("CancellationTokenSource")!
            .SetValue(inProgress, new CancellationTokenSource());
        itemType.GetProperty("WorkerLease")!.SetValue(inProgress, new NzbWebDAV.Coordination.WorkerLeaseIdentity(
            workerJob.Id,
            workerJob.LeaseOwner!,
            workerJob.LeaseToken!.Value,
            workerJob.LeaseGeneration));
        itemType.GetProperty("Stage")!.SetValue(inProgress, QueueProcessingStage.Downloading);

        var itemsField = typeof(QueueManager).GetField(
            "_inProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(itemsField);
        var items = Assert.IsAssignableFrom<System.Collections.IDictionary>(itemsField.GetValue(queueManager));
        items.Add(queueItem.Id, inProgress);
    }

    private sealed record DiagnosticProjection(
        int DownloadWaiting,
        int DownloadReady,
        string DownloadState,
        int MaxRepairWorkers,
        int RepairMax,
        string RepairState);
}

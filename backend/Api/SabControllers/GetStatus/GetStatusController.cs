using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Telemetry;

namespace NzbWebDAV.Api.SabControllers.GetStatus;

public class GetStatusController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    ActiveStreamTracker activeStreamTracker,
    HealthCheckService healthCheckService,
    MountStatusProvider mountStatusProvider,
    UsenetStreamingClient usenetClient
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var laneSnapshot = queueManager.GetLaneSnapshot();
        var activeQueueItemIds = queueManager.GetInProgressQueueItems()
            .Select(x => x.queueItem.Id)
            .ToArray();
        var activeJobs = laneSnapshot.TotalActive;
        var queuedJobs = await dbClient.GetQueueItemsCount(null, RequestContext.RequestAborted).ConfigureAwait(false);
        var downloadWaiting = await dbClient.GetQueueItemsCount(
            category: null,
            nzoIds: null,
            search: null,
            priorities: null,
            statuses: ["queued"],
            ct: RequestContext.RequestAborted,
            excludeIds: activeQueueItemIds).ConfigureAwait(false);
        var isPaused = ConfigManager.IsQueuePaused();
        var maxRepairWorkers = ConfigManager.IsRepairJobEnabled()
            ? ConfigManager.GetAdaptiveMaxConcurrentRepairJobs()
            : 0;
        var activeStreams = activeStreamTracker.GetSnapshot();
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var runtimePressure = ConfigManager.GetRuntimePressureSnapshot();
        var rcloneInvalidations = await dbClient.GetRcloneInvalidationStatsAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var healthQueue = await dbClient.GetHealthWorkerQueueStatsAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var durableWorkerJobs = await dbClient.GetWorkerJobQueueStatsAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var arrIntegrationStats = await dbClient.GetArrIntegrationStatsAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var arrImportCommandStats = await dbClient.GetArrImportCommandStatsAsync(
            RequestContext.RequestAborted).ConfigureAwait(false);
        var arrPriorityOptions = ConfigManager.GetArrPrioritizationOptions();
        var arrSearchNudgeOptions = ConfigManager.GetArrSearchNudgeOptions();
        var repairStatus = await dbClient.GetRepairRunStatusAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var healthWorkers = healthCheckService.GetWorkerSnapshot();
        var cacheSnapshot = SparseSegmentCacheManager.Shared.GetSnapshot(ConfigManager.GetSparseSegmentCacheOptions());
        var databaseStorage = await DatabaseStorageTelemetry
            .CaptureAsync(dbClient.Ctx, RequestContext.RequestAborted)
            .ConfigureAwait(false);
        var databaseStatus = DatabaseStatus.FromSnapshots(
            databaseStorage,
            DatabaseTelemetry.Shared.GetSnapshot());
        var criticalPathStatus = CriticalPathStatus.FromSnapshot(CriticalPathTelemetry.Shared.GetSnapshot());
        var response = new GetStatusResponse()
        {
            Status = new GetStatusResponse.StatusObject
            {
                Paused = isPaused,
                PausedAll = isPaused,
                QueueStatus = GetQueueStatus(isPaused, activeJobs, queuedJobs),
                Jobs = queuedJobs,
                JobsActive = activeJobs,
                MaxQueueWorkers = ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                MaxVerifyWorkers = ConfigManager.GetAdaptiveMaxConcurrentVerifyJobs(),
                MaxRepairWorkers = maxRepairWorkers,
                MaxDownloadConnections = ConfigManager.GetMaxDownloadConnections(),
                AdaptiveMaxDownloadConnections = ConfigManager.GetAdaptiveMaxDownloadConnections(),
                QueueFileProcessingConcurrency = ConfigManager.GetAdaptiveQueueFileProcessingConcurrency(),
                HealthCheckConcurrency = ConfigManager.GetAdaptiveHealthCheckConcurrency(),
                MaxStreamingConnections = ConfigManager.GetAdaptiveMaxStreamingConnections(),
                MaxTotalStreamingConnections = ConfigManager.GetAdaptiveMaxTotalStreamingConnections(),
                MaxActiveStreams = ConfigManager.GetAdaptiveMaxActiveStreams(),
                ActiveStreams = activeStreams.Count,
                RcloneInvalidations = RcloneInvalidationStatus.FromSnapshots(
                    rcloneInvalidations,
                    RcloneClient.GetRuntimeSnapshot(),
                    DateTimeOffset.UtcNow),
                Database = databaseStatus,
                CriticalPath = criticalPathStatus,
                Cache = CacheStatus.FromSnapshot(cacheSnapshot),
                Mount = MountDiagnosticStatus.FromSnapshot(mountStatusProvider.GetSnapshot(cacheSnapshot)),
                ProviderDiagnostics = ProviderDiagnosticStatus.FromSnapshots(
                    usenetClient.GetProviderSnapshots(),
                    ConfigManager.GetUsenetProviderConfig()),
                WorkerQueues = WorkerQueueStatus.FromStats(
                    laneSnapshot.DownloadActive,
                    downloadWaiting,
                    laneSnapshot.Verifying,
                    laneSnapshot.WaitingForVerify,
                    ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                    ConfigManager.GetAdaptiveMaxConcurrentVerifyJobs(),
                    maxRepairWorkers,
                    isPaused,
                    healthWorkers,
                    healthQueue,
                    durableWorkerJobs),
                RepairRuns = RepairRunsStatus.FromRuns(
                    repairStatus.ActiveRun,
                    repairStatus.LastRun,
                    repairStatus.BrokenFiles),
                ArrPrioritization = ArrPrioritizationStatus.FromStats(arrPriorityOptions, arrIntegrationStats),
                ArrSearchNudge = ArrSearchNudgeStatus.FromStats(arrSearchNudgeOptions, arrIntegrationStats),
                ArrDownloadReport = ArrDownloadReportStatus.FromStats(arrIntegrationStats),
                ArrImportCommands = ArrImportCommandDiagnosticStatus.FromStats(
                    arrImportCommandStats,
                    DateTimeOffset.UtcNow),
                TotalStreamsOpened = activeStreams.TotalOpened,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                WorkingSetBytes = process.WorkingSet64,
                GcMemoryLoadPercent = GetGcMemoryLoadPercent(gcInfo),
                ProcessCpuCores = Math.Round(runtimePressure.ProcessCpuCores, 2),
                CpuPressureMultiplier = runtimePressure.CpuPressureMultiplier,
                RuntimePressureMultiplier = runtimePressure.EffectiveMultiplier,
                ThreadPoolThreads = ThreadPool.ThreadCount,
                ThreadPoolPendingWorkItems = ThreadPool.PendingWorkItemCount,
                ProcessId = Environment.ProcessId,
                Uptime = GetUptime(),
                Version = ConfigManager.AppVersion,
                CompleteDir = GetCompleteDir(ConfigManager),
                DownloadDir = Path.Join(ConfigManager.GetMountDir(), DavItem.NzbFolder.Name),
            }
        };

        return Ok(response);
    }

    public static string GetQueueStatus(bool isPaused, int activeJobs, int queuedJobs)
    {
        if (isPaused) return "Paused";
        if (activeJobs > 0) return "Downloading";
        return queuedJobs > 0 ? "Queued" : "Idle";
    }

    public static string GetCompleteDir(ConfigManager configManager)
    {
        return Path.Join(configManager.GetMountDir(), DavItem.SymlinkFolder.Name);
    }

    private static string GetUptime()
    {
        var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
        return ((long)uptime.TotalSeconds).ToString();
    }

    private static double GetGcMemoryLoadPercent(GCMemoryInfo gcInfo)
    {
        if (gcInfo.HighMemoryLoadThresholdBytes <= 0) return 0;
        return Math.Round(gcInfo.MemoryLoadBytes * 100.0 / gcInfo.HighMemoryLoadThresholdBytes, 2);
    }
}

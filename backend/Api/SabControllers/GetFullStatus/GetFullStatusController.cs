using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    ActiveStreamTracker activeStreamTracker,
    HealthCheckService healthCheckService,
    MountStatusProvider mountStatusProvider
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var laneSnapshot = queueManager.GetLaneSnapshot();
        var activeJobs = laneSnapshot.TotalActive;
        var queuedJobs = await dbClient.GetQueueItemsCount(null, RequestContext.RequestAborted).ConfigureAwait(false);
        var isPaused = ConfigManager.IsQueuePaused();
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
        var arrPriorityOptions = ConfigManager.GetArrPrioritizationOptions();
        var arrSearchNudgeOptions = ConfigManager.GetArrSearchNudgeOptions();
        var repairStatus = await dbClient.GetRepairRunStatusAsync(
            ct: RequestContext.RequestAborted).ConfigureAwait(false);
        var healthWorkers = healthCheckService.GetWorkerSnapshot();
        var cacheSnapshot = SparseSegmentCacheManager.Shared.GetSnapshot(ConfigManager.GetSparseSegmentCacheOptions());
        var status = new GetFullStatusResponse()
        {
            Status = new GetFullStatusResponse.FullStatusObject()
            {
                Paused = isPaused,
                PausedAll = isPaused,
                QueueStatus = GetStatusController.GetQueueStatus(isPaused, activeJobs, queuedJobs),
                Jobs = queuedJobs,
                JobsActive = activeJobs,
                MaxQueueWorkers = ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                MaxVerifyWorkers = ConfigManager.GetAdaptiveMaxConcurrentVerifyJobs(),
                MaxRepairWorkers = ConfigManager.GetAdaptiveMaxConcurrentRepairJobs(),
                MaxDownloadConnections = ConfigManager.GetMaxDownloadConnections(),
                AdaptiveMaxDownloadConnections = ConfigManager.GetAdaptiveMaxDownloadConnections(),
                QueueFileProcessingConcurrency = ConfigManager.GetAdaptiveQueueFileProcessingConcurrency(),
                HealthCheckConcurrency = ConfigManager.GetAdaptiveHealthCheckConcurrency(),
                MaxStreamingConnections = ConfigManager.GetAdaptiveMaxStreamingConnections(),
                MaxTotalStreamingConnections = ConfigManager.GetAdaptiveMaxTotalStreamingConnections(),
                MaxActiveStreams = ConfigManager.GetAdaptiveMaxActiveStreams(),
                ActiveStreams = activeStreams.Count,
                RcloneInvalidations = RcloneInvalidationStatus.FromStats(rcloneInvalidations),
                Cache = CacheStatus.FromSnapshot(cacheSnapshot),
                Mount = MountDiagnosticStatus.FromSnapshot(mountStatusProvider.GetSnapshot(cacheSnapshot)),
                ProviderDiagnostics = ProviderDiagnosticStatus.FromConfig(ConfigManager.GetUsenetProviderConfig()),
                WorkerQueues = WorkerQueueStatus.FromStats(
                    laneSnapshot.DownloadActive,
                    queuedJobs,
                    laneSnapshot.Verifying,
                    laneSnapshot.WaitingForVerify,
                    ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                    ConfigManager.GetAdaptiveMaxConcurrentVerifyJobs(),
                    ConfigManager.GetAdaptiveMaxConcurrentRepairJobs(),
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
                CompleteDir = GetStatusController.GetCompleteDir(ConfigManager),
                DownloadDir = Path.Join(ConfigManager.GetMountDir(), DavItem.NzbFolder.Name),
            }
        };

        return Ok(status);
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

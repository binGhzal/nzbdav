using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;

namespace NzbWebDAV.Api.SabControllers.GetStatus;

public class GetStatusController(
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
        var activeJobs = queueManager.GetInProgressQueueItems().Count;
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
        var activeRepairRun = await dbClient.GetActiveRepairRunAsync(RequestContext.RequestAborted).ConfigureAwait(false);
        var lastRepairRun = (await dbClient.GetRepairRunsAsync(1, RequestContext.RequestAborted).ConfigureAwait(false))
            .FirstOrDefault();
        var repairBrokenFiles = await dbClient.Ctx.RepairBrokenFiles
            .AsNoTracking()
            .Where(x => !x.Cleared)
            .CountAsync(RequestContext.RequestAborted)
            .ConfigureAwait(false);
        var healthWorkers = healthCheckService.GetWorkerSnapshot();
        var cacheSnapshot = SparseSegmentCacheManager.Shared.GetSnapshot(ConfigManager.GetSparseSegmentCacheOptions());
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
                MaxRepairWorkers = ConfigManager.GetAdaptiveMaxConcurrentRepairJobs(),
                MaxDownloadConnections = ConfigManager.GetMaxDownloadConnections(),
                AdaptiveMaxDownloadConnections = ConfigManager.GetAdaptiveMaxDownloadConnections(),
                QueueFileProcessingConcurrency = ConfigManager.GetAdaptiveQueueFileProcessingConcurrency(),
                HealthCheckConcurrency = ConfigManager.GetAdaptiveHealthCheckConcurrency(),
                MaxStreamingConnections = ConfigManager.GetAdaptiveMaxStreamingConnections(),
                MaxTotalStreamingConnections = ConfigManager.GetAdaptiveMaxTotalStreamingConnections(),
                ActiveStreams = activeStreams.Count,
                RcloneInvalidations = RcloneInvalidationStatus.FromStats(rcloneInvalidations),
                Cache = CacheStatus.FromSnapshot(cacheSnapshot),
                Mount = MountDiagnosticStatus.FromSnapshot(mountStatusProvider.GetSnapshot(cacheSnapshot)),
                ProviderDiagnostics = ProviderDiagnosticStatus.FromConfig(ConfigManager.GetUsenetProviderConfig()),
                WorkerQueues = WorkerQueueStatus.FromStats(
                    activeJobs,
                    queuedJobs,
                    ConfigManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                    ConfigManager.GetAdaptiveMaxConcurrentVerifyJobs(),
                    ConfigManager.GetAdaptiveMaxConcurrentRepairJobs(),
                    isPaused,
                    healthWorkers,
                    healthQueue,
                    durableWorkerJobs),
                RepairRuns = RepairRunsStatus.FromRuns(activeRepairRun, lastRepairRun, repairBrokenFiles),
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
        return configManager.GetImportStrategy() switch
        {
            "strm" => configManager.GetStrmCompletedDownloadDir(),
            "both" => Path.Join(configManager.GetMountDir(), DavItem.SymlinkFolder.Name),
            "symlinks" => Path.Join(configManager.GetMountDir(), DavItem.SymlinkFolder.Name),
            _ => Path.Join(configManager.GetMountDir(), DavItem.SymlinkFolder.Name)
        };
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

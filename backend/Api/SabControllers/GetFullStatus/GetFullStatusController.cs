using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    ActiveStreamTracker activeStreamTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var activeJobs = queueManager.GetInProgressQueueItems().Count;
        var queuedJobs = await dbClient.GetQueueItemsCount(null, httpContext.RequestAborted).ConfigureAwait(false);
        var isPaused = configManager.IsQueuePaused();
        var activeStreams = activeStreamTracker.GetSnapshot();
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var runtimePressure = configManager.GetRuntimePressureSnapshot();
        var status = new GetFullStatusResponse()
        {
            Status = new GetFullStatusResponse.FullStatusObject()
            {
                Paused = isPaused,
                PausedAll = isPaused,
                QueueStatus = GetStatusController.GetQueueStatus(isPaused, activeJobs, queuedJobs),
                Jobs = queuedJobs,
                JobsActive = activeJobs,
                MaxQueueWorkers = configManager.GetAdaptiveMaxConcurrentQueueDownloads(),
                MaxDownloadConnections = configManager.GetMaxDownloadConnections(),
                AdaptiveMaxDownloadConnections = configManager.GetAdaptiveMaxDownloadConnections(),
                QueueFileProcessingConcurrency = configManager.GetAdaptiveQueueFileProcessingConcurrency(),
                HealthCheckConcurrency = configManager.GetAdaptiveHealthCheckConcurrency(),
                MaxStreamingConnections = configManager.GetAdaptiveMaxStreamingConnections(),
                MaxTotalStreamingConnections = configManager.GetAdaptiveMaxTotalStreamingConnections(),
                ActiveStreams = activeStreams.Count,
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
                CompleteDir = GetStatusController.GetCompleteDir(configManager),
                DownloadDir = Path.Join(configManager.GetRcloneMountDir(), DavItem.NzbFolder.Name),
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

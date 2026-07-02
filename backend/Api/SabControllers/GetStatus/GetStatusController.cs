using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.SabControllers.GetStatus;

public class GetStatusController(
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
        var response = new GetStatusResponse()
        {
            Status = new GetStatusResponse.StatusObject
            {
                Paused = isPaused,
                PausedAll = isPaused,
                QueueStatus = GetQueueStatus(isPaused, activeJobs, queuedJobs),
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
                CompleteDir = GetCompleteDir(configManager),
                DownloadDir = Path.Join(configManager.GetRcloneMountDir(), DavItem.NzbFolder.Name),
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
            "both" => Path.Join(configManager.GetRcloneMountDir(), DavItem.SymlinkFolder.Name),
            "symlinks" => Path.Join(configManager.GetRcloneMountDir(), DavItem.SymlinkFolder.Name),
            _ => Path.Join(configManager.GetRcloneMountDir(), DavItem.SymlinkFolder.Name)
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

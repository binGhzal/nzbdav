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
                ActiveStreams = activeStreams.Count,
                TotalStreamsOpened = activeStreams.TotalOpened,
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
}

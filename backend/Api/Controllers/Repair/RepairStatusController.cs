using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/status")]
public sealed class RepairStatusController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var repairStatus = await dbClient.GetRepairRunStatusAsync(
            ct: HttpContext.RequestAborted).ConfigureAwait(false);
        var workerQueues = await dbClient.GetWorkerJobQueueStatsAsync(ct: HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var brokenFiles = await dbClient.Ctx.RepairBrokenFiles
            .AsNoTracking()
            .Where(x => !x.Cleared)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new RepairStatusResponse
        {
            ActiveRun = repairStatus.ActiveRun == null ? null : RepairRunDto.FromModel(repairStatus.ActiveRun),
            LastRun = repairStatus.LastRun == null ? null : RepairRunDto.FromModel(repairStatus.LastRun),
            BrokenFiles = brokenFiles.Select(RepairBrokenFileDto.FromModel).ToList(),
            VerifyQueue = RepairWorkerQueueDto.FromStats(workerQueues.Verify, configManager.GetAdaptiveMaxConcurrentVerifyJobs()),
            RepairQueue = RepairWorkerQueueDto.FromStats(workerQueues.Repair, configManager.GetAdaptiveMaxConcurrentRepairJobs())
        });
    }
}

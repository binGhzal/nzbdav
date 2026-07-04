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
        var activeRun = await dbClient.GetActiveRepairRunAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var runs = await dbClient.GetRepairRunsAsync(1, HttpContext.RequestAborted).ConfigureAwait(false);
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
            ActiveRun = activeRun == null ? null : RepairRunDto.FromModel(activeRun),
            LastRun = runs.FirstOrDefault() is { } lastRun ? RepairRunDto.FromModel(lastRun) : null,
            BrokenFiles = brokenFiles.Select(RepairBrokenFileDto.FromModel).ToList(),
            VerifyQueue = RepairWorkerQueueDto.FromStats(workerQueues.Verify, configManager.GetAdaptiveMaxConcurrentVerifyJobs()),
            RepairQueue = RepairWorkerQueueDto.FromStats(workerQueues.Repair, configManager.GetAdaptiveMaxConcurrentRepairJobs())
        });
    }
}

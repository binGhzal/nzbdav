using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/run/{id:guid}/cancel")]
public sealed class CancelRepairRunController(
    DavDatabaseClient dbClient,
    HistoryVisibilityNotifier historyVisibilityNotifier) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            throw new BadHttpRequestException("Cancelling a repair run requires POST.");

        var id = HttpContext.Request.RouteValues["id"]?.ToString();
        if (!Guid.TryParse(id, out var repairRunId))
            throw new BadHttpRequestException("Invalid repair run id.");

        var exists = await dbClient.Ctx.RepairRuns
            .AnyAsync(x => x.Id == repairRunId, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (!exists)
            return NotFound(new BaseApiResponse
            {
                Status = false,
                Error = $"Repair run {repairRunId} was not found."
            });

        var repairPayload = DavDatabaseClient.CreateRepairRunPayloadJson(repairRunId);
        var affectedDavItemIds = await dbClient.Ctx.WorkerJobs.AsNoTracking()
            .Where(job => job.PayloadJson == repairPayload)
            .Where(job => job.Status == WorkerJob.JobStatus.Pending
                          || job.Status == WorkerJob.JobStatus.Leased
                          || job.Status == WorkerJob.JobStatus.Retry)
            .Select(job => job.TargetId)
            .Distinct()
            .ToListAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);

        await dbClient.CancelRepairRunAsync(repairRunId, ct: HttpContext.RequestAborted).ConfigureAwait(false);
        foreach (var davItemId in affectedDavItemIds)
            await historyVisibilityNotifier
                .PublishForDavItemIfVisibleAsync(davItemId, CancellationToken.None)
                .ConfigureAwait(false);
        var run = await dbClient.Ctx.RepairRuns
            .FirstOrDefaultAsync(x => x.Id == repairRunId, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new RepairRunResponse
        {
            Run = RepairRunDto.FromModel(run ?? throw new BadHttpRequestException("Repair run was not found."))
        });
    }
}

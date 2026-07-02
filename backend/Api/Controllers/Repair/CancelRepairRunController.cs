using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/run/{id:guid}/cancel")]
public sealed class CancelRepairRunController(DavDatabaseClient dbClient) : BaseApiController
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

        await dbClient.CancelRepairRunAsync(repairRunId, ct: HttpContext.RequestAborted).ConfigureAwait(false);
        var run = await dbClient.Ctx.RepairRuns
            .FirstOrDefaultAsync(x => x.Id == repairRunId, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new RepairRunResponse
        {
            Run = RepairRunDto.FromModel(run ?? throw new BadHttpRequestException("Repair run was not found."))
        });
    }
}

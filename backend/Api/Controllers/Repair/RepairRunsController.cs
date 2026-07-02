using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/runs")]
public sealed class RepairRunsController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var rawLimit = HttpContext.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(rawLimit, out var parsedLimit) ? parsedLimit : 50;
        limit = Math.Clamp(limit, 1, 500);
        var runs = await dbClient.GetRepairRunsAsync(limit, HttpContext.RequestAborted).ConfigureAwait(false);

        return Ok(new RepairRunsResponse
        {
            Runs = runs.Select(RepairRunDto.FromModel).ToList()
        });
    }
}

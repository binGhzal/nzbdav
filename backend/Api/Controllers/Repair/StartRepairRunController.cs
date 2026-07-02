using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/run")]
public sealed class StartRepairRunController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            throw new BadHttpRequestException("Starting a repair run requires POST.");

        var run = await dbClient.StartRepairRunAsync(ct: HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new RepairRunResponse
        {
            Run = RepairRunDto.FromModel(run)
        });
    }
}

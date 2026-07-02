using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.Repair;

[ApiController]
[Route("api/repair/clear")]
public sealed class ClearRepairRunsController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            throw new BadHttpRequestException("Clearing repair runs requires POST.");

        await dbClient.ClearRepairRunsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new BaseApiResponse());
    }
}

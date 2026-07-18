using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

[ApiController]
[Route("api/maintenance/runs")]
public sealed class MaintenanceRunsController(MaintenanceRunService service) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsGet(HttpContext.Request.Method))
            return MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Get);
        if (!MaintenanceControllerHelpers.TryReadKind(HttpContext, out var kind))
            throw new BadHttpRequestException("Invalid maintenance run kind.");

        var rawLimit = HttpContext.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(rawLimit, out var parsedLimit) ? parsedLimit : 50;
        var runs = await service.GetRunsAsync(kind, limit, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new MaintenanceRunsResponse
        {
            Runs = runs.Select(MaintenanceRunDto.FromModel).ToList(),
        });
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

[ApiController]
[Route("api/maintenance/runs/{id:guid}")]
public sealed class MaintenanceRunController(MaintenanceRunService service) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsGet(HttpContext.Request.Method))
            return MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Get);
        if (!Guid.TryParse(HttpContext.Request.RouteValues["id"]?.ToString(), out var id))
            throw new BadHttpRequestException("Invalid maintenance run id.");

        var run = await service.GetRunAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
        if (run is null)
        {
            return NotFound(new BaseApiResponse
            {
                Status = false,
                Error = $"Maintenance run {id} was not found.",
            });
        }

        return Ok(new MaintenanceRunResponse { Run = MaintenanceRunDto.FromModel(run) });
    }
}

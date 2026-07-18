using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

[ApiController]
[Route("api/maintenance/status")]
public sealed class MaintenanceStatusController(MaintenanceRunService service) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsGet(HttpContext.Request.Method))
            return MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Get);
        if (!MaintenanceControllerHelpers.TryReadKind(HttpContext, out var kind))
            throw new BadHttpRequestException("Invalid maintenance run kind.");

        var status = await service.GetStatusAsync(kind, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new MaintenanceStatusResponse
        {
            ActiveRun = status.ActiveRun is null ? null : MaintenanceRunDto.FromModel(status.ActiveRun),
            LastRun = status.LastRun is null ? null : MaintenanceRunDto.FromModel(status.LastRun),
        });
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Security;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

[ApiController]
[Route("api/maintenance/runs/{id:guid}/cancel")]
public sealed class CancelMaintenanceRunController(MaintenanceRunService service) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            return MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Post);
        if (!Guid.TryParse(HttpContext.Request.RouteValues["id"]?.ToString(), out var id))
            throw new BadHttpRequestException("Invalid maintenance run id.");

        var run = await service.RequestCancellationAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
        if (run is null)
            return Failure(StatusCodes.Status404NotFound, PublicFailureContract.ResourceNotFound());

        return Ok(new MaintenanceRunResponse { Run = MaintenanceRunDto.FromModel(run) });
    }
}

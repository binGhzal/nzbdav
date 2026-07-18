using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Maintenance;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers;

[ApiController]
[Route("api/recreate-strm-files")]
public class RecreateStrmFiles(MaintenanceRunService service) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            return Task.FromResult<IActionResult>(
                MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Post));
        return MaintenanceControllerHelpers.StartRunAsync(
            this,
            service,
            MaintenanceRunKind.RecreateStrmFiles,
            HttpContext.RequestAborted);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Maintenance;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ConvertStrmToSymlinks;

[ApiController]
[Route("api/convert-strm-to-symlinks")]
public class ConvertStrmToSymlinks(MaintenanceRunService service) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(HttpContext.Request.Method))
            return Task.FromResult<IActionResult>(
                MaintenanceControllerHelpers.MethodNotAllowed(this, HttpMethods.Post));
        return MaintenanceControllerHelpers.StartRunAsync(
            this,
            service,
            MaintenanceRunKind.ConvertStrmToSymlinks,
            HttpContext.RequestAborted);
    }
}

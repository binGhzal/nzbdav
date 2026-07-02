using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetWarnings;

public class GetWarningsController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        if (httpContext.GetRequestParam("name") == "clear")
            return Task.FromResult<IActionResult>(Ok(new SabBaseResponse { Status = true }));

        return Task.FromResult<IActionResult>(Ok(new GetWarningsResponse()));
    }
}

using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.GetConfig;

public class GetConfigRequest
{
    public HashSet<string> ConfigKeys { get; init; }

    public GetConfigRequest(HttpContext context)
    {
        var form = context.Request.Form;
        ConfigKeys = form["config-keys"]
            .Where(x => x is not null)
            .Select(x => x!)
            .ToHashSet();
    }
}

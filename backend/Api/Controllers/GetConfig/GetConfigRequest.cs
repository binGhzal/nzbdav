using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.GetConfig;

public class GetConfigRequest
{
    public HashSet<string> ConfigKeys { get; init; }
    public bool IncludeSecrets { get; init; }

    public GetConfigRequest(HttpContext context)
    {
        var form = context.Request.Form;
        ConfigKeys = form["config-keys"]
            .Where(x => x is not null)
            .Select(x => x!)
            .ToHashSet();
        IncludeSecrets = bool.TryParse(form["include-secrets"].FirstOrDefault(), out var includeSecrets)
            && includeSecrets;
    }
}

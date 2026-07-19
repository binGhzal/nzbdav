using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace NzbWebDAV.Api.Controllers.GetConfig;

[ApiController]
[Route("api/get-config")]
public class GetConfigController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetConfigResponse> GetConfig(GetConfigRequest request)
    {
        var configItems = await dbClient.Ctx.ConfigItems
            .AsNoTracking()
            .Where(x => x.ConfigName != TransferV3ReservedConfigPolicy.ImportStateKey
                        && request.ConfigKeys.Contains(x.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        configItems = configItems
            .Where(configItem => !TransferV3ReservedConfigPolicy.IsReserved(configItem.ConfigName)
                                 && !RetiredV1ConfigPolicy.IsRetired(configItem.ConfigName))
            .ToList();

        configItems = configItems.Select(ConfigSecretRedactor.RedactForDisplay).ToList();

        var response = new GetConfigResponse { ConfigItems = configItems };
        return response;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetConfigRequest(HttpContext);
        var response = await GetConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}

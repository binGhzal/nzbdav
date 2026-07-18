using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

[ApiController]
[Route("api/test-arr-connection")]
public class TestArrConnectionController(ConfigManager? configManager = null) : BaseApiController
{
    private async Task<TestArrConnectionResponse> TestArrConnection(TestArrConnectionRequest request)
    {
        var apiKey = ResolveApiKey(request);
        try
        {
            var client = new ArrClient(request.Host, apiKey);
            var apiInfo = await client.GetApiInfo().ConfigureAwait(false);
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = apiInfo.Current?.Length > 0
            };
        }
        catch (Exception e)
        {
            return new TestArrConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }

    internal string ResolveApiKey(TestArrConnectionRequest request)
    {
        if (!ConfigSecretRedactor.IsRedactedSecret(request.ApiKey)) return request.ApiKey;
        var instances = request.Type?.Trim().ToLowerInvariant() switch
        {
            "radarr" => configManager?.GetArrConfig().RadarrInstances,
            "sonarr" => configManager?.GetArrConfig().SonarrInstances,
            "lidarr" => configManager?.GetArrConfig().LidarrInstances,
            _ => null
        };
        var matches = instances?
            .Where(instance => EndpointIdentity.AreEquivalent(instance.Host, request.Host))
            .Take(2)
            .ToArray();
        if (matches is { Length: 1 }
            && !string.IsNullOrEmpty(matches[0].ApiKey)
            && !ConfigSecretRedactor.IsRedactedSecret(matches[0].ApiKey))
        {
            return matches[0].ApiKey;
        }
        throw new BadHttpRequestException(
            "Saved ARR credentials could not be matched to this application and host; re-enter the API key.");
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestArrConnectionRequest(HttpContext);
        var response = await TestArrConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}

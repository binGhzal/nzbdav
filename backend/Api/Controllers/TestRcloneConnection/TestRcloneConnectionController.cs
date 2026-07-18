using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

[ApiController]
[Route("api/test-rclone-connection")]
public class TestRcloneConnectionController(ConfigManager? configManager = null) : BaseApiController
{
    private async Task<TestRcloneConnectionResponse> TestRcloneConnection(TestRcloneConnectionRequest request)
    {
        var password = ResolvePassword(request);
        try
        {
            var result = await RcloneClient
                .TestConnection(
                    request.Host,
                    request.User,
                    password,
                    request.Fs,
                    request.CancellationToken)
                .ConfigureAwait(false);

            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = result.Success,
                Error = result.Error,
                ErrorCategory = result.Success
                    ? null
                    : RcloneClient.GetSafeFailureCategory(result),
                ResponseStatusCode = result.ResponseStatusCode
            };
        }
        catch
        {
            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = "Rclone RC request failed.",
                ErrorCategory = "rclone_rc_request_failed"
            };
        }
    }

    internal string? ResolvePassword(TestRcloneConnectionRequest request)
    {
        if (!ConfigSecretRedactor.IsRedactedSecret(request.Pass)) return request.Pass;
        if (configManager is not null
            && EndpointIdentity.AreEquivalent(configManager.GetRcloneHost(), request.Host)
            && string.Equals(configManager.GetRcloneUser(), request.User, StringComparison.Ordinal)
            && string.Equals(
                NormalizeSelector(configManager.GetRcloneFs()),
                NormalizeSelector(request.Fs),
                StringComparison.Ordinal))
        {
            var savedPassword = configManager.GetRclonePass();
            if (!string.IsNullOrEmpty(savedPassword)
                && !ConfigSecretRedactor.IsRedactedSecret(savedPassword))
            {
                return savedPassword;
            }
        }

        throw new BadHttpRequestException(
            "Saved rclone credentials could not be matched to this host, user, and VFS selector; re-enter the password.");
    }

    private static string NormalizeSelector(string? value) => (value ?? "").Trim();

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestRcloneConnectionRequest(HttpContext);
        var response = await TestRcloneConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}

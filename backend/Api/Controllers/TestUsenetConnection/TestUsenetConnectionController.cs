using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Security;
using Serilog;

namespace NzbWebDAV.Api.Controllers.TestUsenetConnection;

[ApiController]
[Route("api/test-usenet-connection")]
public class TestUsenetConnectionController(ConfigManager? configManager = null) : BaseApiController
{
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(10);
    private TimeSpan _connectionTimeout = DefaultConnectionTimeout;

    internal TestUsenetConnectionController(TimeSpan connectionTimeout)
        : this(configManager: null)
    {
        if (connectionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        _connectionTimeout = connectionTimeout;
    }

    private async Task<IActionResult> TestUsenetConnection(TestUsenetConnectionRequest request)
    {
        var password = ResolvePassword(request, configManager);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(_connectionTimeout);
        try
        {
            using var connection = await UsenetStreamingClient
                .CreateNewConnection(
                    request.ToConnectionDetails(password),
                    timeout.Token)
                .ConfigureAwait(false);
            return Ok(new TestUsenetConnectionResponse { Status = true, Connected = true });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.UsenetConnectionTimeout)
                .Warning("Usenet connection test timed out.");
            return CompatibilityFailure(
                StatusCodes.Status504GatewayTimeout,
                PublicFailureContract.ConnectionTimeout(),
                new TestUsenetConnectionResponse { Status = true, Connected = false });
        }
        catch (CouldNotConnectToUsenetException)
        {
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.UsenetConnectionFailure)
                .Warning("Usenet connection test failed.");
            return Ok(CompatibilityFailure(
                PublicFailureContract.UsenetConnectionFailure(),
                new TestUsenetConnectionResponse { Status = true, Connected = false }));
        }
        catch (CouldNotLoginToUsenetException)
        {
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.UsenetAuthenticationFailure)
                .Warning("Usenet connection authentication failed.");
            return Ok(CompatibilityFailure(
                PublicFailureContract.UsenetConnectionFailure(),
                new TestUsenetConnectionResponse { Status = true, Connected = false }));
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.UsenetUnexpectedFailure)
                .Warning("Usenet connection test failed unexpectedly.");
            throw;
        }
    }

    internal static string ResolvePassword(
        TestUsenetConnectionRequest request,
        ConfigManager? configManager)
    {
        if (!ConfigSecretRedactor.IsRedactedSecret(request.Pass)) return request.Pass;
        var effectiveUseSsl = request.ToConnectionDetails().GetEffectiveUseSsl();
        var matches = configManager?.GetUsenetProviderConfig().Providers
            .Where(provider =>
                string.Equals(provider.Host.Trim(), request.Host.Trim(), StringComparison.OrdinalIgnoreCase)
                && provider.Port == request.Port
                && provider.GetEffectiveUseSsl() == effectiveUseSsl
                && string.Equals(provider.User, request.User, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches is { Length: 1 }
            && !string.IsNullOrEmpty(matches[0].Pass)
            && !ConfigSecretRedactor.IsRedactedSecret(matches[0].Pass))
        {
            return matches[0].Pass;
        }
        throw new BadHttpRequestException(
            "Saved Usenet credentials could not be matched to this host, port, TLS mode, and user; re-enter the password.");
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestUsenetConnectionRequest(HttpContext);
        return await TestUsenetConnection(request).ConfigureAwait(false);
    }
}

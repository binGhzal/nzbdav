using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace NzbWebDAV.Hosting;

internal static partial class BackendListenPolicy
{
    internal const string InvalidListenUrlMessage =
        "ASPNETCORE_URLS must be exactly one private IPv4 loopback HTTP listener with an explicit port.";
    internal const string ConfiguredEndpointsUnavailableMessage =
        "Configured Kestrel endpoints are unavailable in the V1 all-in-one runtime.";
    private const int DefaultPort = 8080;

    internal static int ResolvePort(string? configuredUrl, IConfiguration configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException(ConfiguredEndpointsUnavailableMessage);

        if (string.IsNullOrEmpty(configuredUrl))
            return DefaultPort;

        var match = ListenUrlPattern().Match(configuredUrl);
        if (!match.Success
            || !int.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(InvalidListenUrlMessage);
        }

        return port;
    }

    [GeneratedRegex("\\Ahttp://127\\.0\\.0\\.1:([1-9][0-9]{0,4})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ListenUrlPattern();
}

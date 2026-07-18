using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace NzbWebDAV.Api.Controllers.TestUsenetConnection;

public class TestUsenetConnectionRequest
{
    public string Host { get; init; }
    public string User { get; init; }
    public string Pass { get; init; }
    public int Port { get; init; }
    public bool UseSsl { get; init; }

    public TestUsenetConnectionRequest(HttpContext context)
    {
        Host = context.Request.Form["host"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet host is required");

        User = context.Request.Form["user"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet user is required");

        Pass = context.Request.Form["pass"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet pass is required");

        var port = context.Request.Form["port"].FirstOrDefault()
                   ?? throw new BadHttpRequestException("Usenet port is required");

        var useSsl = context.Request.Form["use-ssl"].FirstOrDefault()
                     ?? throw new BadHttpRequestException("Usenet use-ssl is required");

        Port = !int.TryParse(port, out int portValue) || portValue is < 1 or > 65535
            ? throw new BadHttpRequestException("Invalid usenet port")
            : portValue;

        UseSsl = !bool.TryParse(useSsl, out bool useSslValue)
            ? throw new BadHttpRequestException("Invalid use-ssl value")
            : useSslValue;
    }

    public UsenetProviderConfig.ConnectionDetails ToConnectionDetails(string? resolvedPassword = null)
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Host = Host,
            User = User,
            Pass = resolvedPassword ?? Pass,
            Port = Port,
            UseSsl = UseSsl,
            MaxConnections = 1,
            Type = ProviderType.Disabled
        };
    }
}

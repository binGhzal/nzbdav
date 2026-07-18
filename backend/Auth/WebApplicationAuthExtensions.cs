using Microsoft.AspNetCore.Builder;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Auth;

public static class WebApplicationAuthExtensions
{
    private const string DisableWebdavAuthEnvVar = "DISABLE_WEBDAV_AUTH";

    public static void EnsureWebdavAuthenticationRequired()
    {
        if (!EnvironmentUtil.IsVariableTrue(DisableWebdavAuthEnvVar)) return;

        throw new InvalidOperationException(
            $"{DisableWebdavAuthEnvVar}=true is unsupported in V1. " +
            "Remove the variable and configure WebDAV credentials before using the protocol endpoint.");
    }

    public static void UseWebdavBasicAuthentication(this WebApplication app)
    {
        EnsureWebdavAuthenticationRequired();
        app.UseAuthentication();
    }
}
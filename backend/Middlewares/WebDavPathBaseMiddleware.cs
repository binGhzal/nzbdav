using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace NzbWebDAV.Middlewares;

internal sealed class WebDavPathBaseMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Pinrail-WebDav-Path-Base";
    private const int MaxPathBaseBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove(HeaderName);
        if (values.Count != 1 || !TryDecode(values[0], out var pathBase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Request.PathBase = new PathString(pathBase);
        await next(context).ConfigureAwait(false);
    }

    internal static string StripPathBase(HttpRequest request, string absolutePath)
    {
        var pathBase = request.PathBase.Value;
        if (string.IsNullOrEmpty(pathBase))
            return absolutePath;

        if (!absolutePath.StartsWith(pathBase, StringComparison.Ordinal)
            || (absolutePath.Length > pathBase.Length && absolutePath[pathBase.Length] != '/'))
        {
            throw new BadHttpRequestException("The WebDAV resource path is outside the request base.");
        }

        return absolutePath.Length == pathBase.Length ? "/" : absolutePath[pathBase.Length..];
    }

    private static bool TryDecode(string? encoded, out string pathBase)
    {
        pathBase = string.Empty;
        if (string.IsNullOrEmpty(encoded))
            return false;

        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length == 0
            || bytes.Length > MaxPathBaseBytes
            || !string.Equals(WebEncoders.Base64UrlEncode(bytes), encoded, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            pathBase = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return IsCanonicalPathBase(pathBase);
    }

    private static bool IsCanonicalPathBase(string value)
    {
        if (value.Length == 0
            || value[0] != '/'
            || value[^1] == '/'
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains('%')
            || value.Contains('?')
            || value.Contains('#')
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length >= 2
            && segments[0].Length == 0
            && segments.Skip(1).All(segment => segment.Length > 0 && segment is not "." and not "..")
            && segments[^1] == "protocol";
    }
}

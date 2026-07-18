using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Extensions;

public static class HttpContextExtensions
{
    private const int MaxApiKeyLength = 512;

    public static string? GetRequestParam(this HttpContext httpContext, string key)
    {
        return httpContext.GetQueryParam(key)
            ?? httpContext.GetFormParam(key);
    }

    public static string? GetQueryParam(this HttpContext httpContext, string name)
    {
        return httpContext.Request.Query[name].FirstOrDefault().ToNullIfEmpty();
    }

    public static string? GetFormParam(this HttpContext httpContext, string name)
    {
        return httpContext.Request.HasFormContentType
            ? httpContext.Request.Form[name].FirstOrDefault().ToNullIfEmpty()
            : null;
    }

    public static IEnumerable<string> GetQueryParamValues(this HttpContext httpContext, string name)
    {
        return httpContext.Request.Query[name]
            .Where(x => x is not null)
            .Select(x => x!);
    }

    public static string? GetInternalRequestApiKey(this HttpContext httpContext)
    {
        return ReadSingleCarrier(httpContext.Request.Headers["x-api-key"]);
    }

    public static string? GetProtocolRequestApiKey(this HttpContext httpContext)
    {
        var header = ReadSingleCarrier(httpContext.Request.Headers["x-api-key"]);
        var query = ReadCanonicalParameterCarrier(httpContext.Request.Query.Keys,
            key => httpContext.Request.Query[key]);
        var form = httpContext.Request.HasFormContentType
            ? ReadCanonicalParameterCarrier(httpContext.Request.Form.Keys,
                key => httpContext.Request.Form[key])
            : null;

        if (form is not null && (header is not null || query is not null))
            throw InvalidApiKeyCarrier();
        if (header is not null && query is not null)
        {
            if (!FixedTimeEquals(header, query))
                throw InvalidApiKeyCarrier();
            return header;
        }

        return header ?? query ?? form;
    }

    private static string? ReadCanonicalParameterCarrier(
        IEnumerable<string> keys,
        Func<string, Microsoft.Extensions.Primitives.StringValues> getValues)
    {
        string? candidate = null;
        foreach (var key in keys.Where(key => key.Equals("apikey", StringComparison.OrdinalIgnoreCase)))
        {
            if (!key.Equals("apikey", StringComparison.Ordinal))
                throw InvalidApiKeyCarrier();
            if (candidate is not null)
                throw InvalidApiKeyCarrier();
            candidate = ReadSingleCarrier(getValues(key));
        }

        return candidate;
    }

    private static string? ReadSingleCarrier(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count == 0) return null;
        if (values.Count != 1) throw InvalidApiKeyCarrier();
        var value = values[0];
        if (string.IsNullOrEmpty(value) || value.Length > MaxApiKeyLength)
            throw InvalidApiKeyCarrier();
        return value;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static BadHttpRequestException InvalidApiKeyCarrier()
    {
        return new BadHttpRequestException("Invalid or conflicting API key carriers.");
    }
}

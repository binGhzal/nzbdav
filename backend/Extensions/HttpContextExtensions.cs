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
        var candidates = new List<string>();
        AddCandidate(candidates, httpContext.Request.Headers["x-api-key"]);
        AddCanonicalParameterCandidates(candidates, httpContext.Request.Query.Keys,
            key => httpContext.Request.Query[key]);

        if (httpContext.Request.HasFormContentType)
        {
            AddCanonicalParameterCandidates(candidates, httpContext.Request.Form.Keys,
                key => httpContext.Request.Form[key]);
        }

        if (candidates.Count == 0) return null;
        var selected = candidates[0];
        if (candidates.Skip(1).Any(candidate => !FixedTimeEquals(selected, candidate)))
            throw InvalidApiKeyCarrier();
        return selected;
    }

    private static void AddCanonicalParameterCandidates(
        ICollection<string> candidates,
        IEnumerable<string> keys,
        Func<string, Microsoft.Extensions.Primitives.StringValues> getValues)
    {
        foreach (var key in keys.Where(key => key.Equals("apikey", StringComparison.OrdinalIgnoreCase)))
        {
            if (!key.Equals("apikey", StringComparison.Ordinal))
                throw InvalidApiKeyCarrier();
            AddCandidate(candidates, getValues(key));
        }
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

    private static void AddCandidate(
        ICollection<string> candidates,
        Microsoft.Extensions.Primitives.StringValues values)
    {
        var value = ReadSingleCarrier(values);
        if (value is not null) candidates.Add(value);
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

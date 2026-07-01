using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Extensions;

public static class HttpContextExtensions
{
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

    public static string? GetRequestApiKey(this HttpContext httpContext)
    {
        return httpContext.Request.Headers["x-api-key"].FirstOrDefault()
            ?? httpContext.GetRequestParam("apikey");
    }
}

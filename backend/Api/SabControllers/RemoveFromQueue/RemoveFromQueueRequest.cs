using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueRequest()
{
    public List<Guid> NzoIds { get; init; } = [];
    public bool RemoveAll { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<RemoveFromQueueRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        return new RemoveFromQueueRequest()
        {
            NzoIds = NzoIdsFromQueryParam(httpContext)
                .Concat(await NzoIdsFromRequestBody(httpContext, cancellationToken).ConfigureAwait(false))
                .ToList(),
            RemoveAll = IsRemoveAll(httpContext),
            CancellationToken = cancellationToken
        };
    }

    private static IEnumerable<Guid> NzoIdsFromQueryParam(HttpContext httpContext)
    {
        return httpContext.GetQueryParamValues("value")
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(TryParseGuid)
            .Where(x => x.HasValue)
            .Select(x => x!.Value);
    }

    private static bool IsRemoveAll(HttpContext httpContext)
    {
        return httpContext.GetQueryParamValues("value")
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<Guid>> NzoIdsFromRequestBody(HttpContext httpContext, CancellationToken ct)
    {
        try
        {
            await using var stream = httpContext.Request.Body;
            var deserialized = await JsonSerializer.DeserializeAsync<RequestBody>(stream, cancellationToken: ct).ConfigureAwait(false);
            return deserialized?.NzoIds.Concat(deserialized.CamelCaseNzoIds).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Guid? TryParseGuid(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];

        [JsonPropertyName("nzoIds")]
        public List<Guid> CamelCaseNzoIds { get; set; } = [];
    }
}

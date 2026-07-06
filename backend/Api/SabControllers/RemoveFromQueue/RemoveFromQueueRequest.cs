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
        return SabPagination.ParseValueIdList(httpContext, "all");
    }

    private static bool IsRemoveAll(HttpContext httpContext)
    {
        return httpContext.GetQueryParamValues("value")
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<Guid>> NzoIdsFromRequestBody(HttpContext httpContext, CancellationToken ct)
    {
        var deserialized = await SabPagination
            .ReadOptionalJsonBody<RequestBody>(httpContext, ct)
            .ConfigureAwait(false);
        return deserialized?.NzoIds.Concat(deserialized.CamelCaseNzoIds).ToList() ?? [];
    }

    private class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];

        [JsonPropertyName("nzoIds")]
        public List<Guid> CamelCaseNzoIds { get; set; } = [];
    }
}

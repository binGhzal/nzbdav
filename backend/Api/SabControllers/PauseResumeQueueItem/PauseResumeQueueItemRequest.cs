using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.PauseResumeQueueItem;

public class PauseResumeQueueItemRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }

    public static async Task<PauseResumeQueueItemRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        var requestBody = await ReadRequestBody(httpContext, cancellationToken).ConfigureAwait(false);
        return new PauseResumeQueueItemRequest
        {
            NzoIds = NzoIdsFromQueryParam(httpContext)
                .Concat(requestBody?.NzoIds ?? [])
                .Concat(requestBody?.CamelCaseNzoIds ?? [])
                .Distinct()
                .ToList(),
            CancellationToken = cancellationToken
        };
    }

    private static IEnumerable<Guid> NzoIdsFromQueryParam(HttpContext httpContext)
    {
        return SabPagination.ParseValueIdList(httpContext);
    }

    private static async Task<RequestBody?> ReadRequestBody(HttpContext httpContext, CancellationToken ct)
    {
        return await SabPagination
            .ReadOptionalJsonBody<RequestBody>(httpContext, ct)
            .ConfigureAwait(false);
    }

    private class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];

        [JsonPropertyName("nzoIds")]
        public List<Guid> CamelCaseNzoIds { get; set; } = [];
    }
}

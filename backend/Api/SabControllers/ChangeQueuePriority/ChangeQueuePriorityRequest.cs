using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePriority;

public class ChangeQueuePriorityRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public QueueItem.PriorityOption Priority { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<ChangeQueuePriorityRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        var requestBody = await ReadRequestBody(httpContext, cancellationToken).ConfigureAwait(false);
        return new ChangeQueuePriorityRequest()
        {
            NzoIds = NzoIdsFromQueryParam(httpContext)
                .Concat(requestBody?.NzoIds ?? [])
                .Concat(requestBody?.CamelCaseNzoIds ?? [])
                .Distinct()
                .ToList(),
            Priority = AddFileRequest.MapPriorityOption(
                httpContext.GetRequestParam("value2")
                ?? httpContext.GetRequestParam("priority")
                ?? requestBody?.Priority),
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

        [JsonPropertyName("priority")]
        public string? Priority { get; set; }
    }
}

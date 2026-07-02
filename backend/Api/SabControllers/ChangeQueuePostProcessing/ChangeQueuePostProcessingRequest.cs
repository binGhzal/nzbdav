using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.ChangeQueuePostProcessing;

public class ChangeQueuePostProcessingRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public QueueItem.PostProcessingOption PostProcessing { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<ChangeQueuePostProcessingRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        var requestBody = await ReadRequestBody(httpContext, cancellationToken).ConfigureAwait(false);
        return new ChangeQueuePostProcessingRequest
        {
            NzoIds = NzoIdsFromQueryParam(httpContext)
                .Concat(requestBody?.NzoIds ?? [])
                .Concat(requestBody?.CamelCaseNzoIds ?? [])
                .Distinct()
                .ToList(),
            PostProcessing = AddFileRequest.MapPostProcessingOption(
                httpContext.GetRequestParam("value2")
                ?? httpContext.GetRequestParam("pp")
                ?? requestBody?.PostProcessing),
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

    private static async Task<RequestBody?> ReadRequestBody(HttpContext httpContext, CancellationToken ct)
    {
        if (httpContext.Request.ContentLength is null or 0) return null;

        try
        {
            return await JsonSerializer
                .DeserializeAsync<RequestBody>(httpContext.Request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
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

        [JsonPropertyName("pp")]
        public string? PostProcessing { get; set; }
    }
}

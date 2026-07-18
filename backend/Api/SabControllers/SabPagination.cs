using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using System.Text.Json;

namespace NzbWebDAV.Api.SabControllers;

public static class SabPagination
{
    public const int MaxLimit = 1000;

    public static int ParseStart(string? value)
    {
        if (value is null) return 0;
        if (!int.TryParse(value, out var start))
            throw new BadHttpRequestException("Invalid start parameter");

        return Math.Max(0, start);
    }

    public static int ParseLimit(string? value, string parameterName = "limit")
    {
        return ParseBoundedLimit(value, parameterName, zeroMeansMaximum: false);
    }

    public static int ParseQueueLimit(string? value)
    {
        return ParseBoundedLimit(value, "limit", zeroMeansMaximum: true);
    }

    private static int ParseBoundedLimit(string? value, string parameterName, bool zeroMeansMaximum)
    {
        if (value is null) return MaxLimit;
        if (!int.TryParse(value, out var limit))
            throw new BadHttpRequestException($"Invalid {parameterName} parameter");

        if (zeroMeansMaximum && limit == 0) return MaxLimit;
        return Math.Clamp(limit, 0, MaxLimit);
    }

    public static HashSet<Guid> ParseNzoIdSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var result = new HashSet<Guid>();
        foreach (var id in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(id, out var guid))
                throw new BadHttpRequestException("Invalid nzo_ids parameter");

            result.Add(guid);
        }

        return result;
    }

    public static List<Guid> ParseNzoIdList(string? value)
    {
        return ParseNzoIdSet(value).ToList();
    }

    public static IEnumerable<Guid> ParseValueIdList(HttpContext context, params string[] allowedCommandValues)
    {
        var allowedCommands = allowedCommandValues
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in context.GetQueryParamValues("value")
                     .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (allowedCommands.Contains(value))
                continue;
            if (!Guid.TryParse(value, out var guid))
                throw new BadHttpRequestException("Invalid value parameter");
            yield return guid;
        }
    }

    public static async Task<T?> ReadOptionalJsonBody<T>(HttpContext context, CancellationToken ct)
    {
        if (!HasRequestBody(context)) return default;

        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(context.Request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new BadHttpRequestException("Invalid request body", e);
        }
    }

    private static bool HasRequestBody(HttpContext context)
    {
        if (context.Request.ContentLength is > 0) return true;
        if (context.Request.Body.CanSeek) return context.Request.Body.Length > context.Request.Body.Position;
        return false;
    }
}

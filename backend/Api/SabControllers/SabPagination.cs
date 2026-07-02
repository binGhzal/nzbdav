using Microsoft.AspNetCore.Http;

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
        if (value is null) return MaxLimit;
        if (!int.TryParse(value, out var limit))
            throw new BadHttpRequestException($"Invalid {parameterName} parameter");

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
}

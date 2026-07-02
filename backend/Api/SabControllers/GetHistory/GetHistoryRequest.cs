using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryRequest
{
    public int Start { get; init; } = 0;
    public int Limit { get; init; } = SabPagination.MaxLimit;
    public string? Category { get; init; }
    public List<Guid> NzoIds { get; init; } = [];
    public string? Search { get; init; }
    public HashSet<string> Statuses { get; init; } = [];
    public bool FailedOnly { get; init; }
    public CancellationToken CancellationToken { get; set; }


    public GetHistoryRequest(HttpContext context, ConfigManager configManager)
    {
        var startParam = context.GetRequestParam("start");
        var limitParam = context.GetRequestParam("limit");
        var pageSizeParam = context.GetRequestParam("pageSize");
        var nzoIdsParam = context.GetRequestParam("nzo_ids");
        Category = context.GetRequestParam("category") ?? context.GetRequestParam("cat");
        Search = context.GetRequestParam("search");
        Statuses = ParseStatuses(context.GetRequestParam("status"));
        FailedOnly = context.GetRequestParam("failed_only") == "1";
        CancellationToken = context.RequestAborted;

        Start = SabPagination.ParseStart(startParam);

        // The official Sabnzbd api uses the `limit` param to specify the number of history items
        // that should be returned in the response. However, radarr/sonarr set this param to 60 items
        // which causes problems:
        //   * https://github.com/nzbdav-dev/nzbdav/issues/48
        //   * https://github.com/Sonarr/Sonarr/issues/5452
        //
        // Because of this, NzbDAV added a setting to ignore the `limit` value specified by the Arrs.
        // When this setting is enabled, we always return all history items.
        if (limitParam is not null && !configManager.IsIgnoreSabHistoryLimitEnabled())
        {
            Limit = SabPagination.ParseLimit(limitParam);
        }

        // Even though we may want to ignore the `limit` param from the Arrs, NzbDAV frontend
        // still needs a way to limit the pageSize for pagination. The `pageSize` param is used
        // for this, which takes precedence over the `limit` param. This param is not official to
        // the Sabnzbd api, and is intended to be used only by the NzbDAV frontend.
        if (pageSizeParam is not null)
        {
            Limit = SabPagination.ParseLimit(pageSizeParam, "pageSize");
        }

        NzoIds = SabPagination.ParseNzoIdList(nzoIdsParam);
    }

    private static HashSet<string> ParseStatuses(string? statusParam)
    {
        if (string.IsNullOrWhiteSpace(statusParam)) return [];

        return statusParam
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();
    }
}

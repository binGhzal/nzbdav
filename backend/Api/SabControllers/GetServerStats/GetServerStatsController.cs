using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetServerStats;

public class GetServerStatsController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        var now = DateTime.Now;
        var dayStart = now.Date;
        var weekStart = now.AddDays(-7);
        var monthStart = now.AddDays(-30);
        var completedHistory = dbClient.Ctx.HistoryItems
            .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed);

        var response = new GetServerStatsResponse
        {
            TotalBytes = await completedHistory
                .SumAsync(x => (long?)x.TotalSegmentBytes, RequestContext.RequestAborted)
                .ConfigureAwait(false) ?? 0,
            DayBytes = await completedHistory
                .Where(x => x.CreatedAt >= dayStart)
                .SumAsync(x => (long?)x.TotalSegmentBytes, RequestContext.RequestAborted)
                .ConfigureAwait(false) ?? 0,
            WeekBytes = await completedHistory
                .Where(x => x.CreatedAt >= weekStart)
                .SumAsync(x => (long?)x.TotalSegmentBytes, RequestContext.RequestAborted)
                .ConfigureAwait(false) ?? 0,
            MonthBytes = await completedHistory
                .Where(x => x.CreatedAt >= monthStart)
                .SumAsync(x => (long?)x.TotalSegmentBytes, RequestContext.RequestAborted)
                .ConfigureAwait(false) ?? 0,
            Servers = GetServerStatsResponse.GetServers(ConfigManager.GetUsenetProviderConfig())
        };

        return Ok(response);
    }
}

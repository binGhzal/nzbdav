using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

[ApiController]
[Route("api/get-health-check-history")]
public class GetHealthCheckHistoryController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckHistoryResponse> GetHealthCheckHistory(GetHealthCheckHistoryRequest request)
    {
        var now = DateTime.UtcNow;
        var tomorrow = now.AddDays(1);
        var thirtyDaysAgo = now.AddDays(-30);
        var stats = await dbClient
            .GetHealthCheckStatsAsync(thirtyDaysAgo, tomorrow, request.CancellationToken)
            .ConfigureAwait(false);
        var items = await dbClient.Ctx.HealthCheckResults
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(request.PageSize)
            .ToListAsync(request.CancellationToken)
            .ConfigureAwait(false);

        return new GetHealthCheckHistoryResponse()
        {
            Stats = stats,
            Items = items.Select(GetHealthCheckHistoryResponse.Project).ToList()
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckHistoryRequest(HttpContext);
        var response = await GetHealthCheckHistory(request).ConfigureAwait(false);
        return Ok(response);
    }
}

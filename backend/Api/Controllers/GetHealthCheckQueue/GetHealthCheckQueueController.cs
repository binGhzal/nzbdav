using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckQueue;

[ApiController]
[Route("api/get-health-check-queue")]
public class GetHealthCheckQueueController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetHealthCheckQueueResponse> GetHealthCheckQueue(GetHealthCheckQueueRequest request)
    {
        var snapshot = await dbClient
            .GetHealthCheckQueueSnapshotAsync(request.PageSize, request.CancellationToken)
            .ConfigureAwait(false);

        return new GetHealthCheckQueueResponse()
        {
            UncheckedCount = snapshot.UncheckedCount,
            Items = snapshot.Items.Select(x => new GetHealthCheckQueueResponse.HealthCheckQueueItem()
            {
                Id = x.Id.ToString(),
                Name = x.Name,
                Path = x.Path,
                ReleaseDate = x.ReleaseDate,
                LastHealthCheck = x.LastHealthCheck,
                NextHealthCheck = x.NextHealthCheck,
            }).ToList(),
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetHealthCheckQueueRequest(HttpContext);
        var response = await GetHealthCheckQueue(request).ConfigureAwait(false);
        return Ok(response);
    }
}

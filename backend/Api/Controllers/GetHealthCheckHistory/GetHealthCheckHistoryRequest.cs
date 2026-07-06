using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryRequest
{
    public int PageSize { get; init; } = 20;
    public CancellationToken CancellationToken { get; init; }

    public GetHealthCheckHistoryRequest(HttpContext context)
    {
        var pageSizeParam = context.GetQueryParam("pageSize");
        CancellationToken = context.RequestAborted;

        if (pageSizeParam is not null)
            PageSize = SabPagination.ParseLimit(pageSizeParam, "pageSize");
    }
}

using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.GetHealthCheckHistory;
using NzbWebDAV.Api.Controllers.GetHealthCheckQueue;
using NzbWebDAV.Api.SabControllers;

namespace backend.Tests.Api;

public sealed class HealthCheckRequestTests
{
    [Theory]
    [InlineData("?pageSize=-1", 0)]
    [InlineData("?pageSize=999999", SabPagination.MaxLimit)]
    public void GetHealthCheckQueueRequest_ClampsPageSize(string query, int expected)
    {
        var request = new GetHealthCheckQueueRequest(CreateContext(query));

        Assert.Equal(expected, request.PageSize);
    }

    [Theory]
    [InlineData("?pageSize=-1", 0)]
    [InlineData("?pageSize=999999", SabPagination.MaxLimit)]
    public void GetHealthCheckHistoryRequest_ClampsPageSize(string query, int expected)
    {
        var request = new GetHealthCheckHistoryRequest(CreateContext(query));

        Assert.Equal(expected, request.PageSize);
    }

    private static DefaultHttpContext CreateContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }
}

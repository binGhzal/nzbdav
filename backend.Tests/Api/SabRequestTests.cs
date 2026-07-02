using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Api;

public sealed class SabRequestTests
{
    [Fact]
    public void GetQueueRequest_RejectsMalformedNzoIdsWithBadRequest()
    {
        var context = CreateContext("?nzo_ids=not-a-guid");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetQueueRequest(context));

        Assert.Contains("nzo_ids", ex.Message);
    }

    [Fact]
    public void GetHistoryRequest_RejectsMalformedNzoIdsWithBadRequest()
    {
        var context = CreateContext("?nzo_ids=not-a-guid");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetHistoryRequest(context, new ConfigManager()));

        Assert.Contains("nzo_ids", ex.Message);
    }

    [Theory]
    [InlineData("?limit=999999")]
    [InlineData("?limit=999999&pageSize=999999")]
    public void GetHistoryRequest_ClampsLargeLimits(string query)
    {
        var request = new GetHistoryRequest(CreateContext(query), new ConfigManager());

        Assert.Equal(SabPagination.MaxLimit, request.Limit);
    }

    [Fact]
    public void GetQueueRequest_ClampsLargeLimits()
    {
        var request = new GetQueueRequest(CreateContext("?limit=999999"));

        Assert.Equal(SabPagination.MaxLimit, request.Limit);
    }

    [Fact]
    public void QueueSlot_DoesNotWrapCompletedPercentageToZero()
    {
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };

        var slot = GetQueueResponse.QueueSlot.FromQueueItem(queueItem, progressPercentage: 100);

        Assert.Equal("100", slot.Percentage);
        Assert.Equal("100", slot.TruePercentage);
        Assert.Equal("0.00", slot.SizeLeftInMB);
    }

    private static DefaultHttpContext CreateContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }
}

using Microsoft.AspNetCore.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.ChangeQueuePostProcessing;
using NzbWebDAV.Api.SabControllers.ChangeQueuePriority;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Api.SabControllers.PauseResumeQueueItem;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Api.SabControllers.RemoveFromQueue;
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

    [Fact]
    public async Task RemoveFromQueueRequest_RejectsMalformedValueIdsWithBadRequest()
    {
        var context = CreateContext("?value=not-a-guid");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => RemoveFromQueueRequest.New(context));

        Assert.Contains("value", ex.Message);
    }

    [Theory]
    [InlineData("?value=all")]
    [InlineData("?value=ALL")]
    public async Task RemoveFromQueueRequest_AllCommandDoesNotRequireGuid(string query)
    {
        var request = await RemoveFromQueueRequest.New(CreateContext(query));

        Assert.True(request.RemoveAll);
        Assert.Empty(request.NzoIds);
    }

    [Theory]
    [InlineData("""{"nzo_ids":["not-a-guid"]}""")]
    [InlineData("""{"nzoIds":["not-a-guid"]}""")]
    public async Task RemoveFromQueueRequest_RejectsMalformedBodyIdsWithBadRequest(string body)
    {
        var context = CreateContext("");
        SetJsonBody(context, body);

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => RemoveFromQueueRequest.New(context));

        Assert.Contains("body", ex.Message);
    }

    [Fact]
    public async Task RemoveFromHistoryRequest_RejectsMalformedValueIdsWithBadRequest()
    {
        var context = CreateContext("?value=not-a-guid");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => RemoveFromHistoryRequest.New(context));

        Assert.Contains("value", ex.Message);
    }

    [Theory]
    [InlineData("?value=all", true, false)]
    [InlineData("?value=failed", true, true)]
    [InlineData("?value=FAILED", true, true)]
    public async Task RemoveFromHistoryRequest_CommandValuesDoNotRequireGuid(
        string query,
        bool removeAll,
        bool failedOnly)
    {
        var request = await RemoveFromHistoryRequest.New(CreateContext(query));

        Assert.Equal(removeAll, request.RemoveAll);
        Assert.Equal(failedOnly, request.FailedOnly);
        Assert.Empty(request.NzoIds);
    }

    [Fact]
    public async Task RemoveFromHistoryRequest_RejectsMalformedBodyIdsWithBadRequest()
    {
        var context = CreateContext("");
        SetJsonBody(context, """{"nzo_ids":["not-a-guid"]}""");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => RemoveFromHistoryRequest.New(context));

        Assert.Contains("body", ex.Message);
    }

    [Fact]
    public async Task ChangeQueuePriorityRequest_RejectsMalformedValueIdsWithBadRequest()
    {
        var context = CreateContext("?value=not-a-guid&value2=0");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => ChangeQueuePriorityRequest.New(context));

        Assert.Contains("value", ex.Message);
    }

    [Fact]
    public async Task ChangeQueuePriorityRequest_RejectsMalformedBodyIdsWithBadRequest()
    {
        var context = CreateContext("?value2=0");
        SetJsonBody(context, """{"nzo_ids":["not-a-guid"],"priority":"0"}""");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => ChangeQueuePriorityRequest.New(context));

        Assert.Contains("body", ex.Message);
    }

    [Fact]
    public async Task ChangeQueuePostProcessingRequest_RejectsMalformedValueIdsWithBadRequest()
    {
        var context = CreateContext("?value=not-a-guid&value2=0");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => ChangeQueuePostProcessingRequest.New(context));

        Assert.Contains("value", ex.Message);
    }

    [Fact]
    public async Task ChangeQueuePostProcessingRequest_RejectsMalformedBodyIdsWithBadRequest()
    {
        var context = CreateContext("?value2=0");
        SetJsonBody(context, """{"nzo_ids":["not-a-guid"],"pp":"0"}""");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => ChangeQueuePostProcessingRequest.New(context));

        Assert.Contains("body", ex.Message);
    }

    [Fact]
    public async Task PauseResumeQueueItemRequest_RejectsMalformedValueIdsWithBadRequest()
    {
        var context = CreateContext("?value=not-a-guid");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => PauseResumeQueueItemRequest.New(context));

        Assert.Contains("value", ex.Message);
    }

    [Fact]
    public async Task PauseResumeQueueItemRequest_RejectsMalformedBodyIdsWithBadRequest()
    {
        var context = CreateContext("");
        SetJsonBody(context, """{"nzo_ids":["not-a-guid"]}""");

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => PauseResumeQueueItemRequest.New(context));

        Assert.Contains("body", ex.Message);
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
    public void GetQueueRequest_MapsZeroLimitToBoundedUnlimited()
    {
        var request = new GetQueueRequest(CreateContext("?limit=0"));

        Assert.Equal(SabPagination.MaxLimit, request.Limit);
    }

    [Fact]
    public void GetQueueRequest_DoesNotMapNegativeLimitToMaximum()
    {
        var request = new GetQueueRequest(CreateContext("?limit=-1"));

        Assert.Equal(0, request.Limit);
    }

    [Fact]
    public void GetHistoryRequest_KeepsZeroLimitEndpointSpecific()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.ignore-history-limit", ConfigValue = "false" }
        ]);
        var request = new GetHistoryRequest(CreateContext("?limit=0"), configManager);

        Assert.Equal(0, request.Limit);
    }

    [Fact]
    public void PageSizeParsingKeepsZeroLimitSemantics()
    {
        Assert.Equal(0, SabPagination.ParseLimit("0", "pageSize"));
    }

    [Fact]
    public void GetQueueRequest_NormalizesStatusAliases()
    {
        var request = new GetQueueRequest(CreateContext("?status=Downloading,QuickCheck,PP,Repairing,Queued,Paused"));

        Assert.Equal(
            new HashSet<string> { "downloading", "verifying", "moving", "repairing", "queued", "paused" },
            request.Statuses);
    }

    [Fact]
    public void GetQueueRequest_TreatsAllStatusAsNoFilter()
    {
        var request = new GetQueueRequest(CreateContext("?status=all"));

        Assert.Empty(request.Statuses);
    }

    [Fact]
    public void GetQueueRequest_RejectsInvalidStatusFilter()
    {
        var context = CreateContext("?status=random");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetQueueRequest(context));

        Assert.Contains("status", ex.Message);
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

    [Fact]
    public void AddFileComputesTotalSegmentBytesFromEveryNzbSegment()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file poster="poster" date="1" subject="Example">
                <segments>
                  <segment bytes="10" number="1">segment-1</segment>
                  <segment bytes="20" number="2">segment-2</segment>
                  <segment bytes="30" number="3">segment-3</segment>
                </segments>
              </file>
            </nzb>
            """));

        var method = typeof(AddFileController).GetMethod(
            "ComputeTotalSegmentBytes",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var totalBytes = Assert.IsType<long>(method.Invoke(null, [stream]));

        Assert.Equal(60, totalBytes);
    }

    [Fact]
    public void GetQueueResponse_DeserializesSabTimeleftValues()
    {
        var response = JsonSerializer.Deserialize<GetQueueResponse>(
            """
            {
              "queue": {
                "timeleft": "1:02:03:04",
                "slots": [
                  { "timeleft": "0:00:10:05" }
                ]
              }
            }
            """);

        Assert.NotNull(response);
        Assert.Equal(new TimeSpan(1, 2, 3, 4), response.Queue.TimeLeft);
        Assert.Equal(new TimeSpan(0, 0, 10, 5), response.Queue.Slots.Single().TimeLeft);
    }

    private static DefaultHttpContext CreateContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static void SetJsonBody(DefaultHttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";
    }
}

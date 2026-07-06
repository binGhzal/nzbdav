using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Api;

public sealed class GetWebdavItemRequestTests
{
    [Fact]
    public void ConstructorRejectsMalformedRangeHeader()
    {
        var context = CreateContext("bytes=bad-10");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetWebdavItemRequest(context));

        Assert.Equal("Invalid Range header.", ex.Message);
    }

    [Fact]
    public void ConstructorRejectsInvertedRangeHeader()
    {
        var context = CreateContext("bytes=20-10");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetWebdavItemRequest(context));

        Assert.Equal("Invalid Range header.", ex.Message);
    }

    [Fact]
    public void ConstructorParsesOpenEndedRangeHeader()
    {
        var context = CreateContext("bytes=10-");

        var request = new GetWebdavItemRequest(context);

        Assert.Equal(10, request.RangeStart);
        Assert.Null(request.RangeEnd);
    }

    [Fact]
    public void ConstructorParsesSuffixRangeHeader()
    {
        var context = CreateContext("bytes=-500");

        var request = new GetWebdavItemRequest(context);

        Assert.Null(request.RangeStart);
        Assert.Null(request.RangeEnd);
        Assert.Equal(500, request.RangeSuffixLength);
    }

    [Fact]
    public void ConstructorRejectsZeroLengthSuffixRangeHeader()
    {
        var context = CreateContext("bytes=-0");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetWebdavItemRequest(context));

        Assert.Equal("Invalid Range header.", ex.Message);
    }

    private static DefaultHttpContext CreateContext(string rangeHeader)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" }
        ]);
        var path = ".ids/movie.mkv";
        var context = new DefaultHttpContext();
        context.Request.Path = $"/view/{path}";
        context.Request.QueryString = QueryString.Create(
            "downloadKey",
            GetWebdavItemRequest.GenerateDownloadKey("test-strm-key", path));
        context.Request.Headers.Range = rangeHeader;
        context.Items["configManager"] = configManager;
        return context;
    }
}

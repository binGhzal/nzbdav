using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class GetWebdavItemRequestTests : IDisposable
{
    private const string InternalFixtureKey = "fixture-internal";
    private readonly string? _previousApiKey =
        Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");

    public GetWebdavItemRequestTests()
    {
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", InternalFixtureKey);
    }

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
        var path = ".ids/movie.mkv";
        var context = new DefaultHttpContext();
        context.Request.Path = $"/view/{path}";
        context.Request.QueryString = QueryString.Create(
            "downloadKey",
            GetWebdavItemRequest.GenerateDownloadKey(InternalFixtureKey, path));
        context.Request.Headers.Range = rangeHeader;
        return context;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);
    }
}

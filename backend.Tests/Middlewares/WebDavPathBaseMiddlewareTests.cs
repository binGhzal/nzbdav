using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Middlewares;

namespace backend.Tests.Middlewares;

public sealed class WebDavPathBaseMiddlewareTests
{
    [Theory]
    [InlineData("/protocol")]
    [InlineData("/nzbdav/protocol")]
    [InlineData("/edge/apps/nzbdav/protocol")]
    public async Task InvokeAsyncAppliesOneCanonicalEncodedPathBaseAndRemovesTransportHeader(string pathBase)
    {
        var nextCalled = false;
        var middleware = new WebDavPathBaseMiddleware(context =>
        {
            nextCalled = true;
            Assert.Equal(pathBase, context.Request.PathBase.Value);
            Assert.False(context.Request.Headers.ContainsKey(WebDavPathBaseMiddleware.HeaderName));
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/README";
        context.Request.Headers[WebDavPathBaseMiddleware.HeaderName] = Encode(pathBase);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("/README", context.Request.Path.Value);
    }

    [Fact]
    public async Task InvokeAsyncLeavesDirectBackendRequestsUnchangedWhenMetadataIsAbsent()
    {
        var nextCalled = false;
        var middleware = new WebDavPathBaseMiddleware(context =>
        {
            nextCalled = true;
            Assert.Empty(context.Request.PathBase.Value ?? string.Empty);
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/README";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    public static TheoryData<StringValues> InvalidMetadata => new()
    {
        new StringValues([Encode("/protocol"), Encode("/protocol")]),
        new StringValues(string.Empty),
        new StringValues("="),
        new StringValues("L3Byb3RvY29s="),
        new StringValues(Encode("protocol")),
        new StringValues(Encode("/")),
        new StringValues(Encode("/protocol/")),
        new StringValues(Encode("//protocol")),
        new StringValues(Encode("/nzbdav//protocol")),
        new StringValues(Encode("/nzbdav/../protocol")),
        new StringValues(Encode("/nzbdav/%2f/protocol")),
        new StringValues(Encode("/nzbdav\\protocol")),
        new StringValues(Encode("/nzbdav/PROTOCOL")),
        new StringValues(Encode($"/{new string('a', 8192)}/protocol")),
    };

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public async Task InvokeAsyncRejectsInvalidMetadataBeforeDownstream(StringValues metadata)
    {
        var nextCalled = false;
        var middleware = new WebDavPathBaseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Headers[WebDavPathBaseMiddleware.HeaderName] = metadata;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(context.Request.Headers.ContainsKey(WebDavPathBaseMiddleware.HeaderName));
    }

    [Theory]
    [InlineData("/protocol", "/protocol", "/")]
    [InlineData("/protocol", "/protocol/README", "/README")]
    [InlineData("/nzbdav/protocol", "/nzbdav/protocol/content/movie.mkv", "/content/movie.mkv")]
    [InlineData("/edge/apps/nzbdav/protocol", "/edge/apps/nzbdav/protocol/.ids/a/file.mkv", "/.ids/a/file.mkv")]
    public void StripPathBaseReturnsTheBackendRelativeStorePath(
        string pathBase,
        string absolutePath,
        string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.PathBase = pathBase;

        Assert.Equal(expected, WebDavPathBaseMiddleware.StripPathBase(request, absolutePath));
    }

    [Theory]
    [InlineData("/nzbdav/protocol", "/protocol/README")]
    [InlineData("/nzbdav/protocol", "/nzbdav/protocolish/README")]
    [InlineData("/nzbdav/protocol", "/nzbdav/protoco")]
    public void StripPathBaseRejectsAUriOutsideTheValidatedPrefix(string pathBase, string absolutePath)
    {
        var request = new DefaultHttpContext().Request;
        request.PathBase = pathBase;

        Assert.Throws<BadHttpRequestException>(() =>
            WebDavPathBaseMiddleware.StripPathBase(request, absolutePath));
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;

namespace backend.Tests.Api;

public sealed class TestUsenetConnectionRequestTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public void ConstructorRejectsOutOfRangePorts(string port)
    {
        var context = CreateContext(port);

        var ex = Assert.Throws<BadHttpRequestException>(() => new TestUsenetConnectionRequest(context));

        Assert.Equal("Invalid usenet port", ex.Message);
    }

    [Theory]
    [InlineData("119", 119)]
    [InlineData("563", 563)]
    [InlineData("65535", 65535)]
    public void ConstructorAcceptsValidPorts(string port, int expected)
    {
        var request = new TestUsenetConnectionRequest(CreateContext(port));

        Assert.Equal(expected, request.Port);
    }

    private static DefaultHttpContext CreateContext(string port)
    {
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = "news.example.test",
            ["user"] = "user",
            ["pass"] = "pass",
            ["port"] = port,
            ["use-ssl"] = "true"
        });
        return context;
    }
}

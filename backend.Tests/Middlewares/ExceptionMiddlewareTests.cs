using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Middlewares;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using backend.Tests.Security;
using backend.Tests.Services;

namespace backend.Tests.Middlewares;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ExceptionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsyncConvertsBadHttpRequestExceptionToBadRequest()
    {
        var middleware = new ExceptionMiddleware(
            _ => throw new BadHttpRequestException("Invalid Range header."),
            new ConfigManager());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsyncConvertsDavItemFileNotFoundToNotFound()
    {
        var middleware = new ExceptionMiddleware(
            context =>
            {
                context.Items["DavItem"] = new DavItem
                {
                    Id = Guid.NewGuid(),
                    IdPrefix = "abcde",
                    CreatedAt = DateTime.UtcNow,
                    ParentId = DavItem.ContentFolder.Id,
                    Name = "Missing.mkv",
                    FileSize = 1024,
                    Type = DavItem.ItemType.UsenetFile,
                    SubType = DavItem.ItemSubType.NzbFile,
                    Path = "/content/Missing.mkv"
                };
                throw new FileNotFoundException("Could not find nzb file.");
            },
            new ConfigManager());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("bad-request", StatusCodes.Status400BadRequest, "invalid_request", "The request is invalid.")]
    [InlineData("internal", StatusCodes.Status500InternalServerError, "internal_error", "The request could not be completed.")]
    public async Task InvokeAsyncUsesBoundedEnvelopeForNonDavFailures(
        string failureKind,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var context = CreateContext();
        var middleware = new ExceptionMiddleware(
            _ => throw (failureKind == "bad-request"
                ? new BadHttpRequestException(PublicFailureCanary.Composite)
                : PublicFailureCanary.NestedException),
            new ConfigManager());

        var handled = true;
        try
        {
            await middleware.InvokeAsync(context);
        }
        catch
        {
            handled = false;
        }

        Assert.True(handled);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        var body = await PublicFailureCanary.ReadBodyAsync(context.Response);
        PublicFailureCanary.AssertSafe(body);
        Assert.True(body.StartsWith('{'));
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(expectedMessage, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("code").GetString());
        AssertCorrelation(context, json.RootElement.GetProperty("correlation_id").GetString());
    }

    [Theory]
    [InlineData("missing", StatusCodes.Status404NotFound, null, "content_unavailable")]
    [InlineData("retry", StatusCodes.Status503ServiceUnavailable, "30", "content_temporarily_unavailable")]
    [InlineData("range", StatusCodes.Status404NotFound, null, "content_range_unavailable")]
    [InlineData("internal", StatusCodes.Status500InternalServerError, null, "internal_error")]
    public async Task InvokeAsyncPreservesDavStatusAndNoBodyWhileSanitizingLogs(
        string failureKind,
        int expectedStatus,
        string? expectedRetryAfter,
        string expectedCode)
    {
        var sink = new CollectingSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var context = CreateContext();
            context.Request.Path = "/public-failure";
            context.Request.Headers.Range = PublicFailureCanary.Composite;
            context.Items["DavItem"] = CreateDavItem(PublicFailureCanary.Composite);
            var middleware = new ExceptionMiddleware(
                _ => throw failureKind switch
                {
                    "missing" => new FileNotFoundException(PublicFailureCanary.Composite),
                    "retry" => new RetryableDownloadException(PublicFailureCanary.Composite, PublicFailureCanary.NestedException),
                    "range" => new SeekPositionNotFoundException(PublicFailureCanary.Composite),
                    _ => PublicFailureCanary.NestedException,
                },
                new ConfigManager());

            await middleware.InvokeAsync(context);

            Assert.Equal(expectedStatus, context.Response.StatusCode);
            Assert.Equal(expectedRetryAfter, context.Response.Headers.RetryAfter.FirstOrDefault());
            Assert.Equal(expectedCode, context.Response.Headers["X-Error-Code"]);
            Assert.Matches("^[0-9a-f]{32}$", context.Response.Headers["X-Correlation-ID"].ToString());
            Assert.Empty(await PublicFailureCanary.ReadBodyAsync(context.Response));
            PublicFailureCanary.AssertSafe(sink.Render());
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task InvokeAsyncKeepsHeadFailureBodyEmpty()
    {
        var context = CreateContext();
        context.Request.Method = HttpMethods.Head;
        var middleware = new ExceptionMiddleware(
            _ => throw new BadHttpRequestException(PublicFailureCanary.Composite),
            new ConfigManager());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var body = await PublicFailureCanary.ReadBodyAsync(context.Response);
        PublicFailureCanary.AssertSafe(body);
        Assert.True(body.Length == 0);
        Assert.Matches("^[0-9a-f]{32}$", context.Response.Headers["X-Correlation-ID"].ToString());
    }

    [Fact]
    public async Task InvokeAsyncConvertsMissingUsenetArticleToBodylessContentFailure()
    {
        var context = CreateContext();
        var middleware = new ExceptionMiddleware(
            _ => throw new UsenetArticleNotFoundException(PublicFailureCanary.Composite),
            new ConfigManager());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("content_unavailable", context.Response.Headers["X-Error-Code"]);
        Assert.Empty(await PublicFailureCanary.ReadBodyAsync(context.Response));
    }

    [Fact]
    public async Task InvokeAsyncUses499EnvelopeForAbortedRequestBeforeResponseStarts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = CreateContext();
        context.RequestAborted = cancellation.Token;
        var middleware = new ExceptionMiddleware(
            _ => throw new OperationCanceledException(PublicFailureCanary.Composite),
            new ConfigManager());

        await middleware.InvokeAsync(context);

        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal("client_closed_request", context.Response.Headers["X-Error-Code"]);
        PublicFailureCanary.AssertSafe(await PublicFailureCanary.ReadBodyAsync(context.Response));
    }

    [Fact]
    public async Task InvokeAsyncRethrowsAfterResponseStartsWithoutRewritingPartialResponse()
    {
        var original = new InvalidOperationException(PublicFailureCanary.Composite);
        var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("partial-canary"));
        var feature = new StartedResponseFeature(body)
        {
            StatusCode = StatusCodes.Status206PartialContent,
        };
        feature.Headers["X-Existing"] = "preserved";
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);
        var middleware = new ExceptionMiddleware(_ => throw original, new ConfigManager());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Same(original, thrown);
        Assert.Equal(StatusCodes.Status206PartialContent, feature.StatusCode);
        Assert.Equal("preserved", feature.Headers["X-Existing"]);
        Assert.False(feature.Headers.ContainsKey("X-Error-Code"));
        Assert.Equal("partial-canary", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task InvokeAsyncPreservesSuccessfulPassThroughResponse()
    {
        var context = CreateContext();
        var middleware = new ExceptionMiddleware(
            async current =>
            {
                current.Response.StatusCode = StatusCodes.Status202Accepted;
                current.Response.Headers["X-Result"] = "accepted";
                await current.Response.WriteAsync("ok");
            },
            new ConfigManager());

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal("accepted", context.Response.Headers["X-Result"]);
        Assert.Equal("ok", await PublicFailureCanary.ReadBodyAsync(context.Response));
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DavItem CreateDavItem(string path) => new()
    {
        Id = Guid.NewGuid(),
        IdPrefix = "abcde",
        CreatedAt = DateTime.UtcNow,
        ParentId = DavItem.ContentFolder.Id,
        Name = "Failure.mkv",
        FileSize = 1024,
        Type = DavItem.ItemType.UsenetFile,
        SubType = DavItem.ItemSubType.NzbFile,
        Path = path,
    };

    private static void AssertCorrelation(HttpContext context, string? correlationId)
    {
        Assert.Matches("^[0-9a-f]{32}$", correlationId ?? string.Empty);
        Assert.Equal(correlationId, context.Response.Headers["X-Correlation-ID"]);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);

        public string Render() => string.Join(
            "|",
            _events.Select(logEvent => string.Concat(
                logEvent.RenderMessage(),
                "|",
                logEvent.Exception?.ToString(),
                "|",
                string.Join("|", logEvent.Properties.Select(property => property.Value.ToString())))));
    }

    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}

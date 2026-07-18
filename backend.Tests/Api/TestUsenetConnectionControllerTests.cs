using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class TestUsenetConnectionControllerTests
{
    [Fact]
    public async Task HandleApiRequest_DisposesSuccessfulTestConnection()
    {
        await using var server = await FakeNntpServer.StartAsync();
        var controller = new TestUsenetConnectionController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext(server.Port)
            }
        };

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TestUsenetConnectionResponse>(ok.Value);
        Assert.True(response.Connected);
        await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HandleApiRequest_LoginFailureNeverLogsProviderResponseOrPassword()
    {
        const string password = "provider-secret-log-marker";
        await using var server = await FakeNntpServer.StartAsync(rejectPassword: true);
        using var logger = new LoggerScope();
        var controller = new TestUsenetConnectionController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext(server.Port, password)
            }
        };

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TestUsenetConnectionResponse>(ok.Value);
        Assert.False(response.Connected);
        Assert.Contains("login error", logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(password, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTHINFO PASS", logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleApiRequest_ConnectionTimeoutReturnsGatewayTimeoutAndDisposesConnection()
    {
        await using var server = await FakeNntpServer.StartAsync(holdGreeting: true);
        var controller = new TestUsenetConnectionController(TimeSpan.FromMilliseconds(100))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateContext(server.Port)
            }
        };

        var result = await HandleWithApiKeyAsync(controller).WaitAsync(TimeSpan.FromSeconds(2));

        var timeout = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, timeout.StatusCode);
        var response = Assert.IsType<TestUsenetConnectionResponse>(timeout.Value);
        Assert.False(response.Connected);
        await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HandleApiRequest_RequestCancellationPropagatesAndDisposesConnection()
    {
        await using var server = await FakeNntpServer.StartAsync(holdGreeting: true);
        using var requestCancellation = new CancellationTokenSource();
        var context = CreateContext(server.Port);
        context.RequestAborted = requestCancellation.Token;
        var controller = new TestUsenetConnectionController(TimeSpan.FromSeconds(5))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context
            }
        };
        var handling = HandleWithApiKeyAsync(controller);
        await server.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await requestCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handling.WaitAsync(TimeSpan.FromSeconds(2)));
        await server.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static DefaultHttpContext CreateContext(int port, string password = "pass")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = "127.0.0.1",
            ["user"] = "user",
            ["pass"] = password,
            ["port"] = port.ToString(),
            ["use-ssl"] = "false"
        });
        return context;
    }

    private static async Task<IActionResult> HandleWithApiKeyAsync(TestUsenetConnectionController controller)
    {
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            return await controller.HandleApiRequest();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private sealed class FakeNntpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly bool _rejectPassword;
        private readonly bool _holdGreeting;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        private FakeNntpServer(TcpListener listener, bool rejectPassword, bool holdGreeting)
        {
            _listener = listener;
            _rejectPassword = rejectPassword;
            _holdGreeting = holdGreeting;
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeAsync);
        }

        public int Port { get; }
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Task<FakeNntpServer> StartAsync(
            bool rejectPassword = false,
            bool holdGreeting = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeNntpServer(listener, rejectPassword, holdGreeting));
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Connected.TrySetResult();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                if (_holdGreeting)
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is not null) continue;
                        Disconnected.TrySetResult();
                        return;
                    }

                    return;
                }

                await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("200 nzbdav test server ready").ConfigureAwait(false);

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line == null)
                    {
                        Disconnected.TrySetResult();
                        return;
                    }

                    if (line.StartsWith("AUTHINFO USER ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("381 password required").ConfigureAwait(false);
                        continue;
                    }

                    if (line.StartsWith("AUTHINFO PASS ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync(_rejectPassword
                            ? $"481 authentication rejected: {line}"
                            : "281 authentication accepted").ConfigureAwait(false);
                        continue;
                    }

                    if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("205 closing connection").ConfigureAwait(false);
                        continue;
                    }

                    await writer.WriteLineAsync("200 ok").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            _cts.Dispose();
        }
    }

    private sealed class LoggerScope : IDisposable
    {
        private readonly CollectingLogSink _sink = new();
        private readonly Serilog.ILogger _previous = Log.Logger;
        private readonly Serilog.Core.Logger _logger;

        public LoggerScope()
        {
            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(_sink)
                .CreateLogger();
            Log.Logger = _logger;
        }

        public string Rendered => _sink.Rendered;

        public void Dispose()
        {
            Log.Logger = _previous;
            _logger.Dispose();
        }
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();

        public string Rendered => string.Join('\n', _events);

        public void Emit(LogEvent logEvent)
        {
            _events.Enqueue(logEvent.RenderMessage());
            if (logEvent.Exception is not null)
                _events.Enqueue(logEvent.Exception.ToString());
        }
    }
}

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;

namespace backend.Tests.Api;

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

    private static DefaultHttpContext CreateContext(int port)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = "127.0.0.1",
            ["user"] = "user",
            ["pass"] = "pass",
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
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        private FakeNntpServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeAsync);
        }

        public int Port { get; }
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Task<FakeNntpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeNntpServer(listener));
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
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
                        await writer.WriteLineAsync("281 authentication accepted").ConfigureAwait(false);
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
}

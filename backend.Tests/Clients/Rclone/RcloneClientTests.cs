using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Clients.Rclone;

public sealed class RcloneClientTests
{
    [Fact]
    public async Task TestConnection_ReturnsFailureForMalformedSuccessfulRcResponse()
    {
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            "<html>not json</html>");

        var response = await RcloneClient.TestConnection(server.Url, user: null, pass: null);

        Assert.False(response.Success);
        Assert.Contains("Malformed rclone RC response", response.Error);
    }

    [Fact]
    public async Task IsAvailable_ReturnsFalseWhenNoOpReturnsMalformedResponse()
    {
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            "<html>not json</html>");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);

        var available = await RcloneClient.IsAvailable();

        Assert.False(available);
    }

    [Fact]
    public async Task ForgetVfsPaths_HonorsCancellationWhileWaitingForRcResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RcloneClient.ForgetVfsPaths(["/content/movie"], cts.Token));
    }

    [Fact]
    public async Task TestConnection_RetriesTransientRcSocketFailure()
    {
        await using var server = await ResetThenOkHttpServer.StartAsync("""{"version":"v1.70.0"}""");

        var response = await RcloneClient.TestConnection(server.Url, user: null, pass: null);

        Assert.True(response.Success);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public void ConfigChange_ClearingRcloneHostUpdatesClientHostToNull()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" }
        ]);
        RcloneClient.Initialize(configManager);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "" }
        ]);

        Assert.Null(RcloneClient.Host);
    }

    [Fact]
    public void Initialize_TreatsWhitespaceOnlyRcloneHostAsUnset()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "   " }
        ]);

        RcloneClient.Initialize(configManager);

        Assert.Null(RcloneClient.Host);
    }

    private sealed class SingleResponseHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private Task _serverTask;

        private SingleResponseHttpServer(TcpListener listener, Task serverTask, string url)
        {
            _listener = listener;
            _serverTask = serverTask;
            Url = url;
        }

        public string Url { get; }

        public static Task<SingleResponseHttpServer> StartAsync(HttpStatusCode statusCode, string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(() => ServeOneRequestAsync(listener, statusCode, body));
            return Task.FromResult(new SingleResponseHttpServer(
                listener,
                serverTask,
                $"http://127.0.0.1:{port}"));
        }

        private static async Task ServeOneRequestAsync(TcpListener listener, HttpStatusCode statusCode, string body)
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await using var stream = client.GetStream();

            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer).ConfigureAwait(false);

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header = string.Join("\r\n", [
                $"HTTP/1.1 {(int)statusCode} {statusCode}",
                "Content-Type: text/html; charset=utf-8",
                $"Content-Length: {bodyBytes.Length}",
                "Connection: close",
                "",
                ""
            ]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serverTask.ConfigureAwait(false);
        }
    }

    private sealed class HangingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private Task _serverTask;
        private readonly CancellationTokenSource _cts = new();

        private HangingHttpServer(TcpListener listener, Task serverTask, string url)
        {
            _listener = listener;
            _serverTask = serverTask;
            Url = url;
        }

        public string Url { get; }

        public static Task<HangingHttpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new HangingHttpServer(listener, Task.CompletedTask, $"http://127.0.0.1:{port}");
            server._serverTask = Task.Run(server.ServeOneRequestAsync);
            return Task.FromResult(server);
        }

        private async Task ServeOneRequestAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _serverTask.ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    private sealed class ResetThenOkHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _body;
        private readonly CancellationTokenSource _cts = new();
        private Task _serverTask;
        private int _requestCount;

        private ResetThenOkHttpServer(TcpListener listener, string body, string url)
        {
            _listener = listener;
            _body = body;
            Url = url;
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        public static Task<ResetThenOkHttpServer> StartAsync(string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new ResetThenOkHttpServer(
                listener,
                body,
                $"http://127.0.0.1:{port}"));
        }

        private async Task ServeAsync()
        {
            try
            {
                using (var firstClient = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _requestCount);
                    firstClient.LingerState = new LingerOption(enable: true, seconds: 0);
                }

                using var secondClient = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _requestCount);
                await using var stream = secondClient.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);

                var bodyBytes = Encoding.UTF8.GetBytes(_body);
                var header = string.Join("\r\n", [
                    "HTTP/1.1 200 OK",
                    "Content-Type: application/json",
                    $"Content-Length: {bodyBytes.Length}",
                    "Connection: close",
                    "",
                    ""
                ]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _serverTask.ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}

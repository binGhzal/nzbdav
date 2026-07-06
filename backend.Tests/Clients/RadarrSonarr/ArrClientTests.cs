using System.Net;
using System.Net.Sockets;
using NzbWebDAV.Clients.RadarrSonarr;

namespace backend.Tests.Clients.RadarrSonarr;

public sealed class ArrClientTests
{
    [Fact]
    public async Task GetApiInfo_HonorsCancellationWhileWaitingForArrResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var client = new ArrClient(server.Url, "api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetApiInfo(cts.Token));
    }

    private sealed class HangingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _serverTask;

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
}

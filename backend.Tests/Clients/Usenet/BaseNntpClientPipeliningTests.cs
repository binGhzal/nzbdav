using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Usenet;

namespace backend.Tests.Clients.Usenet;

public sealed class BaseNntpClientPipeliningTests
{
    [Fact]
    public async Task StatPipelinedAsyncSendsBatchBeforeWaitingForFirstResponse()
    {
        await using var server = await RecordingNntpServer.StartAsync();
        using var client = new BaseNntpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            server.Port,
            useSsl: false,
            timeout.Token);

        var results = await client.StatPipelinedAsync(["segment-1", "segment-2"], timeout.Token);

        Assert.True(server.SecondStatArrivedBeforeFirstResponse);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.ArticleExists));
    }

    private sealed class RecordingNntpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        private RecordingNntpServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _serverTask = RunAsync();
        }

        public int Port { get; }
        public bool SecondStatArrivedBeforeFirstResponse { get; private set; }

        public static Task<RecordingNntpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new RecordingNntpServer(listener));
        }

        private async Task RunAsync()
        {
            using var tcpClient = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };

            await writer.WriteLineAsync("200 fake server ready").ConfigureAwait(false);
            var firstCommand = await reader.ReadLineAsync().ConfigureAwait(false);
            Assert.Equal("STAT <segment-1>", firstCommand);

            using (var waitForSecondCommand = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                var secondCommandTask = reader.ReadLineAsync(waitForSecondCommand.Token).AsTask();
                try
                {
                    var secondCommand = await secondCommandTask.ConfigureAwait(false);
                    SecondStatArrivedBeforeFirstResponse = true;
                    Assert.Equal("STAT <segment-2>", secondCommand);
                }
                catch (OperationCanceledException)
                {
                    SecondStatArrivedBeforeFirstResponse = false;
                }
            }

            await writer.WriteLineAsync("223 0 <segment-1> article exists").ConfigureAwait(false);
            if (!SecondStatArrivedBeforeFirstResponse)
            {
                var secondCommand = await reader.ReadLineAsync().ConfigureAwait(false);
                Assert.Equal("STAT <segment-2>", secondCommand);
            }

            await writer.WriteLineAsync("223 0 <segment-2> article exists").ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
}

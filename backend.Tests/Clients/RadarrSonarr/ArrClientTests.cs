using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;

namespace backend.Tests.Clients.RadarrSonarr;

public sealed class ArrClientTests
{
    [Fact]
    public async Task DownloadedEpisodesScanAsync_PostsExactCommandWithoutNormalizingArrValues()
    {
        const string apiKey = "sonarr-secret-api-key";
        const string path = @"C:\ARR Completed\Series.Name\S01E01.mkv";
        const string downloadId = @"ABCdef-0123:/\";
        await using var server = await RecordingHttpServer.StartAsync("{\"id\":17}");
        var client = new SonarrClient(server.Url, apiKey);

        var command = await client.DownloadedEpisodesScanAsync(path, downloadId);
        var request = await server.Request;

        Assert.Equal(17, command.Id);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v3/command", request.Target);
        Assert.Equal(
            "{\"name\":\"DownloadedEpisodesScan\",\"path\":\"C:\\\\ARR Completed\\\\Series.Name\\\\S01E01.mkv\",\"downloadClientId\":\"ABCdef-0123:/\\\\\",\"importMode\":0}",
            request.Body);
        Assert.Equal(apiKey, request.Headers["X-Api-Key"]);
        Assert.DoesNotContain(apiKey, request.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, request.Body, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(path, body.RootElement.GetProperty("path").GetString());
        Assert.Equal(downloadId, body.RootElement.GetProperty("downloadClientId").GetString());
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("importMode").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("importMode").GetInt32());
    }

    [Fact]
    public async Task DownloadedMoviesScanAsync_PostsExactCommandWithoutNormalizingArrValues()
    {
        const string apiKey = "radarr-secret-api-key";
        const string path = "/mnt/ARR Completed/Movie.Name/file.mkv";
        const string downloadId = "radarr-ID-01";
        await using var server = await RecordingHttpServer.StartAsync("{\"id\":23}");
        var client = new RadarrClient(server.Url, apiKey);

        var command = await client.DownloadedMoviesScanAsync(path, downloadId);
        var request = await server.Request;

        Assert.Equal(23, command.Id);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v3/command", request.Target);
        Assert.Equal(
            "{\"name\":\"DownloadedMoviesScan\",\"path\":\"/mnt/ARR Completed/Movie.Name/file.mkv\",\"downloadClientId\":\"radarr-ID-01\",\"importMode\":0}",
            request.Body);
        Assert.Equal(apiKey, request.Headers["X-Api-Key"]);
        Assert.DoesNotContain(apiKey, request.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, request.Body, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(path, body.RootElement.GetProperty("path").GetString());
        Assert.Equal(downloadId, body.RootElement.GetProperty("downloadClientId").GetString());
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("importMode").ValueKind);
        Assert.Equal(0, body.RootElement.GetProperty("importMode").GetInt32());
    }

    [Fact]
    public async Task GetSonarrQueueAsync_DeserializesTypedMediaIds()
    {
        const string response = """
            {"page":1,"pageSize":5000,"totalRecords":1,"records":[{"downloadId":"sonarr-download","outputPath":"/arr/series","seriesId":41,"episodeId":42}]}
            """;
        await using var server = await RecordingHttpServer.StartAsync(response);
        var client = new SonarrClient(server.Url, "api-key");

        var queue = await client.GetSonarrQueueAsync();

        var record = Assert.Single(queue.Records);
        Assert.Equal(41, record.SeriesId);
        Assert.Equal(42, record.EpisodeId);
        Assert.Equal("sonarr-download", record.DownloadId);
        Assert.Equal("/arr/series", record.OutputPath);
        var request = await server.Request;
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/v3/queue?protocol=usenet&pageSize=5000", request.Target);
    }

    [Fact]
    public async Task GetSonarrQueueAsync_DeserializesExplicitNullMediaIdentity()
    {
        const string response = """
            {"page":1,"pageSize":5000,"totalRecords":1,"records":[{"downloadId":"sonarr-download","outputPath":"/arr/series","seriesId":null,"episodeId":null,"seasonNumber":null}]}
            """;
        await using var server = await RecordingHttpServer.StartAsync(response);
        var client = new SonarrClient(server.Url, "api-key");

        var queue = await client.GetSonarrQueueAsync();

        var record = Assert.Single(queue.Records);
        Assert.Null(record.SeriesId);
        Assert.Null(record.EpisodeId);
        Assert.Null(record.SeasonNumber);
    }

    [Fact]
    public async Task GetRadarrQueueAsync_DeserializesTypedMediaId()
    {
        const string response = """
            {"page":1,"pageSize":5000,"totalRecords":1,"records":[{"downloadId":"radarr-download","outputPath":"/arr/movie","movieId":73}]}
            """;
        await using var server = await RecordingHttpServer.StartAsync(response);
        var client = new RadarrClient(server.Url, "api-key");

        var queue = await client.GetRadarrQueueAsync();

        var record = Assert.Single(queue.Records);
        Assert.Equal(73, record.MovieId);
        Assert.Equal("radarr-download", record.DownloadId);
        Assert.Equal("/arr/movie", record.OutputPath);
        var request = await server.Request;
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/v3/queue?protocol=usenet&pageSize=5000", request.Target);
    }

    [Fact]
    public async Task GetRadarrQueueAsync_DeserializesExplicitNullMediaIdentity()
    {
        const string response = """
            {"page":1,"pageSize":5000,"totalRecords":1,"records":[{"downloadId":"radarr-download","outputPath":"/arr/movie","movieId":null}]}
            """;
        await using var server = await RecordingHttpServer.StartAsync(response);
        var client = new RadarrClient(server.Url, "api-key");

        var queue = await client.GetRadarrQueueAsync();

        var record = Assert.Single(queue.Records);
        Assert.Null(record.MovieId);
    }

    [Fact]
    public async Task GetSonarrQueueAsync_HonorsCancellationWhileWaitingForArrResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var client = new SonarrClient(server.Url, "api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSonarrQueueAsync(cts.Token));
    }

    [Fact]
    public async Task GetRadarrQueueAsync_HonorsCancellationWhileWaitingForArrResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var client = new RadarrClient(server.Url, "api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetRadarrQueueAsync(cts.Token));
    }

    [Fact]
    public async Task DownloadedEpisodesScanAsync_HonorsCancellationWhileWaitingForArrResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var client = new SonarrClient(server.Url, "api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadedEpisodesScanAsync("/arr/series", "download-id", cts.Token));
    }

    [Fact]
    public async Task DownloadedMoviesScanAsync_HonorsCancellationWhileWaitingForArrResponse()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var client = new RadarrClient(server.Url, "api-key");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadedMoviesScanAsync("/arr/movie", "download-id", cts.Token));
    }

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

    private sealed class RecordingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serverTask;
        private readonly TaskCompletionSource<CapturedHttpRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private RecordingHttpServer(TcpListener listener, string responseBody)
        {
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}";
            _serverTask = Task.Run(() => ServeOneRequestAsync(responseBody));
        }

        public string Url { get; }
        public Task<CapturedHttpRequest> Request => _request.Task;

        public static Task<RecordingHttpServer> StartAsync(string responseBody)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new RecordingHttpServer(listener, responseBody));
        }

        private async Task ServeOneRequestAsync(string responseBody)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
                _request.TrySetResult(request);

                var responseBytes = Encoding.UTF8.GetBytes(responseBody);
                var responseHeaders = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(responseBytes, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                _request.TrySetCanceled(_cts.Token);
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
                _request.TrySetCanceled(_cts.Token);
            }
            catch (Exception exception)
            {
                _request.TrySetException(exception);
            }
        }

        private static async Task<CapturedHttpRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;

            while (headerEnd < 0 || bytes.Length < headerEnd + 4 + contentLength)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("ARR test request ended before its declared body.");

                bytes.Write(buffer, 0, read);
                if (headerEnd >= 0)
                    continue;

                var text = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                    continue;

                var headerLines = text[..headerEnd].Split("\r\n", StringSplitOptions.None);
                foreach (var line in headerLines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0 &&
                        line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line[(separator + 1)..].Trim());
                    }
                }
            }

            var allBytes = bytes.ToArray();
            var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator]] = line[(separator + 1)..].Trim();
            }

            var body = Encoding.UTF8.GetString(allBytes, headerEnd + 4, contentLength);
            return new CapturedHttpRequest(requestLine[0], requestLine[1], headers, body);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _serverTask.ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    private sealed record CapturedHttpRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}

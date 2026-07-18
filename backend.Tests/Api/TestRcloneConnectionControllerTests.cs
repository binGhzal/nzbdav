using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestRcloneConnection;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class TestRcloneConnectionControllerTests
{
    [Fact]
    public async Task HandleApiRequest_HttpErrorEchoingBasicAuthReturnsOnlySafeCategoryAndStatus()
    {
        const string password = "super-secret-controller-password raw-token-controller";
        await using var server = await AdversarialRcServer.StartAsync(
            HttpStatusCode.InternalServerError,
            authorization => $$"""{"error":"compromised RC echoed {{authorization}}"}""");

        var response = await InvokeAsync(server.Url, password);

        Assert.False(response.Connected);
        Assert.Equal("Rclone RC returned HTTP 500.", response.Error);
        Assert.Equal("rclone_rc_http_500", response.ErrorCategory);
        Assert.Equal(500, response.ResponseStatusCode);
        AssertSecretMarkersAbsent(response, password);
    }

    [Fact]
    public async Task HandleApiRequest_MalformedSuccessEchoingBasicAuthReturnsOnlySafeCategory()
    {
        const string password = "super-secret-malformed-password raw-token-malformed";
        await using var server = await AdversarialRcServer.StartAsync(
            HttpStatusCode.OK,
            authorization => $"malformed RC response echoed {authorization}");

        var response = await InvokeAsync(server.Url, password);

        Assert.False(response.Connected);
        Assert.Equal("Rclone RC returned a malformed response.", response.Error);
        Assert.Equal("rclone_rc_malformed_response", response.ErrorCategory);
        Assert.Null(response.ResponseStatusCode);
        AssertSecretMarkersAbsent(response, password);
    }

    [Fact]
    public async Task HandleApiRequest_InvalidHostExceptionReturnsOnlySafeCategory()
    {
        const string marker = "invalid-host-secret-marker";

        var response = await InvokeAsync($"http://[{marker}", "unused-password-marker");

        Assert.False(response.Connected);
        Assert.Equal("Rclone RC endpoint is invalid.", response.Error);
        Assert.Equal("rclone_rc_invalid_endpoint", response.ErrorCategory);
        Assert.Null(response.ResponseStatusCode);
        AssertSecretMarkersAbsent(response, marker);
        AssertSecretMarkersAbsent(response, "unused-password-marker");
    }

    private static void AssertSecretMarkersAbsent(
        TestRcloneConnectionResponse response,
        string marker)
    {
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"benchmark-user:{marker}")), json);
    }

    private static async Task<TestRcloneConnectionResponse> InvokeAsync(string host, string password)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = host,
            ["user"] = "benchmark-user",
            ["pass"] = password,
            ["fs"] = ""
        });
        var controller = new TestRcloneConnectionController
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            var result = await controller.HandleApiRequest();
            var ok = Assert.IsType<OkObjectResult>(result);
            return Assert.IsType<TestRcloneConnectionResponse>(ok.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private sealed class AdversarialRcServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly HttpStatusCode _statusCode;
        private readonly Func<string, string> _responseBody;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        private AdversarialRcServer(
            TcpListener listener,
            HttpStatusCode statusCode,
            Func<string, string> responseBody)
        {
            _listener = listener;
            _statusCode = statusCode;
            _responseBody = responseBody;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _serveTask = Task.Run(ServeAsync);
        }

        internal string Url { get; }

        internal static Task<AdversarialRcServer> StartAsync(
            HttpStatusCode statusCode,
            Func<string, string> responseBody)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new AdversarialRcServer(listener, statusCode, responseBody));
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, _cts.Token);
                var authorization = request
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("Authorization: Basic ", StringComparison.OrdinalIgnoreCase));
                var encoded = authorization?["Authorization: Basic ".Length..].Trim() ?? "missing";
                var decoded = encoded == "missing"
                    ? encoded
                    : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var body = _responseBody(decoded);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var headers = string.Join("\r\n", [
                    $"HTTP/1.1 {(int)_statusCode} {_statusCode}",
                    "Content-Type: application/json",
                    $"Content-Length: {bodyBytes.Length}",
                    "Connection: close",
                    "",
                    ""
                ]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cts.Token);
                await stream.WriteAsync(bodyBytes, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            using var request = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                request.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(request.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                    return text;
            }

            return Encoding.ASCII.GetString(request.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _serveTask;
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
            }
            _cts.Dispose();
        }
    }
}

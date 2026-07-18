using System.Net;
using System.Net.Sockets;
using System.Text;
using backend.Tests.Services;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Clients.Rclone;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RcloneClientTests
{
    [Fact]
    public void Initialize_DefaultRcloneWithDisabledRemoteControlDoesNotRequireFence()
    {
        var configManager = new ConfigManager();

        RcloneClient.Initialize(configManager);

        Assert.False(RcloneClient.IsRemoteControlEnabled);
        Assert.Null(RcloneClient.Host);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
    }

    [Fact]
    public void EnablingRemoteControlOnDefaultRcloneArmsFenceWithoutHost()
    {
        var configManager = new ConfigManager();
        RcloneClient.Initialize(configManager);
        var disabledGeneration = RcloneClient.VisibilityFenceGeneration;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);

        Assert.True(RcloneClient.IsRemoteControlEnabled);
        Assert.True(RcloneClient.RequiresVfsVisibilityFence);
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
        Assert.NotEqual(disabledGeneration, RcloneClient.VisibilityFenceGeneration);
    }

    [Fact]
    public void DisablingRemoteControlOnRcloneClearsRequirementAndAdvancesGeneration()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(configManager);
        var enabledGeneration = RcloneClient.VisibilityFenceGeneration;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "false" }
        ]);

        Assert.False(RcloneClient.IsRemoteControlEnabled);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
        Assert.NotEqual(enabledGeneration, RcloneClient.VisibilityFenceGeneration);
    }

    [Fact]
    public async Task ConfiguredCall_RecordsSuccessfulRuntimeEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 12, 8, 30, 0, TimeSpan.Zero);
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            "{}");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url },
            new ConfigItem { ConfigName = "mount.type", ConfigValue = "rclone" }
        ]);
        RcloneClient.Initialize(configManager, new FixedTimeProvider(now));

        var response = await RcloneClient.NoOp();
        var snapshot = RcloneClient.GetRuntimeSnapshot();

        Assert.True(response.Success);
        Assert.True(snapshot.VisibilityFenceRequired);
        Assert.True(snapshot.RemoteControlEnabled);
        Assert.True(snapshot.HostConfigured);
        Assert.Equal(now, snapshot.LastAttemptAt);
        Assert.Equal(now, snapshot.LastSuccessfulConfiguredCallAt);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public async Task ConfiguredCall_RecordsBoundedFailureEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 12, 8, 31, 0, TimeSpan.Zero);
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.InternalServerError,
            $$"""{"error":"{{new string('x', 2_000)}}"}""");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager, new FixedTimeProvider(now));

        var response = await RcloneClient.NoOp();
        var snapshot = RcloneClient.GetRuntimeSnapshot();

        Assert.False(response.Success);
        Assert.Equal(now, snapshot.LastAttemptAt);
        Assert.Null(snapshot.LastSuccessfulConfiguredCallAt);
        Assert.NotNull(snapshot.LastError);
        Assert.InRange(snapshot.LastError!.Length, 1, 512);
    }

    [Fact]
    public async Task ConfiguredCall_DoesNotPersistServerEchoedSecretsInRuntimeEvidence()
    {
        const string secret = "super-secret-rc-token";
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.InternalServerError,
            $$"""{"error":"provider echoed {{secret}}"}""");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(
            configManager,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 12, 8, 31, 30, TimeSpan.Zero)));

        Assert.False((await RcloneClient.NoOp()).Success);
        var runtimeError = RcloneClient.GetRuntimeSnapshot().LastError;

        Assert.Equal("remote-control request failed", runtimeError);
        Assert.DoesNotContain(secret, runtimeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_DoesNotRecordConfiguredRuntimeEvidence()
    {
        var now = new DateTimeOffset(2026, 7, 12, 8, 32, 0, TimeSpan.Zero);
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            """{"version":"v1.70.0"}""");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://configured-rclone:5572" }
        ]);
        RcloneClient.Initialize(configManager, new FixedTimeProvider(now));

        var response = await RcloneClient.TestConnection(server.Url, user: null, pass: null);
        var snapshot = RcloneClient.GetRuntimeSnapshot();

        Assert.True(response.Success);
        Assert.Null(snapshot.LastAttemptAt);
        Assert.Null(snapshot.LastSuccessfulConfiguredCallAt);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public async Task RuntimeConfigChange_ClearsEvidenceFromPreviousConfiguration()
    {
        var now = new DateTimeOffset(2026, 7, 12, 8, 33, 0, TimeSpan.Zero);
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            "{}");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager, new FixedTimeProvider(now));
        Assert.True((await RcloneClient.NoOp()).Success);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://replacement-rclone:5572" }
        ]);
        var snapshot = RcloneClient.GetRuntimeSnapshot();

        Assert.True(snapshot.HostConfigured);
        Assert.Null(snapshot.LastAttemptAt);
        Assert.Null(snapshot.LastSuccessfulConfiguredCallAt);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void Initialize_RcloneAlwaysRearmsWholeCacheFenceForCrashSafety()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572/" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = " nzbdav: " }
        ]);

        RcloneClient.Initialize(configManager);
        var firstGeneration = RcloneClient.VisibilityFenceGeneration;
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);

        RcloneClient.Initialize(configManager);

        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
        Assert.NotEqual(firstGeneration, RcloneClient.VisibilityFenceGeneration);
    }

    [Fact]
    public void LiveTopologyIdentityChangesArmFenceButCredentialOnlyChangesDoNot()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "mount.type", ConfigValue = "dfs" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572/" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = " nzbdav: " }
        ]);
        RcloneClient.Initialize(configManager);
        var dfsGeneration = RcloneClient.VisibilityFenceGeneration;
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "new-user" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "new-password" }
        ]);
        Assert.Equal(dfsGeneration, RcloneClient.VisibilityFenceGeneration);
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" }
        ]);
        var rcloneGeneration = RcloneClient.VisibilityFenceGeneration;
        Assert.NotEqual(dfsGeneration, rcloneGeneration);
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://replacement:5572/" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = " replacement: " }
        ]);
        Assert.NotEqual(rcloneGeneration, RcloneClient.VisibilityFenceGeneration);
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
    }

    [Theory]
    [InlineData("Mount:Type")]
    [InlineData("mount.type")]
    public void EachMountTypeAliasArmsWholeCacheFenceOnDfsToRcloneTransition(string configName)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = configName, ConfigValue = "dfs" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(configManager);
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = configName, ConfigValue = "rclone" }
        ]);

        Assert.True(RcloneClient.RequiresVfsVisibilityFence);
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
    }

    [Fact]
    public void FormattingOnlyHostAndFsUpdatesDoNotAdvanceTopologyGeneration()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "dfs" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = " http://rclone:5572/ " },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = " nzbdav: " }
        ]);
        RcloneClient.Initialize(configManager);
        var generation = RcloneClient.VisibilityFenceGeneration;

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "nzbdav:" }
        ]);

        Assert.Equal(generation, RcloneClient.VisibilityFenceGeneration);
        Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
    }

    [Theory]
    [InlineData("rclone", "dfs", true, false)]
    [InlineData("dfs", "rclone", false, true)]
    public async Task VisibilityTopologyLease_BlocksRestartTopologyChangeUntilReleased(
        string activeMount,
        string restartMount,
        bool activeFence,
        bool restartFence)
    {
        var activeConfig = new ConfigManager();
        activeConfig.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = activeMount },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(activeConfig);
        var lease = await RcloneClient.AcquireVisibilityFenceTopologyLeaseAsync();
        var activeGeneration = lease.Generation;
        var restartConfig = new ConfigManager();
        restartConfig.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = restartMount },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);

        var restart = Task.Run(() => RcloneClient.Initialize(restartConfig));
        await Task.Delay(100);

        Assert.False(restart.IsCompleted);
        Assert.Equal(activeFence, lease.Required);
        Assert.Equal(activeFence, RcloneClient.RequiresVfsVisibilityFence);
        Assert.Equal(activeGeneration, RcloneClient.VisibilityFenceGeneration);

        await lease.DisposeAsync();
        await restart.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(restartFence, RcloneClient.RequiresVfsVisibilityFence);
        Assert.NotEqual(activeGeneration, RcloneClient.VisibilityFenceGeneration);
    }

    [Theory]
    [InlineData("rclone", "dfs", true, false)]
    [InlineData("dfs", "rclone", false, true)]
    public async Task VisibilityTopologyLease_BlocksLiveTopologyChangeUntilReleased(
        string activeMount,
        string changedMount,
        bool activeFence,
        bool changedFence)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = activeMount },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(configManager);
        var lease = await RcloneClient.AcquireVisibilityFenceTopologyLeaseAsync();
        var activeGeneration = lease.Generation;

        var change = Task.Run(() => configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = changedMount }
        ]));
        await Task.Delay(100);

        Assert.False(change.IsCompleted);
        Assert.Equal(activeFence, RcloneClient.RequiresVfsVisibilityFence);
        Assert.Equal(activeGeneration, RcloneClient.VisibilityFenceGeneration);

        await lease.DisposeAsync();
        await change.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(changedFence, RcloneClient.RequiresVfsVisibilityFence);
        Assert.NotEqual(activeGeneration, RcloneClient.VisibilityFenceGeneration);
    }

    [Fact]
    public async Task TestConnection_ReturnsFailureForMalformedSuccessfulRcResponse()
    {
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            "<html>not json</html>");

        var response = await RcloneClient.TestConnection(server.Url, user: null, pass: null);

        Assert.False(response.Success);
        Assert.Equal("Rclone RC returned a malformed response.", response.Error);
    }

    [Fact]
    public async Task TestConnection_RejectsWhitespaceOnlyCoreVersion()
    {
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            """{"version":"   "}""");

        var response = await RcloneClient.TestConnection(server.Url, user: null, pass: null);

        Assert.False(response.Success);
        Assert.Equal("Rclone RC returned an invalid response.", response.Error);
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
    public async Task ForgetVfsPaths_TimesOutWithinTheFastPathBudget()
    {
        await using var server = await HangingHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);
        using var outerBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await RcloneClient.ForgetVfsPaths(["/content/movie"], outerBudget.Token);

        Assert.False(response.Success);
        Assert.Equal("Rclone RC request timed out.", response.Error);
        Assert.False(outerBudget.IsCancellationRequested);
    }

    [Fact]
    public async Task ForgetVfsPaths_IncludesConfiguredVfsSelector()
    {
        await using var server = await SingleResponseHttpServer.StartAsync(
            HttpStatusCode.OK,
            """{"forgotten":["content/movie"]}""");
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "nzbdav:" }
        ]);
        RcloneClient.Initialize(configManager);

        var response = await RcloneClient.ForgetVfsPaths(["/content/movie"]);

        Assert.True(response.Success);
        var requestBody = await server.RequestBody;
        Assert.Contains("\"fs\":\"nzbdav:\"", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_ValidatesConfiguredVfsSelector()
    {
        await using var server = await SingleResponseHttpServer.StartSequentialAsync(
            HttpStatusCode.OK,
            [
                """{"version":"v1.70.0"}""",
                """{"metadataCache":{"dirs":1,"files":2},"opt":{"CacheMode":"full"}}"""
            ]);

        var response = await RcloneClient.TestConnection(
            server.Url,
            user: null,
            pass: null,
            fs: " nzbdav: ");

        Assert.True(response.Success);
        var requestBodies = await server.RequestBodies;
        Assert.Equal(2, requestBodies.Count);
        Assert.Equal("{}", requestBodies[0]);
        Assert.Contains("\"fs\":\"nzbdav:\"", requestBodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":\"v1.70.0\"}")]
    [InlineData("{\"metadataCache\":null,\"opt\":{}}")]
    [InlineData("{\"metadataCache\":{},\"opt\":null}")]
    public async Task TestConnection_RejectsInvalidConfiguredVfsStatsShape(string invalidStats)
    {
        await using var server = await SingleResponseHttpServer.StartSequentialAsync(
            HttpStatusCode.OK,
            ["""{"version":"v1.70.0"}""", invalidStats]);

        var response = await RcloneClient.TestConnection(
            server.Url,
            user: "shape-test-user",
            pass: "shape-test-password",
            fs: "nzbdav:");

        Assert.False(response.Success);
        Assert.Equal("Rclone RC returned an invalid response.", response.Error);
        Assert.DoesNotContain(invalidStats, response.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("shape-test-password", response.Error, StringComparison.Ordinal);
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

    [Fact]
    public void ConfigChange_UpdatesOptionalVfsSelectorAndWakesRuntimeState()
    {
        var configManager = new ConfigManager();
        RcloneClient.Initialize(configManager);

        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = " nzbdav: " }
        ]);

        Assert.Equal("nzbdav:", RcloneClient.Fs);
    }

    private sealed class SingleResponseHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private Task _serverTask;
        private readonly TaskCompletionSource<string> _requestBody =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<string>> _requestBodies =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedRequests;

        private SingleResponseHttpServer(TcpListener listener, Task serverTask, string url, int expectedRequests)
        {
            _listener = listener;
            _serverTask = serverTask;
            Url = url;
            _expectedRequests = expectedRequests;
        }

        public string Url { get; }
        public Task<string> RequestBody => _requestBody.Task;
        public Task<IReadOnlyList<string>> RequestBodies => _requestBodies.Task;

        public static Task<SingleResponseHttpServer> StartAsync(
            HttpStatusCode statusCode,
            string body,
            int expectedRequests = 1)
        {
            return StartSequentialAsync(
                statusCode,
                Enumerable.Repeat(body, expectedRequests).ToArray());
        }

        public static Task<SingleResponseHttpServer> StartSequentialAsync(
            HttpStatusCode statusCode,
            IReadOnlyList<string> responseBodies)
        {
            if (responseBodies.Count == 0)
                throw new ArgumentException("At least one response body is required.", nameof(responseBodies));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new SingleResponseHttpServer(
                listener,
                Task.CompletedTask,
                $"http://127.0.0.1:{port}",
                responseBodies.Count);
            server._serverTask = Task.Run(() => server.ServeRequestsAsync(statusCode, responseBodies));
            return Task.FromResult(server);
        }

        private async Task ServeRequestsAsync(
            HttpStatusCode statusCode,
            IReadOnlyList<string> responseBodies)
        {
            var requestBodies = new List<string>(_expectedRequests);
            for (var requestIndex = 0; requestIndex < _expectedRequests; requestIndex++)
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await using var stream = client.GetStream();

                var requestBody = await ReadRequestBodyAsync(stream).ConfigureAwait(false);
                requestBodies.Add(requestBody);
                _requestBody.TrySetResult(requestBody);

                var body = responseBodies[requestIndex];
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = string.Join("\r\n", [
                    $"HTTP/1.1 {(int)statusCode} {statusCode}",
                    "Content-Type: application/json; charset=utf-8",
                    $"Content-Length: {bodyBytes.Length}",
                    "Connection: close",
                    "",
                    ""
                ]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            }

            _requestBodies.TrySetResult(requestBodies);
        }

        private static async Task<string> ReadRequestBodyAsync(NetworkStream stream)
        {
            using var request = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                request.Write(buffer, 0, read);
                var bytes = request.ToArray();
                if (headerEnd < 0)
                {
                    var text = Encoding.ASCII.GetString(bytes);
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        var headers = text[..headerEnd];
                        var lengthHeader = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        if (lengthHeader != null)
                            int.TryParse(lengthHeader[(lengthHeader.IndexOf(':') + 1)..].Trim(), out contentLength);
                    }
                }

                if (headerEnd >= 0 && bytes.Length >= headerEnd + 4 + contentLength)
                    return Encoding.UTF8.GetString(bytes, headerEnd + 4, contentLength);
            }

            return "";
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

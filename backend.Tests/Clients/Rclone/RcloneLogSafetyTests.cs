using System.Collections.Concurrent;
using System.Net;
using backend.Tests.Services;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace backend.Tests.Clients.Rclone;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RcloneLogSafetyTests
{
    [Fact]
    public async Task TimeoutLogContainsOnlyStableCategoryAndExceptionType()
    {
        const string exceptionMarker = "super-secret-timeout-exception-marker";
        const string credentialMarker = "super-secret-timeout-credential-marker";
        const string hostMarker = "url-host-timeout-marker.test";
        const string userInfoMarker = "url-user-timeout-marker";
        const string pathMarker = "request-path-timeout-marker";
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = "rclone.host",
                ConfigValue = $"http://{userInfoMarker}:{credentialMarker}@{hostMarker}/base-path-marker"
            },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "configured-user-marker" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = credentialMarker },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "body-token-marker:" }
        ]);
        RcloneClient.Initialize(configManager);
        using var logger = new LoggerScope();
        using var clientOverride = RcloneClient.OverrideHttpClientForTests(
            new HttpClient(new ThrowingHandler(
                new TaskCanceledException(exceptionMarker, new TimeoutException(exceptionMarker)))));

        var response = await RcloneClient.ForgetVfsPaths([$"/{pathMarker}"]);

        Assert.False(response.Success);
        Assert.Equal("Rclone RC request timed out.", response.Error);
        Assert.Contains("rclone_rc_timeout", logger.Rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(TaskCanceledException), logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(hostMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(userInfoMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(pathMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-user-marker", logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("body-token-marker", logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientRetryAndFinalConnectionLogsNeverRenderRawException()
    {
        const string exceptionMarker = "super-secret-transient-exception-marker";
        const string credentialMarker = "super-secret-transient-credential-marker";
        const string hostMarker = "url-host-transient-marker.test";
        const string userInfoMarker = "url-user-transient-marker";
        const string pathMarker = "request-path-transient-marker";
        using var logger = new LoggerScope();
        using var clientOverride = RcloneClient.OverrideHttpClientForTests(
            new HttpClient(new ThrowingHandler(new HttpRequestException(exceptionMarker))));

        var response = await RcloneClient.TestConnection(
            $"http://{userInfoMarker}:{credentialMarker}@{hostMarker}/{pathMarker}",
            user: "user",
            pass: credentialMarker);

        Assert.False(response.Success);
        Assert.Equal("Could not connect to rclone RC.", response.Error);
        Assert.Contains("rclone_rc_connection_failure", logger.Rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), logger.Rendered, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(logger.Rendered, "Rclone RC retry scheduled"));
        Assert.Equal(1, CountOccurrences(logger.Rendered, "Rclone RC request failed"));
        Assert.DoesNotContain(exceptionMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(hostMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(userInfoMarker, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(pathMarker, logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidationWorkerOuterCatchNeverRendersRawException()
    {
        const string marker = "super-secret-worker-outer-exception-marker";
        using var logger = new LoggerScope();
        using var service = new RcloneInvalidationService(
            _ => Task.FromException(new InvalidOperationException(marker)));

        await service.StartAsync(CancellationToken.None);
        await logger.WaitForAsync("rclone_invalidation_worker_failure", TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(nameof(InvalidOperationException), logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, logger.Rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, 17777, "rclone_rc_http_502")]
    [InlineData(HttpStatusCode.OK, 18888, "rclone_rc_malformed_response")]
    public async Task AttackerControlledResponseLengthIsNeverLogged(
        HttpStatusCode statusCode,
        int attackerControlledLength,
        string expectedCategory)
    {
        var body = new string(statusCode == HttpStatusCode.OK ? '!' : 'x', attackerControlledLength);
        using var logger = new LoggerScope();
        using var clientOverride = RcloneClient.OverrideHttpClientForTests(
            new HttpClient(new StaticResponseHandler(statusCode, body)));

        var response = await RcloneClient.TestConnection(
            "http://rclone-log-safety.invalid",
            user: "attacker-length-user",
            pass: "attacker-length-password");

        Assert.False(response.Success);
        Assert.Contains(expectedCategory, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("response_length", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attackerControlledLength.ToString(), logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(body, logger.Rendered, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(marker, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += marker.Length;
        }

        return count;
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class LoggerScope : IDisposable, ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();
        private readonly Serilog.ILogger _previous = Log.Logger;
        private readonly Serilog.Core.Logger _logger;
        private int _disposed;

        internal LoggerScope()
        {
            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(this)
                .CreateLogger();
            Log.Logger = _logger;
        }

        internal string Rendered => string.Join('\n', _events);

        public void Emit(LogEvent logEvent)
        {
            var rendered = logEvent.RenderMessage();
            _events.Enqueue(rendered);
            if (logEvent.Exception is not null)
                _events.Enqueue(logEvent.Exception.ToString());
            foreach (var (marker, waiter) in _waiters)
            {
                if (rendered.Contains(marker, StringComparison.Ordinal))
                    waiter.TrySetResult();
            }
        }

        internal Task WaitForAsync(string marker, TimeSpan timeout)
        {
            if (Rendered.Contains(marker, StringComparison.Ordinal))
                return Task.CompletedTask;
            var waiter = _waiters.GetOrAdd(
                marker,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return waiter.Task.WaitAsync(timeout);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Log.Logger = _previous;
            _logger.Dispose();
        }
    }
}

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Clients.Usenet;

public sealed class MultiProviderCircuitBreakerTests
{
    [Fact]
    public void ProviderCircuitBreakerBlocksImmediatelyAfterHardFailure()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");

        breaker.RecordHardFailure();

        Assert.Null(breaker.TryAcquireAttempt());
    }

    [Fact]
    public void ProviderCircuitBreakerSnapshotDistinguishesSoftAndHardFailures()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        var softSnapshot = breaker.GetSnapshot();
        breaker.RecordSuccess();
        breaker.RecordHardFailure();
        var hardSnapshot = breaker.GetSnapshot();

        Assert.Equal("soft_cooldown", softSnapshot.CircuitState);
        Assert.Equal("retryable", softSnapshot.LastFailureKind);
        Assert.NotNull(softSnapshot.CooldownUntil);
        Assert.Equal("hard_tripped", hardSnapshot.CircuitState);
        Assert.Equal("auth_or_config", hardSnapshot.LastFailureKind);
        Assert.NotNull(hardSnapshot.CooldownUntil);
    }

    [Fact]
    public void ProviderCircuitBreakerSnapshotReportsElapsedCooldownBeforeProbe()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        ExpireCooldown(breaker);

        var snapshot = breaker.GetSnapshot();

        Assert.Equal("cooldown_elapsed", snapshot.CircuitState);
        Assert.Equal(3, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.CooldownUntil);
    }

    [Fact]
    public async Task MissingArticlesDoNotTripProviderCircuitBreaker()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");
        using var provider = new MissingArticleProvider("provider-a", breaker);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
                provider.DecodedBodyAsync($"missing-{i}", CancellationToken.None));
        }

        var snapshot = breaker.GetSnapshot();
        Assert.False(breaker.IsTripped);
        Assert.Equal("healthy", snapshot.CircuitState);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastFailureKind);
    }

    [Fact]
    public async Task AllTrippedProvidersAllowOnlyOneCooldownProbePerProvider()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        ExpireCooldown(breaker);

        using var provider = new FailingFactoryProvider("provider-a", breaker);
        using var client = new MultiProviderNntpClient([provider]);
        var tasks = Enumerable.Range(0, 12)
            .Select(_ => Assert.ThrowsAsync<RetryableDownloadException>(() =>
                client.DecodedBodyAsync("segment-1", CancellationToken.None)))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, provider.FactoryCalls);
    }

    [Fact]
    public async Task AllUnavailableProvidersThrowRetryableDownloadException()
    {
        var breaker = new ProviderCircuitBreaker("provider-a");
        breaker.RecordHardFailure();

        using var provider = new FailingFactoryProvider("provider-a", breaker);
        using var client = new MultiProviderNntpClient([provider]);

        await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            client.DecodedBodyAsync("segment-1", CancellationToken.None));
    }

    private static void ExpireCooldown(ProviderCircuitBreaker breaker)
    {
        var field = typeof(ProviderCircuitBreaker).GetField(
            "_trippedUntilMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(breaker, Environment.TickCount64 - 1);
    }

    private sealed class FailingFactoryProvider : MultiConnectionNntpClient
    {
        private sealed class Counter
        {
            public int FactoryCalls;
        }

        public FailingFactoryProvider(string name, ProviderCircuitBreaker breaker)
            : this(name, breaker, new Counter())
        {
        }

        private FailingFactoryProvider(string name, ProviderCircuitBreaker breaker, Counter counter)
            : base(
                new ConnectionPool<INntpClient>(
                    16,
                    _ =>
                    {
                        Interlocked.Increment(ref counter.FactoryCalls);
                        throw new IOException("provider unavailable");
                    },
                    TimeSpan.FromMinutes(1)),
                NzbWebDAV.Models.ProviderType.Pooled,
                breaker,
                name,
                providerPriority: 0)
        {
            _counter = counter;
        }

        private readonly Counter _counter;

        public int FactoryCalls => Volatile.Read(ref _counter.FactoryCalls);
    }

    private sealed class MissingArticleProvider : MultiConnectionNntpClient
    {
        public MissingArticleProvider(string name, ProviderCircuitBreaker breaker)
            : base(
                new ConnectionPool<INntpClient>(
                    1,
                    _ => ValueTask.FromResult<INntpClient>(new FakeNntpClient()),
                    TimeSpan.FromMinutes(1)),
                ProviderType.Pooled,
                breaker,
                name,
                providerPriority: 0)
        {
        }
    }
}

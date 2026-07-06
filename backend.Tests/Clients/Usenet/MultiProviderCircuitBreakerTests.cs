using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Exceptions;

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
}

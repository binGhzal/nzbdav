using NzbWebDAV.Clients.Usenet.Concurrency;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class PrioritizedSemaphoreTests
{
    [Fact]
    public async Task NormalPriorityRunsBeforeBulkDownloadWhenForegroundIsIdle()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);

        var low = semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);
        var normal = semaphore.WaitAsync(SemaphorePriority.Normal, timeout.Token);

        semaphore.Release();

        await normal.WaitAsync(timeout.Token);
        Assert.False(low.IsCompleted);

        semaphore.Release();
        await low.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ForegroundPriorityRunsBeforeVerificationPriority()
    {
        using var semaphore = new PrioritizedSemaphore(
            initialAllowed: 1,
            maxAllowed: 1,
            new SemaphorePriorityOdds { HighPriorityOdds = 100 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);

        var normal = semaphore.WaitAsync(SemaphorePriority.Normal, timeout.Token);
        var high = semaphore.WaitAsync(SemaphorePriority.High, timeout.Token);

        semaphore.Release();

        await high.WaitAsync(timeout.Token);
        Assert.False(normal.IsCompleted);

        semaphore.Release();
        await normal.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task IncreasingMaxAllowedImmediatelyReleasesQueuedWaiters()
    {
        using var semaphore = new PrioritizedSemaphore(initialAllowed: 1, maxAllowed: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);

        var queued = semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);
        Assert.False(queued.IsCompleted);

        semaphore.UpdateMaxAllowed(2);

        await queued.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task IncreasingMaxAllowedPreservesPriorityOrder()
    {
        using var semaphore = new PrioritizedSemaphore(
            initialAllowed: 1,
            maxAllowed: 1,
            new SemaphorePriorityOdds { HighPriorityOdds = 100 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);

        var low = semaphore.WaitAsync(SemaphorePriority.Low, timeout.Token);
        var normal = semaphore.WaitAsync(SemaphorePriority.Normal, timeout.Token);
        var high = semaphore.WaitAsync(SemaphorePriority.High, timeout.Token);

        semaphore.UpdateMaxAllowed(2);

        await high.WaitAsync(timeout.Token);
        Assert.False(normal.IsCompleted);
        Assert.False(low.IsCompleted);

        semaphore.UpdateMaxAllowed(3);
        await normal.WaitAsync(timeout.Token);
        Assert.False(low.IsCompleted);

        semaphore.UpdateMaxAllowed(4);
        await low.WaitAsync(timeout.Token);
    }
}

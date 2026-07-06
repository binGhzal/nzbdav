using NzbWebDAV.Queue;

namespace backend.Tests.Queue;

public sealed class QueueProcessingLoopTests
{
    [Fact]
    public async Task RunAsyncBacksOffAfterUnexpectedErrorBeforeRetrying()
    {
        var attempts = 0;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var loop = new QueueProcessingLoop(
            isPaused: () => false,
            tryStartNext: _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("database unavailable");

                cts.Cancel();
                return Task.FromResult(false);
            },
            waitForWork: _ => Task.CompletedTask,
            onUnexpectedError: _ => Task.CompletedTask,
            delayAfterUnexpectedError: (_, ct) =>
            {
                delayStarted.TrySetResult();
                return releaseDelay.Task.WaitAsync(ct);
            });

        var runTask = loop.RunAsync(cts.Token);

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts);

        releaseDelay.SetResult();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RunAsyncReturnsCleanlyWhenCancelledDuringErrorBackoff()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new QueueProcessingLoop(
            isPaused: () => false,
            tryStartNext: _ => throw new InvalidOperationException("database unavailable"),
            waitForWork: _ => Task.CompletedTask,
            onUnexpectedError: _ => Task.CompletedTask,
            delayAfterUnexpectedError: async (_, ct) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        var runTask = loop.RunAsync(cts.Token);

        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

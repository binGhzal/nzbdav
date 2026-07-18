using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace backend.Tests.Tasks;

public sealed class BaseTaskLifecycleTests
{
    [Fact]
    public async Task Execute_ForwardsCancellationAndDoesNotSwallowIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new DelegateTask(
            async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();

        var execution = task.Execute(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task Execute_PropagatesFailureAndReportsStructuredProgress()
    {
        var progress = new List<MaintenanceTaskProgress>();
        var expected = new InvalidOperationException("maintenance exploded");
        var task = new DelegateTask(
            _ => throw expected,
            update =>
            {
                progress.Add(update);
                return Task.CompletedTask;
            });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => task.Execute());

        Assert.Same(expected, thrown);
        var update = Assert.Single(progress);
        Assert.Equal("Starting test task.", update.Message);
        Assert.Equal(2, update.Current);
        Assert.Equal(5, update.Total);
    }

    [Fact]
    public async Task Execute_DoesNotSerializeIndependentTaskInstancesInProcess()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new DelegateTask(async _ =>
        {
            firstEntered.TrySetResult();
            await release.Task;
        });
        var second = new DelegateTask(async _ =>
        {
            secondEntered.TrySetResult();
            await release.Task;
        });

        var firstExecution = first.Execute();
        var secondExecution = second.Execute();
        await Task.WhenAll(
            firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        release.TrySetResult();
        await Task.WhenAll(firstExecution, secondExecution);
    }

    private sealed class DelegateTask : BaseTask
    {
        private readonly Func<CancellationToken, Task> _execute;

        public DelegateTask(
            Func<CancellationToken, Task> execute,
            MaintenanceProgressReporter? progressReporter = null)
            : base(new WebsocketManager(), WebsocketTopic.CleanupTaskProgress, progressReporter)
        {
            _execute = execute;
        }

        protected override Task ExecuteInternal(CancellationToken cancellationToken)
        {
            Report("Starting test task.", current: 2, total: 5);
            return _execute(cancellationToken);
        }
    }
}

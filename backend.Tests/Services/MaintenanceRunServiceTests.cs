using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using backend.Tests.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class MaintenanceRunServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public MaintenanceRunServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryStartRunAsync_PersistsQueuedRunAndRejectsConflictingActiveRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var executor = new DelegateMaintenanceTaskExecutor((_, _, _) => Task.CompletedTask);
        using var service = CreateService(executor);

        var first = await service.TryStartRunAsync(
            MaintenanceRunKind.RecreateStrmFiles,
            requestedBy: "manual",
            CancellationToken.None);
        var second = await service.TryStartRunAsync(
            MaintenanceRunKind.ConvertStrmToSymlinks,
            requestedBy: "manual",
            CancellationToken.None);

        Assert.True(first.Started);
        Assert.Equal(MaintenanceRunStatus.Queued, first.Run.Status);
        Assert.Equal(1, first.Run.ActiveSlot);
        Assert.False(second.Started);
        Assert.Equal(first.Run.Id, second.Run.Id);
        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.MaintenanceRuns.SingleAsync();
        Assert.Equal(first.Run.Id, persisted.Id);
    }

    [Fact]
    public async Task HostedExecutor_WakesImmediatelyAndPersistsProgressAndCompletion()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var executor = new DelegateMaintenanceTaskExecutor(async (_, report, cancellationToken) =>
        {
            await report(new MaintenanceTaskProgress("Converted 1 of 2.", 1, 2));
            cancellationToken.ThrowIfCancellationRequested();
        });
        using var service = CreateService(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var started = await service.TryStartRunAsync(
                MaintenanceRunKind.ConvertStrmToSymlinks,
                requestedBy: "manual",
                CancellationToken.None);

            var completed = await WaitForStatusAsync(started.Run.Id, MaintenanceRunStatus.Completed);

            Assert.NotNull(completed.StartedAt);
            Assert.NotNull(completed.CompletedAt);
            Assert.Null(completed.ActiveSlot);
            Assert.Equal(1, completed.ProgressCurrent);
            Assert.Equal(2, completed.ProgressTotal);
            Assert.Equal("Converted 1 of 2.", completed.Message);
            Assert.Null(completed.Error);
            Assert.Equal(1, executor.ExecutionCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_MarksStaleRunningRunInterruptedWithoutResumingIt()
    {
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            dbContext.MaintenanceRuns.Add(new MaintenanceRun
            {
                Id = Guid.NewGuid(),
                Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
                Status = MaintenanceRunStatus.Running,
                ActiveSlot = 1,
                RequestedBy = "manual",
                CreatedAt = now,
                StartedAt = now,
                UpdatedAt = now,
                ProgressCurrent = 12,
                Message = "Removing unlinked items...",
            });
            await dbContext.SaveChangesAsync();
        }

        var executor = new DelegateMaintenanceTaskExecutor((_, _, _) => Task.CompletedTask);
        using var service = CreateService(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var interrupted = await WaitForLatestStatusAsync(MaintenanceRunStatus.Interrupted);

            Assert.Null(interrupted.ActiveSlot);
            Assert.NotNull(interrupted.CompletedAt);
            Assert.Contains("interrupted", interrupted.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, executor.ExecutionCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestCancellationAsync_CancelsRunningTaskAndPersistsCancelledState()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateMaintenanceTaskExecutor(async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        });
        using var service = CreateService(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var started = await service.TryStartRunAsync(
                MaintenanceRunKind.RecreateStrmFiles,
                requestedBy: "manual",
                CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var cancellation = await service.RequestCancellationAsync(started.Run.Id, CancellationToken.None);
            var cancelled = await WaitForStatusAsync(started.Run.Id, MaintenanceRunStatus.Cancelled);

            Assert.NotNull(cancellation);
            Assert.NotNull(cancelled.CancellationRequestedAt);
            Assert.NotNull(cancelled.CompletedAt);
            Assert.Null(cancelled.ActiveSlot);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HostedExecutor_PersistsFailureAndReleasesActiveSlot()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var executor = new DelegateMaintenanceTaskExecutor((_, _, _) =>
            throw new InvalidOperationException("filesystem failed"));
        using var service = CreateService(executor);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var started = await service.TryStartRunAsync(
                MaintenanceRunKind.RecreateStrmFiles,
                requestedBy: "manual",
                CancellationToken.None);

            var failed = await WaitForStatusAsync(started.Run.Id, MaintenanceRunStatus.Failed);

            Assert.Null(failed.ActiveSlot);
            Assert.NotNull(failed.CompletedAt);
            Assert.Equal("filesystem failed", failed.Error);
            Assert.Equal("Failed.", failed.Message);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private MaintenanceRunService CreateService(IMaintenanceTaskExecutor executor)
    {
        return new MaintenanceRunService(executor);
    }

    private static async Task<MaintenanceRun> WaitForStatusAsync(Guid id, MaintenanceRunStatus status)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = new DavDatabaseContext();
            var run = await dbContext.MaintenanceRuns.AsNoTracking().SingleAsync(x => x.Id == id);
            if (run.Status == status) return run;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Maintenance run {id} did not reach {status}.");
    }

    private static async Task<MaintenanceRun> WaitForLatestStatusAsync(MaintenanceRunStatus status)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = new DavDatabaseContext();
            var run = await dbContext.MaintenanceRuns
                .AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .FirstAsync();
            if (run.Status == status) return run;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Latest maintenance run did not reach {status}.");
    }

    private sealed class DelegateMaintenanceTaskExecutor(
        Func<MaintenanceRunKind, MaintenanceProgressReporter, CancellationToken, Task> execute)
        : IMaintenanceTaskExecutor
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task ExecuteAsync(
            MaintenanceRunKind kind,
            MaintenanceProgressReporter report,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            return execute(kind, report, cancellationToken);
        }
    }
}

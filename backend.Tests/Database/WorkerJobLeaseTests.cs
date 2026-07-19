using System.Data.Common;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Security;
using NzbWebDAV.Websocket;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class WorkerJobLeaseTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public WorkerJobLeaseTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LeaseNextWorkerJobAsync_LeasesOnlyRequestedKindAndSkipsUnexpiredLeases()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var downloadTargetId = Guid.NewGuid();
        var verifyTargetId = Guid.NewGuid();

        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, downloadTargetId, priority: 10, now: now);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Verify, verifyTargetId, priority: 5, now: now);

        var downloadLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Download,
            owner: "worker-a",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now);
        var secondDownloadLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Download,
            owner: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(1));
        var verifyLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Verify,
            owner: "worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(1));

        Assert.NotNull(downloadLease);
        Assert.Equal(downloadTargetId, downloadLease.TargetId);
        Assert.Equal(WorkerJob.JobStatus.Leased, downloadLease.Status);
        Assert.Equal("worker-a", downloadLease.LeaseOwner);
        Assert.Equal(1, downloadLease.Attempts);
        Assert.NotEqual(Guid.Empty, downloadLease.LeaseToken);
        Assert.Equal(1, downloadLease.LeaseGeneration);
        Assert.Equal(now, downloadLease.StartedAt);
        Assert.Equal(now, downloadLease.LastHeartbeatAt);
        Assert.Null(downloadLease.CancelRequestedAt);

        Assert.Null(secondDownloadLease);
        Assert.NotNull(verifyLease);
        Assert.Equal(verifyTargetId, verifyLease.TargetId);

        var expiredDownloadLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Download,
            owner: "worker-c",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(6));

        Assert.NotNull(expiredDownloadLease);
        Assert.Equal(downloadTargetId, expiredDownloadLease.TargetId);
        Assert.Equal("worker-c", expiredDownloadLease.LeaseOwner);
        Assert.Equal(2, expiredDownloadLease.Attempts);
    }

    [Fact]
    public async Task DatabaseCoordinator_DoesNotLeaseDownloadPausedAtAdd()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.Now;
        var queueItem = CreateQueueItem("Paused at add", QueueItem.PriorityOption.Paused, now.LocalDateTime);
        dbContext.QueueItems.Add(queueItem);
        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Download,
            queueItem.Id,
            (int)queueItem.Priority,
            now);

        var leases = await CreateCoordinator(dbContext).LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker",
            1,
            now,
            CancellationToken.None);

        Assert.Empty(leases);
    }

    [Fact]
    public async Task DatabaseCoordinator_CancelsOrphanDownloadWorkerJobWithoutHistory()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var orphan = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Pending,
            now.AddMinutes(-1));
        dbContext.WorkerJobs.Add(orphan);
        await dbContext.SaveChangesAsync();

        var leases = await CreateCoordinator(dbContext).LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker",
            1,
            now,
            CancellationToken.None);

        Assert.Empty(leases);
        dbContext.ChangeTracker.Clear();
        var terminal = await dbContext.WorkerJobs.SingleAsync(job => job.Id == orphan.Id);
        Assert.Equal(WorkerJob.JobStatus.Cancelled, terminal.Status);
        Assert.Equal(WorkerJob.FailureClass.Cancelled, terminal.FailureKind);
        Assert.NotNull(terminal.CompletedAt);
        Assert.Equal(0, terminal.Attempts);
        Assert.Null(terminal.LeaseToken);
    }

    [Fact]
    public async Task DatabaseCoordinator_CompletesOrphanDownloadWorkerJobWithHistory()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();
        var orphan = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Leased,
            now.AddMinutes(-1),
            now.AddMinutes(2),
            targetId);
        orphan.LeaseOwner = "download-worker-a";
        orphan.LeaseToken = Guid.NewGuid();
        orphan.LeaseGeneration = 3;
        var originalIdentity = ToIdentity(orphan);
        dbContext.WorkerJobs.Add(orphan);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = targetId,
            CreatedAt = DateTime.Now,
            FileName = "Completed.nzb",
            JobName = "Completed",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            NzbBlobId = targetId
        });
        await dbContext.SaveChangesAsync();

        var coordinator = CreateCoordinator(dbContext);
        var leases = await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker-b",
            1,
            now,
            CancellationToken.None);

        Assert.Empty(leases);
        dbContext.ChangeTracker.Clear();
        var terminal = await dbContext.WorkerJobs.SingleAsync(job => job.Id == orphan.Id);
        Assert.Equal(WorkerJob.JobStatus.Completed, terminal.Status);
        Assert.Equal("download-worker-a", terminal.LeaseOwner);
        Assert.Equal(orphan.LeaseToken, terminal.LeaseToken);
        Assert.Equal(3, terminal.LeaseGeneration);
        Assert.Null(terminal.LeaseExpiresAt);
        Assert.NotNull(terminal.CompletedAt);
        Assert.True(await coordinator.CompleteAsync(
            originalIdentity,
            null,
            now.AddSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseCoordinator_RetryFailureIsIdempotentAfterAmbiguousSuccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new ThrowAfterNextNonQueryInterceptor();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var nextAttemptAt = now.AddMinutes(1);
        var job = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Leased,
            now,
            now.AddMinutes(2));
        job.LeaseToken = Guid.NewGuid();
        job.LeaseGeneration = 7;
        dbContext.WorkerJobs.Add(job);
        await dbContext.SaveChangesAsync();
        var identity = ToIdentity(job);
        var coordinator = CreateCoordinator(dbContext);
        interceptor.Arm();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FailAsync(
            identity,
            WorkerJob.FailureClass.Retryable,
            "retry after transient failure",
            nextAttemptAt,
            10,
            now,
            CancellationToken.None));
        var reconciled = await coordinator.FailAsync(
            identity,
            WorkerJob.FailureClass.Retryable,
            "retry after transient failure",
            nextAttemptAt,
            10,
            now.AddSeconds(1),
            CancellationToken.None);

        Assert.True(reconciled);
        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.WorkerJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(WorkerJob.JobStatus.Retry, saved.Status);
        Assert.Equal(7, saved.LeaseGeneration);
        Assert.Null(saved.LeaseOwner);
        Assert.Null(saved.LeaseToken);
        Assert.Equal(WorkerJob.FailureClass.Retryable, saved.FailureKind);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.WorkerFailure), saved.LastError);
        Assert.Equal(nextAttemptAt, saved.AvailableAt);
    }

    [Fact]
    public async Task DatabaseCoordinator_ReleaseIsIdempotentAfterAmbiguousSuccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new ThrowAfterNextNonQueryInterceptor();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var job = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Leased,
            now,
            now.AddMinutes(2));
        job.LeaseToken = Guid.NewGuid();
        job.LeaseGeneration = 4;
        dbContext.WorkerJobs.Add(job);
        await dbContext.SaveChangesAsync();
        var identity = ToIdentity(job);
        var coordinator = CreateCoordinator(dbContext);
        interceptor.Arm();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ReleaseAsync(
            identity,
            now,
            CancellationToken.None));
        var reconciled = await coordinator.ReleaseAsync(identity, now, CancellationToken.None);

        Assert.True(reconciled);
        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.WorkerJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(WorkerJob.JobStatus.Pending, saved.Status);
        Assert.Equal(4, saved.LeaseGeneration);
        Assert.Null(saved.LeaseOwner);
        Assert.Null(saved.LeaseToken);
        Assert.Null(saved.LeaseExpiresAt);
        Assert.Equal(0, saved.Attempts);
        Assert.Equal(now, saved.AvailableAt);
    }

    [Fact]
    public async Task DatabaseCoordinator_RechecksPauseBeforeLeaseAndResumesExplicitly()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.Now;
        var queueItem = CreateQueueItem("Pause race", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        dbContext.QueueItems.Add(queueItem);
        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Download,
            queueItem.Id,
            (int)queueItem.Priority,
            now);
        await dbClient.UpdateQueueItemsPriorityAsync([queueItem.Id], QueueItem.PriorityOption.Paused);

        var coordinator = CreateCoordinator(dbContext);
        Assert.Empty(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker",
            1,
            now,
            CancellationToken.None));

        await dbClient.UpdateQueueItemsPriorityAsync([queueItem.Id], QueueItem.PriorityOption.Normal);
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker",
            1,
            now,
            CancellationToken.None));
        Assert.Equal(queueItem.Id, lease.TargetId);
    }

    [Fact]
    public async Task DatabaseCoordinator_DoesNotLeaseDownloadBeforePauseUntil()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.Now;
        var queueItem = CreateQueueItem("Future", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        queueItem.PauseUntil = now.LocalDateTime.AddMinutes(5);
        dbContext.QueueItems.Add(queueItem);
        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Download,
            queueItem.Id,
            (int)queueItem.Priority,
            now);
        var coordinator = CreateCoordinator(dbContext);

        Assert.Empty(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker-a",
            1,
            now,
            CancellationToken.None));

        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download,
            "download-worker-b",
            1,
            now.AddMinutes(6),
            CancellationToken.None));
        Assert.Equal(queueItem.Id, lease.TargetId);
    }

    [Fact]
    public async Task QueueManagerProcessesPreexistingDurableJobOnlyAfterHostedStart()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem(
            "Preexisting durable job",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        dbContext.QueueItems.Add(queueItem);
        dbContext.QueueNzbContents.Add(CreateQueueNzbContents(queueItem.Id));
        await new DavDatabaseClient(dbContext).EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Download,
            queueItem.Id,
            (int)queueItem.Priority,
            DateTimeOffset.UtcNow);

        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var hosted = Assert.IsAssignableFrom<IHostedService>(queueManager);

        await Task.Delay(100);
        await using (var beforeStart = await _fixture.CreateMigratedContextAsync())
            Assert.True(await beforeStart.QueueItems.AnyAsync(x => x.Id == queueItem.Id));

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!timeout.IsCancellationRequested)
            {
                await using var check = await _fixture.CreateMigratedContextAsync();
                if (await check.HistoryItems.AnyAsync(x => x.Id == queueItem.Id, timeout.Token))
                    break;
                await Task.Delay(25, timeout.Token);
            }

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.False(await assertionContext.QueueItems.AnyAsync(x => x.Id == queueItem.Id));
            Assert.True(await assertionContext.HistoryItems.AnyAsync(x => x.Id == queueItem.Id));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void QueueManagerRegistersWorkerBeforeItsProcessingTaskCanComplete()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Queue/QueueManager.cs"));
        var methodStart = source.IndexOf(
            "private InProgressQueueItem BeginProcessingQueueItem",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task RunQueueItemAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var registration = method.IndexOf(
            "_inProgressQueueItems[queueItem.Id] = inProgressQueueItem",
            StringComparison.Ordinal);
        var processingStart = method.IndexOf(
            "RunQueueItemAsync(inProgressQueueItem, dbClient, progressHook)",
            StringComparison.Ordinal);
        var shutdownRegistration = method.IndexOf(
            "_trackedInProgressQueueItems.Add(inProgressQueueItem)",
            StringComparison.Ordinal);

        Assert.True(registration >= 0 && registration < processingStart);
        Assert.True(shutdownRegistration >= 0 && shutdownRegistration < processingStart);
    }

    [Fact]
    public void QueueManagerDisposeToleratesAnAlreadyDisposedTrackedWorkerToken()
    {
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var queueItem = CreateQueueItem(
            "Already completed worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        var workerToken = new CancellationTokenSource();
        var inProgress = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "worker", Guid.NewGuid(), 1),
            queueItem);
        SetInProgressCancellation(inProgress, workerToken, Task.CompletedTask);
        AddInProgressQueueItem(queueManager, queueItem.Id, inProgress);
        workerToken.Dispose();

        var failure = Record.Exception(queueManager.Dispose);

        Assert.Null(failure);
        queueManager.Dispose();
    }

    [Fact]
    public async Task QueueManagerRemovalToleratesWorkerTokenDisposedDuringCleanup()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem(
            "Disposed cleanup token",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var workerToken = new CancellationTokenSource();
        var inProgress = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "worker", Guid.NewGuid(), 1),
            queueItem);
        SetInProgressCancellation(inProgress, workerToken, Task.CompletedTask);
        AddInProgressQueueItem(queueManager, queueItem.Id, inProgress);
        workerToken.Dispose();

        await queueManager.RemoveQueueItemsAsync([queueItem.Id], new DavDatabaseClient(dbContext));

        Assert.False(await dbContext.QueueItems.AnyAsync(x => x.Id == queueItem.Id));
    }

    [Fact]
    public void QueueManagerUntracksWorkerOnlyAfterResourceDisposal()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Queue/QueueManager.cs"));
        var methodStart = source.IndexOf(
            "private async Task DisposeAndUntrackQueueItemAsync",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf(
            "private async Task RenewLeaseAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var resourceDisposal = method.IndexOf(
            "await inProgressQueueItem.DisposeAsync()",
            StringComparison.Ordinal);
        var untrack = method.IndexOf(
            "_inProgressQueueItems.Remove(inProgressQueueItem.QueueItem.Id)",
            StringComparison.Ordinal);
        var wakeQueue = method.IndexOf("AwakenQueue()", StringComparison.Ordinal);
        var shutdownUntrack = method.IndexOf(
            "_trackedInProgressQueueItems.Remove(inProgressQueueItem)",
            StringComparison.Ordinal);

        Assert.True(resourceDisposal >= 0 && resourceDisposal < untrack);
        Assert.True(untrack < wakeQueue);
        Assert.True(wakeQueue < shutdownUntrack);
    }

    [Fact]
    public async Task QueueManagerStopWaitsForTrackedWorkerResourceDisposal()
    {
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var cachingClient = new ArticleCachingNntpClient(
            usenetClient,
            sharedBudget: new ArticleCacheBudget());
        var dbContext = await _fixture.CreateMigratedContextAsync();
        var workerToken = new CancellationTokenSource();
        var blockingStream = new BlockingDisposeStream();
        var queueItem = CreateQueueItem(
            "Blocking cleanup worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        var inProgress = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "worker", Guid.NewGuid(), 1),
            queueItem);
        SetInProgressResources(
            inProgress,
            workerToken,
            Task.CompletedTask,
            dbContext,
            blockingStream,
            cachingClient);
        AddInProgressQueueItem(queueManager, queueItem.Id, inProgress);
        Task? cleanupTask = null;

        try
        {
            var cleanupMethod = typeof(QueueManager).GetMethod(
                "DisposeAndUntrackQueueItemAsync",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(cleanupMethod);
            cleanupTask = Assert.IsAssignableFrom<Task>(cleanupMethod.Invoke(
                queueManager,
                [inProgress]));
            SetInProgressProcessingTask(inProgress, cleanupTask);
            await blockingStream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var stopTask = queueManager.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);

            blockingStream.AllowDispose();
            await cleanupTask;
            await stopTask;
            Assert.Equal(0, queueManager.GetLaneSnapshot().TotalActive);
        }
        finally
        {
            blockingStream.AllowDispose();
            if (cleanupTask is not null)
            {
                try
                {
                    await cleanupTask;
                }
                catch
                {
                    // The assertions retain the authoritative failure.
                }
            }
            queueManager.Dispose();
            workerToken.Dispose();
            blockingStream.Dispose();
            await cachingClient.DisposeAsync();
            usenetClient.Dispose();
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueueManagerStaleCleanupDoesNotUntrackReplacementWorker()
    {
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var cachingClient = new ArticleCachingNntpClient(
            usenetClient,
            sharedBudget: new ArticleCacheBudget());
        var dbContext = await _fixture.CreateMigratedContextAsync();
        var oldWorkerToken = new CancellationTokenSource();
        var replacementWorkerToken = new CancellationTokenSource();
        var oldQueueItem = CreateQueueItem(
            "Old cleanup worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        var replacementQueueItem = CreateQueueItem(
            "Replacement worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        replacementQueueItem.Id = oldQueueItem.Id;
        var oldWorker = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "old-worker", Guid.NewGuid(), 1),
            oldQueueItem);
        var replacementWorker = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "replacement-worker", Guid.NewGuid(), 2),
            replacementQueueItem);
        SetInProgressResources(
            oldWorker,
            oldWorkerToken,
            Task.CompletedTask,
            dbContext,
            new MemoryStream(),
            cachingClient);
        SetInProgressCancellation(
            replacementWorker,
            replacementWorkerToken,
            Task.CompletedTask);
        AddInProgressQueueItem(queueManager, oldQueueItem.Id, oldWorker);
        SetInProgressQueueItem(queueManager, oldQueueItem.Id, replacementWorker);

        try
        {
            var cleanupMethod = typeof(QueueManager).GetMethod(
                "DisposeAndUntrackQueueItemAsync",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(cleanupMethod);
            var cleanupTask = Assert.IsAssignableFrom<Task>(cleanupMethod.Invoke(
                queueManager,
                [oldWorker]));

            await cleanupTask;

            var tracked = Assert.Single(queueManager.GetInProgressQueueItems());
            Assert.Same(replacementQueueItem, tracked.queueItem);
            Assert.Equal(1, queueManager.GetLaneSnapshot().TotalActive);
        }
        finally
        {
            queueManager.Dispose();
            replacementWorkerToken.Dispose();
            oldWorkerToken.Dispose();
            await cachingClient.DisposeAsync();
            usenetClient.Dispose();
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueueManagerStopWaitsForDisplacedWorkerResourceDisposal()
    {
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var cachingClient = new ArticleCachingNntpClient(
            usenetClient,
            sharedBudget: new ArticleCacheBudget());
        var dbContext = await _fixture.CreateMigratedContextAsync();
        var oldWorkerToken = new CancellationTokenSource();
        var replacementWorkerToken = new CancellationTokenSource();
        var blockingStream = new BlockingDisposeStream();
        var oldQueueItem = CreateQueueItem(
            "Displaced cleanup worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        var replacementQueueItem = CreateQueueItem(
            "Displacing worker",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        replacementQueueItem.Id = oldQueueItem.Id;
        var oldWorker = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "old-worker", Guid.NewGuid(), 1),
            oldQueueItem);
        var replacementWorker = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "replacement-worker", Guid.NewGuid(), 2),
            replacementQueueItem);
        SetInProgressResources(
            oldWorker,
            oldWorkerToken,
            Task.CompletedTask,
            dbContext,
            blockingStream,
            cachingClient);
        SetInProgressCancellation(
            replacementWorker,
            replacementWorkerToken,
            Task.CompletedTask);
        AddInProgressQueueItem(queueManager, oldQueueItem.Id, oldWorker);
        Task? cleanupTask = null;

        try
        {
            var cleanupMethod = typeof(QueueManager).GetMethod(
                "DisposeAndUntrackQueueItemAsync",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(cleanupMethod);
            cleanupTask = Assert.IsAssignableFrom<Task>(cleanupMethod.Invoke(
                queueManager,
                [oldWorker]));
            SetInProgressProcessingTask(oldWorker, cleanupTask);
            await blockingStream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            SetInProgressQueueItem(queueManager, oldQueueItem.Id, replacementWorker);

            var stopTask = queueManager.StopAsync(CancellationToken.None);

            Assert.False(stopTask.IsCompleted);
            blockingStream.AllowDispose();
            await cleanupTask;
            await stopTask;
            var tracked = Assert.Single(queueManager.GetInProgressQueueItems());
            Assert.Same(replacementQueueItem, tracked.queueItem);
        }
        finally
        {
            blockingStream.AllowDispose();
            if (cleanupTask is not null)
            {
                try
                {
                    await cleanupTask;
                }
                catch
                {
                    // The assertions retain the authoritative failure.
                }
            }
            queueManager.Dispose();
            replacementWorkerToken.Dispose();
            oldWorkerToken.Dispose();
            blockingStream.Dispose();
            await cachingClient.DisposeAsync();
            usenetClient.Dispose();
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailWorkerJobAsync_RetriesThenQuarantinesAtMaxAttempts()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();

        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, targetId, priority: 0, now: now);
        var firstLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair,
            owner: "repair-worker",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now);
        Assert.NotNull(firstLease);

        await dbClient.FailWorkerJobAsync(
            firstLease,
            error: "first failure",
            nextAttemptAt: now.AddMinutes(10),
            maxAttempts: 2);

        Assert.Equal(WorkerJob.JobStatus.Retry, firstLease.Status);
        Assert.Equal(now.AddMinutes(10), firstLease.AvailableAt);
        Assert.Null(firstLease.LeaseOwner);
        Assert.Null(firstLease.LeaseExpiresAt);

        var secondLease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair,
            owner: "repair-worker",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(11));
        Assert.NotNull(secondLease);

        await dbClient.FailWorkerJobAsync(
            secondLease,
            error: "second failure",
            nextAttemptAt: now.AddMinutes(20),
            maxAttempts: 2);

        Assert.Equal(WorkerJob.JobStatus.Quarantined, secondLease.Status);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.WorkerFailure), secondLease.LastError);
        Assert.Null(secondLease.LeaseOwner);
        Assert.Null(secondLease.LeaseExpiresAt);
    }

    [Fact]
    public async Task LeaseNextWorkerJobAsync_LeasesPostDownloadVerifyAheadOfBackgroundVerify()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var backgroundTargetId = Guid.NewGuid();
        var postDownloadTargetId = Guid.NewGuid();

        await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Verify,
            backgroundTargetId,
            priority: 0,
            now: now);
        await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Verify,
            postDownloadTargetId,
            priority: 50,
            now: now.AddSeconds(1),
            payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson());

        var lease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Verify,
            owner: "verify-worker",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddSeconds(2));

        Assert.NotNull(lease);
        Assert.Equal(postDownloadTargetId, lease.TargetId);
        Assert.True(DavDatabaseClient.IsPostDownloadVerifyPayload(lease.PayloadJson));
    }

    [Fact]
    public async Task LeaseNextWorkerJobAsync_SkipsExcludedTargetsWithoutLeasingThem()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var activeTargetId = Guid.NewGuid();
        var nextTargetId = Guid.NewGuid();

        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, activeTargetId, priority: 100, now: now);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, nextTargetId, priority: 10, now: now);

        var lease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair,
            owner: "repair-worker",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now,
            excludeTargetIds: [activeTargetId]);

        Assert.NotNull(lease);
        Assert.Equal(nextTargetId, lease.TargetId);

        dbContext.ChangeTracker.Clear();
        var activeJob = await dbContext.WorkerJobs.SingleAsync(x => x.TargetId == activeTargetId);
        Assert.Equal(WorkerJob.JobStatus.Pending, activeJob.Status);
        Assert.Null(activeJob.LeaseOwner);
        Assert.Null(activeJob.LeaseExpiresAt);
        Assert.Equal(0, activeJob.Attempts);
    }

    [Fact]
    public async Task CancelledDownloadWorkerReleasesDurableJobLease()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();

        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, targetId, priority: 0, now: now);
        var lease = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Download,
            owner: "download-worker",
            leaseDuration: TimeSpan.FromMinutes(15),
            now: now);
        Assert.NotNull(lease);

        await InvokeUpdateDownloadWorkerJobAsync(
            CreateDownloadInProgressQueueItem(lease),
            dbClient,
            QueueItemProcessor.ProcessingOutcome.Cancelled);

        dbContext.ChangeTracker.Clear();
        var savedJob = await dbContext.WorkerJobs.SingleAsync(x => x.Id == lease.Id);
        Assert.Equal(WorkerJob.JobStatus.Pending, savedJob.Status);
        Assert.Null(savedJob.LeaseOwner);
        Assert.Null(savedJob.LeaseExpiresAt);
        Assert.Equal(0, savedJob.Attempts);
    }

    [Fact]
    public async Task CancelWorkerJobsAsync_RequestsCancellationWithoutDestroyingActiveLeaseIdentity()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var pendingTarget = Guid.NewGuid();
        var retryTarget = Guid.NewGuid();
        var leasedTarget = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, pendingTarget, 10, now);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, retryTarget, 5, now);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, leasedTarget, 100, now);
        var leased = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Download, "worker-a", TimeSpan.FromMinutes(2), now);
        Assert.NotNull(leased);
        Assert.Equal(leasedTarget, leased.TargetId);
        await dbContext.WorkerJobs.Where(job => job.TargetId == retryTarget)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, WorkerJob.JobStatus.Retry)
                .SetProperty(job => job.LastError, "retry"));
        var identity = ToIdentity(leased);

        await dbClient.CancelWorkerJobsAsync(
            WorkerJob.JobKind.Download, [pendingTarget, retryTarget, leasedTarget], CancellationToken.None);

        dbContext.ChangeTracker.Clear();
        var pending = await dbContext.WorkerJobs.SingleAsync(job => job.TargetId == pendingTarget);
        var retry = await dbContext.WorkerJobs.SingleAsync(job => job.TargetId == retryTarget);
        var active = await dbContext.WorkerJobs.SingleAsync(job => job.TargetId == leasedTarget);
        Assert.Equal(WorkerJob.JobStatus.Cancelled, pending.Status);
        Assert.Equal(WorkerJob.JobStatus.Cancelled, retry.Status);
        Assert.Equal(WorkerJob.JobStatus.Leased, active.Status);
        Assert.Equal(identity.Owner, active.LeaseOwner);
        Assert.Equal(identity.Token, active.LeaseToken);
        Assert.Equal(identity.Generation, active.LeaseGeneration);
        Assert.NotNull(active.CancelRequestedAt);

        var coordinator = CreateCoordinator(dbContext);
        Assert.False(await coordinator.RenewAsync(identity, now.AddSeconds(30), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            identity, WorkerJob.FailureClass.Cancelled, "cancelled by request", now, 3,
            now.AddSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task CancelWorkerJobsAsync_UsesOneAtomicWorkerJobUpdate()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.TrimStart().StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("WorkerJobs", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var pending = CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Pending, now);
        var leased = CreateWorkerJob(
            WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Leased, now, now.AddMinutes(2));
        leased.LeaseToken = Guid.NewGuid();
        leased.LeaseGeneration = 1;
        dbContext.WorkerJobs.AddRange(pending, leased);
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        await new DavDatabaseClient(dbContext).CancelWorkerJobsAsync(
            WorkerJob.JobKind.Verify, [pending.TargetId, leased.TargetId]);

        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    public async Task CancelledPendingJobCanBeReEnqueuedInTheSameTrackedContext()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Verify, targetId, 1, now);

        await dbClient.CancelWorkerJobsAsync(WorkerJob.JobKind.Verify, [targetId]);
        var reenqueued = await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Verify, targetId, 2, now.AddMinutes(1));

        AssertFreshPendingJob(reenqueued, now.AddMinutes(1), attempts: 0);
    }

    [Fact]
    public async Task EnqueueWorkerJobAsync_ResetsAllStaleStateAfterAcknowledgedCancellation()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Verify, targetId, 1, now);
        var coordinator = CreateCoordinator(dbContext);
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));
        Assert.True(await coordinator.ReportProgressAsync(
            lease.Identity, "{\"percent\":50}", now.AddSeconds(1), CancellationToken.None));
        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(2), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            lease.Identity, WorkerJob.FailureClass.Cancelled, "cancelled", now, 3,
            now.AddSeconds(3), CancellationToken.None));
        await SetTerminalStaleStateAsync(dbContext, lease.Identity.JobId, "stale-result");

        var reenqueued = await dbClient.EnqueueWorkerJobAsync(
            WorkerJob.JobKind.Verify, targetId, 7, now.AddMinutes(1), payloadJson: "new-payload");

        AssertFreshPendingJob(reenqueued, now.AddMinutes(1), attempts: 0);
        Assert.Equal("new-payload", reenqueued.PayloadJson);
    }

    [Fact]
    public async Task EnqueueWorkerJobsAsync_ResetsAllStaleStateAfterExpiredCancellation()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var oldPayload = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson();
        await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Repair, [firstTarget, secondTarget], 1, now, payloadJson: oldPayload);
        var coordinator = CreateCoordinator(dbContext, repairCapacity: 2);
        var leases = await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-a", 2, now, CancellationToken.None);
        Assert.Equal(2, leases.Count);
        foreach (var lease in leases)
            Assert.True(await coordinator.RequestCancellationAsync(
                lease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));
        Assert.Empty(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-b", 2, now.AddMinutes(3), CancellationToken.None));
        foreach (var lease in leases)
            await SetTerminalStaleStateAsync(dbContext, lease.Identity.JobId, "stale-result");

        var changed = await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Repair, [firstTarget, secondTarget], 9, now.AddMinutes(4), payloadJson: null);

        Assert.Equal(2, changed);
        dbContext.ChangeTracker.Clear();
        var jobs = await dbContext.WorkerJobs.OrderBy(job => job.TargetId).ToListAsync();
        Assert.All(jobs, job => AssertFreshPendingJob(job, now.AddMinutes(4), attempts: 0));
        Assert.All(jobs, job => Assert.Null(job.PayloadJson));
        Assert.Equal(
            leases.Select(lease => lease.Identity.Generation).OrderBy(value => value),
            jobs.Select(job => job.LeaseGeneration).OrderBy(value => value));

        var reLeased = await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-c", 2, now.AddMinutes(5), CancellationToken.None);
        Assert.Equal(2, reLeased.Count);
        Assert.All(reLeased, newLease => Assert.Contains(leases, oldLease =>
            oldLease.Identity.JobId == newLease.Identity.JobId
            && newLease.Identity.Generation == oldLease.Identity.Generation + 1));
    }

    [Fact]
    public async Task QueueManagerRejectedCompletionIsReportedAsLostOwnershipWithoutFallbackMutation()
    {
        var coordinator = new RecordingWorkerJobCoordinator { CompleteResult = false };
        var item = CreateDownloadInProgressQueueItem(CreateLeasedWorkerJob());

        var accepted = await InvokeUpdateDownloadWorkerJobAsync(
            item, coordinator, QueueItemProcessor.ProcessingOutcome.Completed);

        Assert.False(accepted);
        Assert.Equal(1, coordinator.CompleteCalls);
        Assert.Equal(0, coordinator.FailCalls);
        Assert.Equal(0, coordinator.ReleaseCalls);
    }

    [Fact]
    public async Task QueueManagerRejectedFailureIsReportedAsLostOwnershipWithoutFallbackMutation()
    {
        var coordinator = new RecordingWorkerJobCoordinator { FailResult = false };
        var item = CreateDownloadInProgressQueueItem(CreateLeasedWorkerJob());

        var accepted = await InvokeUpdateDownloadWorkerJobAsync(
            item, coordinator, QueueItemProcessor.ProcessingOutcome.RetryScheduled);

        Assert.False(accepted);
        Assert.Equal(0, coordinator.CompleteCalls);
        Assert.Equal(1, coordinator.FailCalls);
        Assert.Equal(0, coordinator.ReleaseCalls);
    }

    [Fact]
    public async Task QueueManagerCancellationAfterLeaseReleasesBeforeWorkerHandoff()
    {
        await _fixture.ResetAsync();
        using var cancellation = new CancellationTokenSource();
        var identity = new WorkerLeaseIdentity(Guid.NewGuid(), "worker", Guid.NewGuid(), 1);
        var coordinator = new RecordingWorkerJobCoordinator
        {
            ReleaseExceptionsRemaining = 1,
            LeaseHandler = (_, _, _, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<IReadOnlyList<WorkerLease>>([
                    new WorkerLease(
                        identity,
                        WorkerJob.JobKind.Download,
                        Guid.NewGuid(),
                        Priority: 0,
                        Attempt: 1,
                        PayloadJson: null,
                        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2),
                        CancellationRequested: false)
                ]);
            }
        };
        var configManager = new ConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager),
            coordinator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeTryStartNextQueueItemAsync(queueManager, cancellation.Token));

        Assert.Equal(2, coordinator.ReleaseCalls);
        Assert.Equal(identity, coordinator.LastReleasedLease);
        Assert.False(coordinator.LastReleaseToken.CanBeCanceled);
        Assert.Equal(0, queueManager.GetLaneSnapshot().TotalActive);
    }

    [Fact]
    public async Task QueueManagerDoesNotHandoffASecondLocalWorkerForTheSameTarget()
    {
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        var queueItem = CreateQueueItem(
            "Duplicate local target",
            QueueItem.PriorityOption.Normal,
            DateTime.Now);
        var replacementIdentity = new WorkerLeaseIdentity(
            Guid.NewGuid(),
            "replacement-worker",
            Guid.NewGuid(),
            2);
        var coordinator = new RecordingWorkerJobCoordinator
        {
            LeaseHandler = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<WorkerLease>>([
                new WorkerLease(
                    replacementIdentity,
                    WorkerJob.JobKind.Download,
                    queueItem.Id,
                    Priority: 0,
                    Attempt: 2,
                    PayloadJson: null,
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2),
                    CancellationRequested: false)
            ])
        };
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager),
            coordinator);
        var activeToken = new CancellationTokenSource();
        var activeWorker = CreateDownloadInProgressQueueItem(
            new WorkerLeaseIdentity(Guid.NewGuid(), "active-worker", Guid.NewGuid(), 1),
            queueItem);
        SetInProgressCancellation(activeWorker, activeToken, Task.CompletedTask);
        activeWorker.GetType().GetProperty("Stage")!
            .SetValue(activeWorker, QueueProcessingStage.Moving);
        AddInProgressQueueItem(queueManager, queueItem.Id, activeWorker);

        var started = await InvokeTryStartNextQueueItemAsync(queueManager, CancellationToken.None);

        Assert.False(started);
        Assert.True(activeToken.IsCancellationRequested);
        Assert.Equal(1, coordinator.ReleaseCalls);
        Assert.Equal(replacementIdentity, coordinator.LastReleasedLease);
        var tracked = Assert.Single(queueManager.GetInProgressQueueItems());
        Assert.Same(queueItem, tracked.queueItem);
        activeToken.Dispose();
    }

    [Fact]
    public async Task QueueManagerTerminalUpdateExceptionReconcilesOnce()
    {
        var coordinator = new RecordingWorkerJobCoordinator { CompleteExceptionsRemaining = 1 };
        var item = CreateDownloadInProgressQueueItem(CreateLeasedWorkerJob());
        var configManager = new ConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager),
            coordinator);

        var accepted = await InvokeReconcileDownloadWorkerJobAsync(
            queueManager,
            item,
            QueueItemProcessor.ProcessingOutcome.Completed);

        Assert.True(accepted);
        Assert.Equal(2, coordinator.CompleteCalls);
    }

    [Fact]
    public async Task QueueManagerRetryUpdateExceptionReconcilesOnce()
    {
        var coordinator = new RecordingWorkerJobCoordinator { FailExceptionsRemaining = 1 };
        var item = CreateDownloadInProgressQueueItem(CreateLeasedWorkerJob());
        var configManager = new ConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager),
            coordinator);

        var accepted = await InvokeReconcileDownloadWorkerJobAsync(
            queueManager,
            item,
            QueueItemProcessor.ProcessingOutcome.RetryScheduled);

        Assert.True(accepted);
        Assert.Equal(2, coordinator.FailCalls);
        Assert.Single(coordinator.FailNextAttemptAt.Distinct());
        Assert.Single(coordinator.FailNow.Distinct());
    }

    [Fact]
    public async Task ActiveQueueRemovalPersistsCancellationBeforeStoppingWorkerAndAcknowledgesPromptly()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var queueItem = CreateQueueItem("Active removal", QueueItem.PriorityOption.Normal, DateTime.UtcNow);
        var unrelatedQueueItem = CreateQueueItem(
            "Unrelated tracked", QueueItem.PriorityOption.Normal, DateTime.UtcNow.AddSeconds(1));
        dbContext.QueueItems.AddRange(queueItem, unrelatedQueueItem);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, queueItem.Id, 1, now);
        await dbContext.SaveChangesAsync();
        unrelatedQueueItem.Priority = QueueItem.PriorityOption.High;
        var coordinator = new DatabaseWorkerJobCoordinator(
            new TestWorkerCapacityPolicy(), Options.Create(new WorkerLeaseOptions()));
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "queue.paused", ConfigValue = "true" }
        ]);
        var websocketManager = new WebsocketManager();
        using var usenetClient = new UsenetStreamingClient(configManager, websocketManager);
        using var queueManager = new QueueManager(
            usenetClient,
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager),
            coordinator);
        var workerCts = new CancellationTokenSource();
        var inProgress = CreateDownloadInProgressQueueItem(lease.Identity, queueItem);
        var workerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestWasVisible = false;
        var queueWasDeleted = false;
        var terminalAccepted = false;
        using var registration = workerCts.Token.Register(() =>
        {
            using var callbackContext = new DavDatabaseContext();
            requestWasVisible = callbackContext.WorkerJobs.AsNoTracking()
                .Single(job => job.Id == lease.Identity.JobId).CancelRequestedAt != null;
            queueWasDeleted = !callbackContext.QueueItems.AsNoTracking()
                .Any(item => item.Id == queueItem.Id);
            terminalAccepted = InvokeUpdateDownloadWorkerJobAsync(
                    inProgress, coordinator, QueueItemProcessor.ProcessingOutcome.Cancelled)
                .GetAwaiter().GetResult();
            workerStopped.SetResult();
        });
        SetInProgressCancellation(inProgress, workerCts, workerStopped.Task);
        AddInProgressQueueItem(queueManager, queueItem.Id, inProgress);

        await queueManager.RemoveQueueItemsAsync([queueItem.Id], dbClient);

        Assert.True(requestWasVisible);
        Assert.True(queueWasDeleted);
        Assert.True(terminalAccepted);
        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, saved.Status);
        Assert.Equal(lease.Identity.Owner, saved.LeaseOwner);
        Assert.Equal(lease.Identity.Token, saved.LeaseToken);
        Assert.Equal(lease.Identity.Generation, saved.LeaseGeneration);
        await using var verificationContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await verificationContext.QueueItems.AnyAsync(item => item.Id == queueItem.Id));
        Assert.Equal(QueueItem.PriorityOption.Normal,
            (await verificationContext.QueueItems.AsNoTracking()
                .SingleAsync(item => item.Id == unrelatedQueueItem.Id)).Priority);
    }

    [Fact]
    public async Task RemoveQueueItemsAsync_DeleteFailureRollsBackCancellationAndQueueDeletion()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var queueItem = CreateQueueItem("Rollback", QueueItem.PriorityOption.Normal, DateTime.UtcNow);
        dbContext.QueueItems.Add(queueItem);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, queueItem.Id, 1, now);
        await dbContext.SaveChangesAsync();
        var coordinator = CreateCoordinator(dbContext);
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_queue_delete
            BEFORE DELETE ON QueueItems
            BEGIN
                SELECT RAISE(ABORT, 'forced queue delete failure');
            END;
            """);

        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                dbClient.RemoveQueueItemsAsync([queueItem.Id]));
            Assert.Contains("forced queue delete failure", exception.ToString());

            await using var verificationContext = await _fixture.CreateMigratedContextAsync();
            Assert.True(await verificationContext.QueueItems.AsNoTracking()
                .AnyAsync(item => item.Id == queueItem.Id));
            var saved = await verificationContext.WorkerJobs.AsNoTracking().SingleAsync();
            Assert.Equal(WorkerJob.JobStatus.Leased, saved.Status);
            Assert.Null(saved.CancelRequestedAt);
            Assert.Equal(lease.Identity.Owner, saved.LeaseOwner);
            Assert.Equal(lease.Identity.Token, saved.LeaseToken);
            Assert.Equal(lease.Identity.Generation, saved.LeaseGeneration);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_queue_delete");
        }
    }

    [Fact]
    public async Task LeaseTopQueueItemAsync_CreatesDurableDownloadJobAndSkipsPausedOrLeasedItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var pausedItem = CreateQueueItem("Paused", QueueItem.PriorityOption.Paused, DateTime.UtcNow);
        var eligibleItem = CreateQueueItem("Eligible", QueueItem.PriorityOption.Normal, DateTime.UtcNow.AddSeconds(1));
        dbContext.QueueItems.AddRange(pausedItem, eligibleItem);
        dbContext.QueueNzbContents.AddRange(
            CreateQueueNzbContents(pausedItem.Id),
            CreateQueueNzbContents(eligibleItem.Id)
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var firstLease = await dbClient.LeaseTopQueueItemAsync(
            excludeIds: [],
            owner: "download-worker-a",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now);
        await firstLease.queueNzbStream!.DisposeAsync();
        var firstLeaseKind = firstLease.workerJob?.Kind;
        var firstLeaseStatus = firstLease.workerJob?.Status;
        var firstLeaseOwner = firstLease.workerJob?.LeaseOwner;
        var firstLeaseAttempts = firstLease.workerJob?.Attempts;
        var secondLease = await dbClient.LeaseTopQueueItemAsync(
            excludeIds: [],
            owner: "download-worker-b",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(1));
        var expiredLease = await dbClient.LeaseTopQueueItemAsync(
            excludeIds: [],
            owner: "download-worker-c",
            leaseDuration: TimeSpan.FromMinutes(5),
            now: now.AddMinutes(6));
        await expiredLease.queueNzbStream!.DisposeAsync();

        Assert.Equal(eligibleItem.Id, firstLease.queueItem?.Id);
        Assert.NotNull(firstLease.workerJob);
        Assert.Equal(WorkerJob.JobKind.Download, firstLeaseKind);
        Assert.Equal(WorkerJob.JobStatus.Leased, firstLeaseStatus);
        Assert.Equal("download-worker-a", firstLeaseOwner);
        Assert.Equal(1, firstLeaseAttempts);

        Assert.Null(secondLease.queueItem);
        Assert.Null(secondLease.queueNzbStream);
        Assert.Null(secondLease.workerJob);

        Assert.Equal(eligibleItem.Id, expiredLease.queueItem?.Id);
        Assert.NotNull(expiredLease.workerJob);
        Assert.Equal("download-worker-c", expiredLease.workerJob.LeaseOwner);
        Assert.Equal(2, expiredLease.workerJob.Attempts);
    }

    [Fact]
    public async Task LeaseTopQueueItemAsync_TerminalizesExpiredCancellationInsteadOfReLeasing()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem("Cancelled", QueueItem.PriorityOption.Normal, DateTime.UtcNow);
        dbContext.QueueItems.Add(queueItem);
        dbContext.QueueNzbContents.Add(CreateQueueNzbContents(queueItem.Id));
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Download, queueItem.Id, 1, now);
        var coordinator = CreateCoordinator(dbContext);
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));
        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));

        var result = await dbClient.LeaseTopQueueItemAsync(
            [], "worker-b", TimeSpan.FromMinutes(2), now.AddMinutes(3));

        Assert.Null(result.queueItem);
        Assert.Null(result.workerJob);
        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, saved.Status);
        Assert.Equal(lease.Identity.Generation, saved.LeaseGeneration);
    }

    [Fact]
    public async Task LeaseNextWorkerJobAsync_TerminalizesExpiredCancellationInsteadOfReLeasing()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, targetId, 1, now);
        var coordinator = CreateCoordinator(dbContext);
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-a", 1, now, CancellationToken.None));
        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));

        var result = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair, "worker-b", TimeSpan.FromMinutes(2), now.AddMinutes(3));

        Assert.Null(result);
        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, saved.Status);
        Assert.Equal(lease.Identity.Generation, saved.LeaseGeneration);
    }

    [Fact]
    public async Task GetWorkerJobQueueStatsAsync_CountsReadyLeasedRetryAndQuarantinedByKind()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var pendingDownload = CreateQueueItem("Pending download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var activeDownload = CreateQueueItem("Active download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var expiredDownload = CreateQueueItem("Expired download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        dbContext.QueueItems.AddRange(pendingDownload, activeDownload, expiredDownload);
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Pending,
                now.AddMinutes(-1),
                targetId: pendingDownload.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Leased,
                now.AddMinutes(-1),
                now.AddMinutes(5),
                activeDownload.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Leased,
                now.AddMinutes(-1),
                now.AddMinutes(-1),
                expiredDownload.Id),
            CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Retry, now.AddMinutes(10)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Quarantined, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Retry, now.AddMinutes(-1))
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var stats = await dbClient.GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(2, stats.Download.Ready);
        Assert.Equal(1, stats.Download.Leased);
        Assert.Equal(1, stats.Download.ExpiredLeased);
        Assert.Equal(0, stats.Download.Retry);
        Assert.Equal(0, stats.Verify.Ready);
        Assert.Equal(1, stats.Verify.Retry);
        Assert.Equal(1, stats.Repair.Ready);
        Assert.Equal(1, stats.Repair.Retry);
        Assert.Equal(1, stats.Repair.Quarantined);
    }

    [Fact]
    public async Task GetWorkerJobQueueStatsAsync_AppliesDownloadQueueReadinessToReadyAndRetryCounts()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var duePending = CreateQueueItem("Due pending", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var dueRetry = CreateQueueItem("Due retry", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var pausedRetry = CreateQueueItem("Paused retry", QueueItem.PriorityOption.Paused, now.LocalDateTime);
        var delayedRetry = CreateQueueItem("Delayed retry", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        delayedRetry.PauseUntil = now.LocalDateTime.AddMinutes(5);
        var futureRetry = CreateQueueItem("Future retry", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var cancelledPending = CreateQueueItem("Cancelled pending", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var active = CreateQueueItem("Active", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        dbContext.QueueItems.AddRange(
            duePending,
            dueRetry,
            pausedRetry,
            delayedRetry,
            futureRetry,
            cancelledPending,
            active);
        var cancelledWorker = CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Pending,
            now.AddMinutes(-1),
            targetId: cancelledPending.Id);
        cancelledWorker.CancelRequestedAt = now;
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Pending,
                now.AddMinutes(-1),
                targetId: duePending.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Retry,
                now.AddMinutes(-1),
                targetId: dueRetry.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Retry,
                now.AddMinutes(-1),
                targetId: pausedRetry.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Retry,
                now.AddMinutes(-1),
                targetId: delayedRetry.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Retry,
                now.AddMinutes(5),
                targetId: futureRetry.Id),
            cancelledWorker,
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Leased,
                now.AddMinutes(-1),
                now.AddMinutes(5),
                active.Id));
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(2, stats.Download.Ready);
        Assert.Equal(1, stats.Download.Retry);
        Assert.Equal(1, stats.Download.Leased);
    }

    [Fact]
    public async Task GetWorkerJobQueueStatsAsync_CountsEligibleQueueItemWithoutWorkerJobAsReady()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.QueueItems.Add(CreateQueueItem(
            "Missing durable job",
            QueueItem.PriorityOption.Normal,
            now.LocalDateTime));
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(1, stats.Download.Ready);
        Assert.Equal(0, stats.Download.Retry);
    }

    [Fact]
    public async Task GetWorkerJobQueueStatsAsync_DoesNotCountOrphanDownloadWorkerJobAsReady()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.WorkerJobs.Add(CreateWorkerJob(
            WorkerJob.JobKind.Download,
            WorkerJob.JobStatus.Pending,
            now.AddMinutes(-1)));
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(0, stats.Download.Ready);
        Assert.Equal(0, stats.Download.Retry);
    }

    [Theory]
    [InlineData(WorkerJob.JobKind.Verify)]
    [InlineData(WorkerJob.JobKind.Repair)]
    public async Task GetWorkerJobQueueStatsAsync_DoesNotCountCancellationRequestedWorkerAsReady(
        WorkerJob.JobKind kind)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var worker = CreateWorkerJob(
            kind,
            WorkerJob.JobStatus.Pending,
            now.AddMinutes(-1));
        worker.CancelRequestedAt = now;
        dbContext.WorkerJobs.Add(worker);
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);
        var kindStats = kind == WorkerJob.JobKind.Verify ? stats.Verify : stats.Repair;

        Assert.Equal(0, kindStats.Ready);
    }

    [Theory]
    [InlineData(WorkerJob.JobStatus.Pending)]
    [InlineData(WorkerJob.JobStatus.Retry)]
    public async Task GetWorkerJobQueueStatsAsync_DoesNotCountDownloadWithFutureStaleLeaseAsEligible(
        WorkerJob.JobStatus status)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var queueItem = CreateQueueItem(
            "Stale future lease",
            QueueItem.PriorityOption.Normal,
            now.LocalDateTime);
        dbContext.QueueItems.Add(queueItem);
        dbContext.WorkerJobs.Add(CreateWorkerJob(
            WorkerJob.JobKind.Download,
            status,
            now.AddMinutes(-1),
            now.AddMinutes(5),
            queueItem.Id));
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(0, stats.Download.Ready);
        Assert.Equal(0, stats.Download.Retry);
    }

    [Fact]
    public async Task GetWorkerJobQueueStatsAsync_UsesOneWorkerJobsQuery()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("WorkerJobs", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var pendingDownload = CreateQueueItem("Pending download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var activeDownload = CreateQueueItem("Active download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        var expiredDownload = CreateQueueItem("Expired download", QueueItem.PriorityOption.Normal, now.LocalDateTime);
        dbContext.QueueItems.AddRange(pendingDownload, activeDownload, expiredDownload);
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Pending,
                now.AddMinutes(-1),
                targetId: pendingDownload.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Leased,
                now.AddMinutes(-1),
                now.AddMinutes(5),
                activeDownload.Id),
            CreateWorkerJob(
                WorkerJob.JobKind.Download,
                WorkerJob.JobStatus.Leased,
                now.AddMinutes(-1),
                now.AddMinutes(-1),
                expiredDownload.Id),
            CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Retry, now.AddMinutes(10)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Quarantined, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Retry, now.AddMinutes(-1))
        );
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var stats = await new DavDatabaseClient(dbContext).GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(1, interceptor.Count);
        Assert.Equal(2, stats.Download.Ready);
        Assert.Equal(1, stats.Download.Leased);
        Assert.Equal(1, stats.Download.ExpiredLeased);
        Assert.Equal(0, stats.Verify.Ready);
        Assert.Equal(1, stats.Repair.Ready);
    }

    [Fact]
    public async Task EnqueueWorkerJobsAsync_UpsertsManyTargetsWithOneSave()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var existingCompletedTarget = Guid.NewGuid();
        var existingRetryTarget = Guid.NewGuid();
        var newTarget = Guid.NewGuid();

        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(
                WorkerJob.JobKind.Verify,
                WorkerJob.JobStatus.Completed,
                now.AddDays(-1),
                targetId: existingCompletedTarget),
            CreateWorkerJob(
                WorkerJob.JobKind.Verify,
                WorkerJob.JobStatus.Retry,
                now.AddMinutes(10),
                targetId: existingRetryTarget));
        await dbContext.SaveChangesAsync();

        var changed = await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Verify,
            [existingCompletedTarget, existingRetryTarget, newTarget, newTarget],
            priority: 7,
            now: now);

        var jobs = await dbContext.WorkerJobs
            .Where(x => x.Kind == WorkerJob.JobKind.Verify)
            .OrderBy(x => x.TargetId)
            .ToListAsync();
        var completedJob = jobs.Single(x => x.TargetId == existingCompletedTarget);
        var retryJob = jobs.Single(x => x.TargetId == existingRetryTarget);
        var newJob = jobs.Single(x => x.TargetId == newTarget);

        Assert.Equal(3, changed);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(WorkerJob.JobStatus.Pending, completedJob.Status);
        Assert.Equal(0, completedJob.Attempts);
        Assert.Equal(WorkerJob.JobStatus.Retry, retryJob.Status);
        Assert.Equal(now.AddMinutes(10), retryJob.AvailableAt);
        Assert.Equal(WorkerJob.JobStatus.Pending, newJob.Status);
        Assert.All(jobs, job => Assert.Equal(7, job.Priority));
    }

    [Fact]
    public async Task EnqueueWorkerJobsAsync_CanDeferSaveForAtomicQueueCompletion()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();

        var changed = await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Verify,
            [targetId],
            priority: 9,
            now: now,
            saveChanges: false);

        Assert.Equal(1, changed);
        Assert.Equal(WorkerJob.JobStatus.Pending, dbContext.ChangeTracker
            .Entries<WorkerJob>()
            .Single(x => x.Entity.TargetId == targetId)
            .Entity
            .Status);

        await using var beforeSaveContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await beforeSaveContext.WorkerJobs.AnyAsync(x => x.TargetId == targetId));

        await dbContext.SaveChangesAsync();
        await using var afterSaveContext = await _fixture.CreateMigratedContextAsync();
        var savedJob = await afterSaveContext.WorkerJobs.SingleAsync(x => x.TargetId == targetId);
        Assert.Equal(WorkerJob.JobKind.Verify, savedJob.Kind);
        Assert.Equal(9, savedJob.Priority);
    }

    [Fact]
    public async Task EnqueueWorkerJobsAsync_DeduplicatesRepeatedDeferredCallsBeforeSave()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var now = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();

        var firstChanged = await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Verify,
            [targetId],
            priority: 5,
            now: now,
            saveChanges: false);
        var secondChanged = await dbClient.EnqueueWorkerJobsAsync(
            WorkerJob.JobKind.Verify,
            [targetId],
            priority: 9,
            now: now.AddSeconds(1),
            saveChanges: false);

        Assert.Equal(1, firstChanged);
        Assert.Equal(1, secondChanged);
        var localJob = Assert.Single(
            dbContext.ChangeTracker.Entries<WorkerJob>(),
            x => x.Entity.Kind == WorkerJob.JobKind.Verify && x.Entity.TargetId == targetId);
        Assert.Equal(9, localJob.Entity.Priority);

        await dbContext.SaveChangesAsync();

        var savedJob = await dbContext.WorkerJobs.SingleAsync(x =>
            x.Kind == WorkerJob.JobKind.Verify && x.TargetId == targetId);
        Assert.Equal(9, savedJob.Priority);
    }

    [Fact]
    public async Task Migration_CreatesWorkerJobOperationalIndexes()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WorkerJobs'";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        var indexNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexNames.Add(reader.GetString(0));

        Assert.Contains("IX_WorkerJobs_Kind_Status_Priority_AvailableAt_CreatedAt", indexNames);
        Assert.Contains("IX_WorkerJobs_Kind_Status_LeaseExpiresAt", indexNames);
        Assert.Contains("IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration", indexNames);
    }

    [Theory]
    [InlineData(nameof(WorkerJob.ProgressJson))]
    [InlineData(nameof(WorkerJob.ResultJson))]
    public async Task SaveChangesAsync_AcceptsWorkerJobJsonAtUtf8ByteLimit(string propertyName)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var workerJob = CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Pending, DateTimeOffset.UtcNow);
        SetWorkerJobJson(workerJob, propertyName, new string('a', 16 * 1024));
        dbContext.WorkerJobs.Add(workerJob);

        await dbContext.SaveChangesAsync();

        Assert.Single(await dbContext.WorkerJobs.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(nameof(WorkerJob.ProgressJson))]
    [InlineData(nameof(WorkerJob.ResultJson))]
    public async Task SaveChanges_RejectsWorkerJobJsonOverUtf8ByteLimitBeforePersistence(string propertyName)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var workerJob = CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Pending, DateTimeOffset.UtcNow);
        SetWorkerJobJson(workerJob, propertyName, new string('a', 16 * 1024 + 1));
        dbContext.WorkerJobs.Add(workerJob);

        var exception = Assert.Throws<InvalidOperationException>(() => dbContext.SaveChanges());

        Assert.Equal($"WorkerJob {propertyName} exceeds the 16384 UTF-8 byte limit.", exception.Message);
        Assert.Empty(await dbContext.WorkerJobs.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(nameof(WorkerJob.ProgressJson))]
    [InlineData(nameof(WorkerJob.ResultJson))]
    public async Task SaveChangesAsync_RejectsMultibyteWorkerJobJsonOverUtf8ByteLimit(string propertyName)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var workerJob = CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Pending, DateTimeOffset.UtcNow);
        var value = new string('é', 8193);
        Assert.True(value.Length <= 16 * 1024);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(value) > 16 * 1024);
        SetWorkerJobJson(workerJob, propertyName, value);
        dbContext.WorkerJobs.Add(workerJob);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Equal($"WorkerJob {propertyName} exceeds the 16384 UTF-8 byte limit.", exception.Message);
        Assert.Empty(await dbContext.WorkerJobs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Migration_CreatesRenewableWorkerLeaseColumns()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();

        var columns = await ReadSqliteColumnsAsync(dbContext, "WorkerJobs");

        Assert.Contains("LeaseToken", columns);
        Assert.Contains("LeaseGeneration", columns);
        Assert.Contains("LastHeartbeatAt", columns);
        Assert.Contains("StartedAt", columns);
        Assert.Contains("CancelRequestedAt", columns);
        Assert.Contains("FailureKind", columns);
        Assert.Contains("ProgressJson", columns);
        Assert.Contains("ProgressUpdatedAt", columns);
        Assert.Contains("ResultJson", columns);
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(DavDatabaseContext dbContext, string tableName)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        return columns;
    }

    private static void SetWorkerJobJson(WorkerJob workerJob, string propertyName, string value)
    {
        switch (propertyName)
        {
            case nameof(WorkerJob.ProgressJson):
                workerJob.ProgressJson = value;
                break;
            case nameof(WorkerJob.ResultJson):
                workerJob.ResultJson = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null);
        }
    }

    private static QueueItem CreateQueueItem
    (
        string jobName,
        QueueItem.PriorityOption priority,
        DateTime createdAt
    )
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = null
        };
    }

    private static QueueNzbContents CreateQueueNzbContents(Guid id)
    {
        return new QueueNzbContents
        {
            Id = id,
            NzbContents = "<nzb />"
        };
    }

    private static WorkerJob CreateWorkerJob
    (
        WorkerJob.JobKind kind,
        WorkerJob.JobStatus status,
        DateTimeOffset availableAt,
        DateTimeOffset? leaseExpiresAt = null,
        Guid? targetId = null
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = status,
            TargetId = targetId ?? Guid.NewGuid(),
            Priority = 0,
            Attempts = status is WorkerJob.JobStatus.Pending ? 0 : 1,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = availableAt,
            LeaseExpiresAt = leaseExpiresAt,
            LeaseOwner = status is WorkerJob.JobStatus.Leased ? "worker" : null,
            LastError = status is WorkerJob.JobStatus.Retry or WorkerJob.JobStatus.Quarantined
                ? "failure"
                : null
        };
    }

    private static object CreateDownloadInProgressQueueItem(WorkerJob workerJob)
    {
        var type = typeof(QueueManager).GetNestedType("InProgressQueueItem",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(type);
        var value = Activator.CreateInstance(type, nonPublic: true);
        Assert.NotNull(value);
        type.GetProperty("WorkerLease")!.SetValue(value, new WorkerLeaseIdentity(
            workerJob.Id,
            workerJob.LeaseOwner!,
            workerJob.LeaseToken!.Value,
            workerJob.LeaseGeneration));
        type.GetProperty("QueueItem")!.SetValue(value, CreateQueueItem(
            "Lease test", QueueItem.PriorityOption.Normal, DateTime.UtcNow));
        return value;
    }

    private static object CreateDownloadInProgressQueueItem(
        WorkerLeaseIdentity identity,
        QueueItem queueItem)
    {
        var type = typeof(QueueManager).GetNestedType("InProgressQueueItem",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(type);
        var value = Activator.CreateInstance(type, nonPublic: true);
        Assert.NotNull(value);
        type.GetProperty("WorkerLease")!.SetValue(value, identity);
        type.GetProperty("QueueItem")!.SetValue(value, queueItem);
        return value;
    }

    private static void SetInProgressCancellation(
        object inProgress,
        CancellationTokenSource cts,
        Task processingTask)
    {
        var type = inProgress.GetType();
        type.GetProperty("CancellationTokenSource")!.SetValue(inProgress, cts);
        type.GetProperty("ProcessingTask")!.SetValue(inProgress, processingTask);
    }

    private static void SetInProgressResources(
        object inProgress,
        CancellationTokenSource cts,
        Task processingTask,
        DavDatabaseContext dbContext,
        Stream nzbStream,
        ArticleCachingNntpClient usenetClient)
    {
        SetInProgressCancellation(inProgress, cts, processingTask);
        var type = inProgress.GetType();
        type.GetProperty("DbContext")!.SetValue(inProgress, dbContext);
        type.GetProperty("NzbStream")!.SetValue(inProgress, nzbStream);
        type.GetProperty("UsenetClient")!.SetValue(inProgress, usenetClient);
    }

    private static void SetInProgressProcessingTask(object inProgress, Task processingTask) =>
        inProgress.GetType().GetProperty("ProcessingTask")!.SetValue(inProgress, processingTask);

    private static void AddInProgressQueueItem(QueueManager queueManager, Guid id, object inProgress)
    {
        var field = typeof(QueueManager).GetField("_inProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(queueManager));
        dictionary.Add(id, inProgress);
        TrackInProgressQueueItem(queueManager, inProgress);
    }

    private static void SetInProgressQueueItem(QueueManager queueManager, Guid id, object inProgress)
    {
        var field = typeof(QueueManager).GetField("_inProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(queueManager));
        dictionary[id] = inProgress;
        TrackInProgressQueueItem(queueManager, inProgress);
    }

    private static void TrackInProgressQueueItem(QueueManager queueManager, object inProgress)
    {
        var field = typeof(QueueManager).GetField("_trackedInProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var trackedItems = field.GetValue(queueManager);
        Assert.NotNull(trackedItems);
        var addMethod = trackedItems.GetType().GetMethod("Add");
        Assert.NotNull(addMethod);
        Assert.Equal(true, addMethod.Invoke(trackedItems, [inProgress]));
    }

    private static WorkerLeaseIdentity ToIdentity(WorkerJob job) => new(
        job.Id, job.LeaseOwner!, job.LeaseToken!.Value, job.LeaseGeneration);

    private static DatabaseWorkerJobCoordinator CreateCoordinator(
        DavDatabaseContext dbContext,
        int repairCapacity = 8)
    {
        return new DatabaseWorkerJobCoordinator(
            dbContext,
            new TestWorkerCapacityPolicy(repairCapacity),
            Options.Create(new WorkerLeaseOptions()));
    }

    private static async Task SetTerminalStaleStateAsync(
        DavDatabaseContext dbContext,
        Guid jobId,
        string resultJson)
    {
        dbContext.ChangeTracker.Clear();
        await dbContext.WorkerJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.ResultJson, resultJson)
            .SetProperty(job => job.StartedAt, DateTimeOffset.UtcNow)
            .SetProperty(job => job.LastHeartbeatAt, DateTimeOffset.UtcNow));
    }

    private static void AssertFreshPendingJob(WorkerJob job, DateTimeOffset availableAt, int attempts)
    {
        Assert.Equal(WorkerJob.JobStatus.Pending, job.Status);
        Assert.Equal(attempts, job.Attempts);
        Assert.Equal(availableAt, job.AvailableAt);
        Assert.Null(job.CancelRequestedAt);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseToken);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(job.LastHeartbeatAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.FailureKind);
        Assert.Null(job.ProgressJson);
        Assert.Null(job.ProgressUpdatedAt);
        Assert.Null(job.ResultJson);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.LastError);
    }

    private static async Task InvokeUpdateDownloadWorkerJobAsync
    (
        object inProgressQueueItem,
        DavDatabaseClient dbClient,
        QueueItemProcessor.ProcessingOutcome outcome
    )
    {
        var method = typeof(QueueManager).GetMethod("UpdateDownloadWorkerJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var coordinator = new DatabaseWorkerJobCoordinator(
            dbClient.Ctx,
            new TestWorkerCapacityPolicy(),
            Options.Create(new WorkerLeaseOptions()));
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null,
            [coordinator, inProgressQueueItem, outcome]));
        await task;
    }

    private static async Task<bool> InvokeUpdateDownloadWorkerJobAsync(
        object inProgressQueueItem,
        IWorkerJobCoordinator coordinator,
        QueueItemProcessor.ProcessingOutcome outcome)
    {
        var method = typeof(QueueManager).GetMethod("UpdateDownloadWorkerJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<bool>>(method.Invoke(null,
            [coordinator, inProgressQueueItem, outcome]));
        return await task;
    }

    private static async Task<bool> InvokeTryStartNextQueueItemAsync(
        QueueManager queueManager,
        CancellationToken cancellationToken)
    {
        var method = typeof(QueueManager).GetMethod(
            "TryStartNextQueueItemAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<bool>>(method.Invoke(queueManager, [cancellationToken]));
        return await task;
    }

    private static async Task<bool> InvokeReconcileDownloadWorkerJobAsync(
        QueueManager queueManager,
        object inProgressQueueItem,
        QueueItemProcessor.ProcessingOutcome outcome)
    {
        var method = typeof(QueueManager).GetMethod(
            "ReconcileDownloadWorkerJobAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<bool>>(method.Invoke(
            queueManager,
            [inProgressQueueItem, outcome]));
        return await task;
    }

    private static WorkerJob CreateLeasedWorkerJob()
    {
        return new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Download,
            TargetId = Guid.NewGuid(),
            Status = WorkerJob.JobStatus.Leased,
            LeaseOwner = "worker",
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
        };
    }

    private sealed class RecordingWorkerJobCoordinator : IWorkerJobCoordinator
    {
        public Func<WorkerJob.JobKind, string, int, DateTimeOffset, CancellationToken,
            Task<IReadOnlyList<WorkerLease>>>? LeaseHandler
        { get; init; }
        public bool CompleteResult { get; init; } = true;
        public bool FailResult { get; init; } = true;
        public int CompleteExceptionsRemaining { get; set; }
        public int ReleaseExceptionsRemaining { get; set; }
        public int FailExceptionsRemaining { get; set; }
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public WorkerLeaseIdentity LastReleasedLease { get; private set; }
        public CancellationToken LastReleaseToken { get; private set; }
        public List<DateTimeOffset> FailNextAttemptAt { get; } = [];
        public List<DateTimeOffset> FailNow { get; } = [];

        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind, string owner, int capacity, DateTimeOffset now, CancellationToken ct) =>
            LeaseHandler?.Invoke(kind, owner, capacity, now, ct)
            ?? Task.FromResult<IReadOnlyList<WorkerLease>>([]);

        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease, string progressJson, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease, string? resultJson, DateTimeOffset now, CancellationToken ct)
        {
            CompleteCalls++;
            if (CompleteExceptionsRemaining > 0)
            {
                CompleteExceptionsRemaining--;
                throw new InvalidOperationException("simulated ambiguous completion acknowledgement");
            }
            return Task.FromResult(CompleteResult);
        }

        public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct)
        {
            ReleaseCalls++;
            LastReleasedLease = lease;
            LastReleaseToken = ct;
            if (ReleaseExceptionsRemaining > 0)
            {
                ReleaseExceptionsRemaining--;
                throw new InvalidOperationException("simulated ambiguous release acknowledgement");
            }
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            WorkerLeaseIdentity lease, WorkerJob.FailureClass failureKind, string error,
            DateTimeOffset nextAttemptAt, int maxAttempts, DateTimeOffset now, CancellationToken ct)
        {
            FailCalls++;
            FailNextAttemptAt.Add(nextAttemptAt);
            FailNow.Add(now);
            if (FailExceptionsRemaining > 0)
            {
                FailExceptionsRemaining--;
                throw new InvalidOperationException("simulated ambiguous failure acknowledgement");
            }
            return Task.FromResult(FailResult);
        }

        public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class TestWorkerCapacityPolicy(int repairCapacity = 128) : IWorkerLaneCapacityPolicy
    {
        public int GetMaximum(WorkerJob.JobKind kind) =>
            kind == WorkerJob.JobKind.Repair ? repairCapacity : 128;
    }

    private sealed class BlockingDisposeStream : MemoryStream
    {
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted => _disposeStarted;

        internal void AllowDispose() => _allowDispose.TrySetResult();

        public override async ValueTask DisposeAsync()
        {
            _disposeStarted.TrySetResult();
            await _allowDispose.Task.ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ThrowAfterNextNonQueryInterceptor : DbCommandInterceptor
    {
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                return ValueTask.FromException<int>(
                    new InvalidOperationException("simulated ambiguous non-query acknowledgement"));
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingCommandInterceptor(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset()
        {
            Volatile.Write(ref _count, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CountIfMatched(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CountIfMatched(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CountIfMatched(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CountIfMatched(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CountIfMatched(DbCommand command)
        {
            if (predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }
}

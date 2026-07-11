using System.Data.Common;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
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
        Assert.Equal("second failure", secondLease.LastError);
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
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Pending, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Leased, now.AddMinutes(-1), now.AddMinutes(5)),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Leased, now.AddMinutes(-1), now.AddMinutes(-1)),
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
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Pending, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Leased, now.AddMinutes(-1), now.AddMinutes(5)),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Leased, now.AddMinutes(-1), now.AddMinutes(-1)),
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

    private static void AddInProgressQueueItem(QueueManager queueManager, Guid id, object inProgress)
    {
        var field = typeof(QueueManager).GetField("_inProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(queueManager));
        dictionary.Add(id, inProgress);
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
        public bool CompleteResult { get; init; } = true;
        public bool FailResult { get; init; } = true;
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind, string owner, int capacity, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkerLease>>([]);

        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease, string progressJson, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease, string? resultJson, DateTimeOffset now, CancellationToken ct)
        {
            CompleteCalls++;
            return Task.FromResult(CompleteResult);
        }

        public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct)
        {
            ReleaseCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            WorkerLeaseIdentity lease, WorkerJob.FailureClass failureKind, string error,
            DateTimeOffset nextAttemptAt, int maxAttempts, DateTimeOffset now, CancellationToken ct)
        {
            FailCalls++;
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

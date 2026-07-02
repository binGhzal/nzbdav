using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

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
    public async Task GetWorkerJobQueueStatsAsync_CountsReadyLeasedRetryAndQuarantinedByKind()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.WorkerJobs.AddRange(
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Pending, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Download, WorkerJob.JobStatus.Leased, now.AddMinutes(-1), now.AddMinutes(5)),
            CreateWorkerJob(WorkerJob.JobKind.Verify, WorkerJob.JobStatus.Retry, now.AddMinutes(10)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Quarantined, now.AddMinutes(-1)),
            CreateWorkerJob(WorkerJob.JobKind.Repair, WorkerJob.JobStatus.Retry, now.AddMinutes(-1))
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var stats = await dbClient.GetWorkerJobQueueStatsAsync(now);

        Assert.Equal(1, stats.Download.Ready);
        Assert.Equal(1, stats.Download.Leased);
        Assert.Equal(0, stats.Download.Retry);
        Assert.Equal(0, stats.Verify.Ready);
        Assert.Equal(1, stats.Verify.Retry);
        Assert.Equal(1, stats.Repair.Ready);
        Assert.Equal(1, stats.Repair.Retry);
        Assert.Equal(1, stats.Repair.Quarantined);
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
        DateTimeOffset? leaseExpiresAt = null
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = status,
            TargetId = Guid.NewGuid(),
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
}

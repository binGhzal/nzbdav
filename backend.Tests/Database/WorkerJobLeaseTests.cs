using System.Data.Common;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

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
        type.GetProperty("WorkerJob")!.SetValue(value, workerJob);
        return value;
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
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null,
            [inProgressQueueItem, dbClient, outcome]));
        await task;
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

        private void CountIfMatched(DbCommand command)
        {
            if (predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }
}

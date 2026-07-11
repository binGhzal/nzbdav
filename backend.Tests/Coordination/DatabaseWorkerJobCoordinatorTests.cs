using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using backend.Tests.Services;

namespace backend.Tests.Coordination;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DatabaseWorkerJobCoordinatorTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DatabaseWorkerJobCoordinatorTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StaleLeaseCannotCompleteAReLeasedJob()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Verify);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;

        var first = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));
        var second = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-b", 1, now.AddMinutes(3), CancellationToken.None));

        Assert.True(second.Identity.Generation > first.Identity.Generation);
        Assert.False(await coordinator.CompleteAsync(
            first.Identity, null, now.AddMinutes(3), CancellationToken.None));
        Assert.True(await coordinator.CompleteAsync(
            second.Identity, "{}", now.AddMinutes(3), CancellationToken.None));
        Assert.True(await coordinator.CompleteAsync(
            second.Identity, "{}", now.AddMinutes(3), CancellationToken.None));
    }

    [Fact]
    public async Task RenewExtendsOnlyTheCurrentLease()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Download);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));

        Assert.True(await coordinator.RenewAsync(
            lease.Identity, now.AddSeconds(30), CancellationToken.None));
        Assert.False(await coordinator.RenewAsync(
            lease.Identity with { Token = Guid.NewGuid() },
            now.AddSeconds(31), CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(now.AddMinutes(2).AddSeconds(30), job.LeaseExpiresAt);
        Assert.Equal(now.AddSeconds(30), job.LastHeartbeatAt);
    }

    [Fact]
    public async Task LeaseCapacityNeverExceedsConfiguredLaneMaximum()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 8; index++)
            AddJob(dbContext, WorkerJob.JobKind.Verify, now, priority: index);
        await dbContext.SaveChangesAsync();
        var coordinator = CreateCoordinator(dbContext, verifyCapacity: 3);

        var first = await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-a", 128, now, CancellationToken.None);
        var second = await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-b", 128, now, CancellationToken.None);

        Assert.Equal(3, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public async Task CancellationRejectsRenewalAndCanBeAcknowledged()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Verify);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(10), CancellationToken.None));
        Assert.False(await coordinator.RenewAsync(
            lease.Identity, now.AddSeconds(30), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            lease.Identity,
            WorkerJob.FailureClass.Cancelled,
            "cancelled by request",
            now.AddSeconds(30),
            3,
            now.AddSeconds(30),
            CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, job.Status);
        Assert.Equal(WorkerJob.FailureClass.Cancelled, job.FailureKind);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task ExpiredCancellationRequestIsTerminalizedInsteadOfReLeased()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Download);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 1, now, CancellationToken.None));
        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(10), CancellationToken.None));

        Assert.Empty(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-b", 1,
            now.AddMinutes(3), CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task ProgressReleaseAndFailureRequireTheCurrentLeaseIdentity()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Repair);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;
        var first = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-a", 1, now, CancellationToken.None));
        var stale = first.Identity with { Generation = first.Identity.Generation - 1 };

        Assert.False(await coordinator.ReportProgressAsync(
            stale, "{\"percent\":10}", now.AddSeconds(5), CancellationToken.None));
        Assert.True(await coordinator.ReportProgressAsync(
            first.Identity, "{\"percent\":20}", now.AddSeconds(6), CancellationToken.None));
        Assert.False(await coordinator.ReleaseAsync(stale, now.AddSeconds(7), CancellationToken.None));
        Assert.True(await coordinator.ReleaseAsync(first.Identity, now.AddSeconds(7), CancellationToken.None));

        var second = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Repair, "worker-b", 1, now.AddSeconds(8), CancellationToken.None));
        Assert.False(await coordinator.FailAsync(
            first.Identity, WorkerJob.FailureClass.Retryable, "stale", now.AddMinutes(1),
            3, now.AddSeconds(9), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            second.Identity, WorkerJob.FailureClass.Provider, "provider unavailable",
            now.AddMinutes(1), 3, now.AddSeconds(9), CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var job = await dbContext.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Retry, job.Status);
        Assert.Equal(WorkerJob.FailureClass.Provider, job.FailureKind);
        Assert.Equal("provider unavailable", job.LastError);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseToken);
    }

    [Fact]
    public async Task CancellationRequestAndAcknowledgementAreIdempotentForTheExactLease()
    {
        await using var dbContext = await CreateContextWithJobAsync(WorkerJob.JobKind.Verify);
        var coordinator = CreateCoordinator(dbContext);
        var now = DateTimeOffset.UtcNow;
        var lease = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));
        Assert.True(await coordinator.RequestCancellationAsync(
            lease.Identity.JobId, now.AddSeconds(2), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            lease.Identity, WorkerJob.FailureClass.Cancelled, "cancelled",
            now, 3, now.AddSeconds(3), CancellationToken.None));
        Assert.True(await coordinator.FailAsync(
            lease.Identity, WorkerJob.FailureClass.Cancelled, "cancelled",
            now, 3, now.AddSeconds(4), CancellationToken.None));
        Assert.False(await coordinator.FailAsync(
            lease.Identity with { Token = Guid.NewGuid() },
            WorkerJob.FailureClass.Cancelled, "cancelled",
            now, 3, now.AddSeconds(4), CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseFreesCapacityAndIncrementsGeneration()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        AddJob(dbContext, WorkerJob.JobKind.Download, now, priority: 10);
        AddJob(dbContext, WorkerJob.JobKind.Download, now, priority: 1);
        await dbContext.SaveChangesAsync();
        var coordinator = CreateCoordinator(dbContext, downloadCapacity: 1);

        var first = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-a", 128, now, CancellationToken.None));
        var second = Assert.Single(await coordinator.LeaseAsync(
            WorkerJob.JobKind.Download, "worker-b", 128,
            now.AddMinutes(3), CancellationToken.None));

        Assert.Equal(first.Identity.JobId, second.Identity.JobId);
        Assert.Equal(first.Identity.Generation + 1, second.Identity.Generation);
    }

    [Fact]
    public async Task ConcurrentCoordinatorsDoNotOversubscribeALane()
    {
        await using var seedContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        AddJob(seedContext, WorkerJob.JobKind.Verify, now, priority: 2);
        AddJob(seedContext, WorkerJob.JobKind.Verify, now, priority: 1);
        await seedContext.SaveChangesAsync();
        await using var firstContext = await _fixture.CreateMigratedContextAsync();
        await using var secondContext = await _fixture.CreateMigratedContextAsync();
        var firstCoordinator = CreateCoordinator(firstContext, verifyCapacity: 1);
        var secondCoordinator = CreateCoordinator(secondContext, verifyCapacity: 1);

        var results = await Task.WhenAll(
            firstCoordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None),
            secondCoordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "worker-b", 1, now, CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.Count));
        seedContext.ChangeTracker.Clear();
        Assert.Equal(1, await seedContext.WorkerJobs.CountAsync(job =>
            job.Kind == WorkerJob.JobKind.Verify && job.Status == WorkerJob.JobStatus.Leased));
    }

    private async Task<DavDatabaseContext> CreateContextWithJobAsync(WorkerJob.JobKind kind)
    {
        var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        AddJob(dbContext, kind, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();
        return dbContext;
    }

    private static DatabaseWorkerJobCoordinator CreateCoordinator(
        DavDatabaseContext dbContext,
        int verifyCapacity = 8,
        int downloadCapacity = 8)
    {
        return new DatabaseWorkerJobCoordinator(
            dbContext,
            new TestCapacityPolicy(download: downloadCapacity, verify: verifyCapacity, repair: 8),
            Options.Create(new WorkerLeaseOptions()));
    }

    private static void AddJob(
        DavDatabaseContext dbContext,
        WorkerJob.JobKind kind,
        DateTimeOffset now,
        int priority = 0)
    {
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = Guid.NewGuid(),
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now
        });
    }

    private sealed class TestCapacityPolicy(int download, int verify, int repair)
        : IWorkerLaneCapacityPolicy
    {
        public int GetMaximum(WorkerJob.JobKind kind) => kind switch
        {
            WorkerJob.JobKind.Download => download,
            WorkerJob.JobKind.Verify => verify,
            WorkerJob.JobKind.Repair => repair,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}

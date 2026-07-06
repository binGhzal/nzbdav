using System.Data.Common;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RepairRunTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public RepairRunTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StartRepairRunAsync_CreatesEntriesAndVerifyJobsWithRunPayload()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        var episode = CreateDavItem("/content/Episode.mkv");
        dbContext.Items.AddRange(movie, episode);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: now);

        var entries = await dbContext.RepairEntryHealth
            .OrderBy(x => x.Path)
            .ToListAsync();
        var jobs = await dbContext.WorkerJobs
            .OrderBy(x => x.TargetId)
            .ToListAsync();

        Assert.Equal(RepairRun.RepairRunStatus.Running, run.Status);
        Assert.Equal("queued", run.Stage);
        Assert.Equal(2, run.Total);
        Assert.All(entries, entry =>
        {
            Assert.Equal(run.Id, entry.RepairRunId);
            Assert.Equal(RepairEntryHealth.RepairEntryState.Pending, entry.State);
            Assert.Equal(now, entry.CreatedAt);
            Assert.Equal(now, entry.UpdatedAt);
        });
        Assert.Equal(
            new[] { episode.Id, movie.Id }.OrderBy(x => x),
            entries.Select(x => x.DavItemId).OrderBy(x => x));
        Assert.All(jobs, job =>
        {
            Assert.Equal(WorkerJob.JobKind.Verify, job.Kind);
            Assert.Equal(WorkerJob.JobStatus.Pending, job.Status);
            Assert.Equal(7, job.Priority);
            Assert.Equal(run.Id, DavDatabaseClient.TryGetRepairRunId(job.PayloadJson));
        });
    }

    [Fact]
    public async Task CancelRepairRunAsync_CancelsPendingEntriesAndJobs()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: now);

        await dbClient.CancelRepairRunAsync(run.Id, now: now.AddMinutes(5));
        dbContext.ChangeTracker.Clear();

        var reloadedRun = await dbContext.RepairRuns.SingleAsync();
        var entry = await dbContext.RepairEntryHealth.SingleAsync();
        var job = await dbContext.WorkerJobs.SingleAsync();

        Assert.Equal(RepairRun.RepairRunStatus.Cancelled, reloadedRun.Status);
        Assert.Equal("cancelled", reloadedRun.Stage);
        Assert.Equal(now.AddMinutes(5), reloadedRun.CancelledAt);
        Assert.Equal(RepairEntryHealth.RepairEntryState.Cancelled, entry.State);
        Assert.Equal(WorkerJob.JobStatus.Cancelled, job.Status);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task StartRepairRunAsync_RejectsSecondActiveRunAndKeepsExistingJobPayload()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var firstRun = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);
        var firstPayload = await dbContext.WorkerJobs
            .Select(x => x.PayloadJson)
            .SingleAsync();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            dbClient.StartRepairRunAsync(priority: 10, now: DateTimeOffset.UtcNow.AddMinutes(1)));
        var payloadAfterRejectedStart = await dbContext.WorkerJobs
            .Select(x => x.PayloadJson)
            .SingleAsync();

        Assert.Contains("already active", error.Message);
        Assert.Equal(firstRun.Id, DavDatabaseClient.TryGetRepairRunId(firstPayload));
        Assert.Equal(firstPayload, payloadAfterRejectedStart);
    }

    [Fact]
    public async Task StartRepairRunAsync_RequeuesExistingQuarantinedVerifyJob()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        dbContext.WorkerJobs.Add(CreateWorkerJob(
            WorkerJob.JobKind.Verify,
            WorkerJob.JobStatus.Quarantined,
            movie.Id,
            DateTimeOffset.UtcNow.AddMinutes(-10)));
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 9, now: DateTimeOffset.UtcNow);
        var job = await dbContext.WorkerJobs.SingleAsync();

        Assert.Equal(WorkerJob.JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(9, job.Priority);
        Assert.Null(job.LastError);
        Assert.Equal(run.Id, DavDatabaseClient.TryGetRepairRunId(job.PayloadJson));
    }

    [Fact]
    public async Task GetActiveRepairRunAsync_ReturnsNullWhenRefreshCompletesRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);
        await dbContext.RepairEntryHealth.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.State, RepairEntryHealth.RepairEntryState.Healthy));
        await dbContext.WorkerJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, WorkerJob.JobStatus.Completed)
            .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));

        var activeRun = await dbClient.GetActiveRepairRunAsync();
        dbContext.ChangeTracker.Clear();
        var reloadedRun = await dbContext.RepairRuns.SingleAsync(x => x.Id == run.Id);

        Assert.Null(activeRun);
        Assert.Equal(RepairRun.RepairRunStatus.Completed, reloadedRun.Status);
    }

    [Fact]
    public async Task GetRepairRunStatusAsync_RefreshesActiveLatestRunOnlyOnce()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("RepairRuns", StringComparison.OrdinalIgnoreCase)
            && commandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: now);
        interceptor.Reset();

        var status = await dbClient.GetRepairRunStatusAsync(now.AddMinutes(2));

        Assert.NotNull(status.ActiveRun);
        Assert.NotNull(status.LastRun);
        Assert.Equal(run.Id, status.ActiveRun.Id);
        Assert.Equal(run.Id, status.LastRun.Id);
        Assert.Equal(RepairRun.RepairRunStatus.Running, status.LastRun.Status);
        Assert.Equal(0, status.BrokenFiles);
        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    public async Task RefreshRepairRunSummaryAsync_ClosesPendingEntryWhenCompletedJobLostItem()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);
        await dbContext.Items
            .Where(x => x.Id == movie.Id)
            .ExecuteDeleteAsync();
        await dbContext.WorkerJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, WorkerJob.JobStatus.Completed)
            .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));

        var refreshed = await dbClient.RefreshRepairRunSummaryAsync(run);
        var entry = await dbContext.RepairEntryHealth.SingleAsync();

        Assert.Equal(RepairEntryHealth.RepairEntryState.Deleted, entry.State);
        Assert.Equal(RepairRun.RepairRunStatus.Completed, refreshed.Status);
        Assert.Equal(1, refreshed.Deleted);
    }

    [Fact]
    public async Task MarkRepairVerificationFailureAsync_MarksProviderErrorAndAllowsRunToFinish()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);
        await dbContext.WorkerJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, WorkerJob.JobStatus.Quarantined)
            .SetProperty(x => x.LastError, "provider timed out"));

        await dbClient.MarkRepairVerificationFailureAsync(
            run.Id,
            movie.Id,
            "provider timed out",
            quarantined: true);
        await dbContext.SaveChangesAsync();
        var refreshed = await dbClient.RefreshRepairRunSummaryAsync(run);
        var entry = await dbContext.RepairEntryHealth.SingleAsync();

        Assert.Equal(RepairEntryHealth.RepairEntryState.ProviderError, entry.State);
        Assert.Contains("quarantined", entry.Message);
        Assert.Equal(RepairRun.RepairRunStatus.Completed, refreshed.Status);
        Assert.Equal(1, refreshed.ProviderErrors);
    }

    [Fact]
    public async Task ClearRepairRunsAsync_RejectsActiveRunAndDeletesSettledPayloadJobs()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<BadHttpRequestException>(() => dbClient.ClearRepairRunsAsync());

        await dbContext.RepairEntryHealth.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.State, RepairEntryHealth.RepairEntryState.Healthy));
        await dbContext.WorkerJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, WorkerJob.JobStatus.Completed)
            .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
        await dbClient.RefreshRepairRunSummaryAsync(run);

        await dbClient.ClearRepairRunsAsync();

        Assert.Empty(await dbContext.RepairRuns.ToListAsync());
        Assert.Empty(await dbContext.RepairEntryHealth.ToListAsync());
        Assert.Empty(await dbContext.RepairBrokenFiles.ToListAsync());
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
    }

    [Fact]
    public async Task UpsertRepairEntryAsync_ClearsBrokenFileRowsWhenFileBecomesHealthy()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(movie);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: DateTimeOffset.UtcNow);
        await dbClient.UpsertRepairBrokenFileAsync(run.Id, movie.Id, movie.Path, "missing articles");
        await dbContext.SaveChangesAsync();

        await dbClient.UpsertRepairEntryAsync(
            run.Id,
            movie.Id,
            movie.Path,
            RepairEntryHealth.RepairEntryState.Healthy,
            "Recovered");
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var brokenFile = await dbContext.RepairBrokenFiles.SingleAsync();

        Assert.True(brokenFile.Cleared);
    }

    [Fact]
    public async Task RefreshRepairRunSummaryAsync_CompletesWhenEntriesAndJobsAreSettled()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var movie = CreateDavItem("/content/Movie.mkv");
        var episode = CreateDavItem("/content/Episode.mkv");
        dbContext.Items.AddRange(movie, episode);
        await dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var dbClient = new DavDatabaseClient(dbContext);
        var run = await dbClient.StartRepairRunAsync(priority: 7, now: now);

        await dbContext.RepairEntryHealth
            .Where(x => x.DavItemId == movie.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.State,
                RepairEntryHealth.RepairEntryState.Healthy));
        await dbContext.RepairEntryHealth
            .Where(x => x.DavItemId == episode.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                x => x.State,
                RepairEntryHealth.RepairEntryState.ProviderError));
        await dbContext.WorkerJobs.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, WorkerJob.JobStatus.Completed)
            .SetProperty(x => x.CompletedAt, now.AddMinutes(1)));

        var refreshed = await dbClient.RefreshRepairRunSummaryAsync(run, now: now.AddMinutes(2));

        Assert.Equal(RepairRun.RepairRunStatus.Completed, refreshed.Status);
        Assert.Equal("completed", refreshed.Stage);
        Assert.Equal(2, refreshed.Total);
        Assert.Equal(2, refreshed.Checked);
        Assert.Equal(1, refreshed.ProviderErrors);
        Assert.Equal(now.AddMinutes(2), refreshed.CompletedAt);
    }

    private static DavItem CreateDavItem(string path)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = Path.GetFileName(path),
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = path,
        };
    }

    private static WorkerJob CreateWorkerJob
    (
        WorkerJob.JobKind kind,
        WorkerJob.JobStatus status,
        Guid targetId,
        DateTimeOffset availableAt
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = status,
            TargetId = targetId,
            Priority = 0,
            Attempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = availableAt,
            LastError = "previous failure"
        };
    }

    private sealed class CountingCommandInterceptor(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset()
        {
            Volatile.Write(ref _count, 0);
        }

        public override InterceptionResult<int> NonQueryExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            CountIfMatched(command);
            return base.NonQueryExecuting(command, eventData, result);
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

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            CountIfMatched(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
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

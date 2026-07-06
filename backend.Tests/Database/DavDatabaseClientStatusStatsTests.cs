using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

public sealed class DavDatabaseClientStatusStatsTests
{
    [Fact]
    public async Task GetHealthCheckQueueSnapshotAsync_ProjectsBoundedRowsWithoutTrackingDavItems()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.Items.AddRange(
            CreateDavItem("/content/Movie.A.mkv", now.AddDays(-5), now.AddDays(-1), now.AddMinutes(-5)),
            CreateDavItem("/content/Movie.B.mkv", now.AddDays(-4), now.AddDays(-2), null),
            CreateDavItem("/content/Movie.C.mkv", now.AddDays(-3), now.AddDays(-3), null),
            new DavItem
            {
                Id = Guid.NewGuid(),
                IdPrefix = Guid.NewGuid().ToString("N")[..DavItem.IdPrefixLength],
                CreatedAt = DateTime.UtcNow,
                ParentId = DavItem.ContentFolder.Id,
                Name = "Imported.mkv",
                FileSize = 1024,
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.NzbFile,
                Path = "/content/Imported.mkv",
                HistoryItemId = Guid.NewGuid()
            });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var snapshot = await new DavDatabaseClient(dbContext)
            .GetHealthCheckQueueSnapshotAsync(pageSize: 2);

        Assert.Equal(2, snapshot.UncheckedCount);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.NotEqual(Guid.Empty, item.Id));
        Assert.Contains(snapshot.Items, item => item.Path == "/content/Movie.A.mkv");
        Assert.Contains(snapshot.Items, item => item.NextHealthCheck == null);
        Assert.Empty(dbContext.ChangeTracker.Entries<DavItem>());
    }

    [Fact]
    public async Task GetArrIntegrationStatsAsync_UsesOneQueryPerArrStatsTable()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("ArrDownloadCorrelations", StringComparison.OrdinalIgnoreCase)
            || commandText.Contains("QueuePriorityHints", StringComparison.OrdinalIgnoreCase)
            || commandText.Contains("ArrSearchNudgeCommands", StringComparison.OrdinalIgnoreCase)
            || commandText.Contains("ArrDownloadLifecycleEvents", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var activeQueueItemId = Guid.NewGuid();
        var staleQueueItemId = Guid.NewGuid();
        dbContext.QueueItems.AddRange(
            CreateQueueItem(activeQueueItemId, "Active priority"),
            CreateQueueItem(staleQueueItemId, "Stale priority"));
        dbContext.ArrDownloadCorrelations.AddRange(
            new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = "sonarr:http://sonarr:8989",
                InstanceHost = "http://sonarr:8989",
                IsDuplicate = true,
                LastSeenAt = now.AddMinutes(-30),
                CreatedAt = now.AddMinutes(-40),
                UpdatedAt = now.AddMinutes(-30)
            },
            new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                ArrApp = "radarr",
                InstanceKey = "radarr:http://radarr:7878",
                InstanceHost = "http://radarr:7878",
                LastSeenAt = now,
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now
            });
        dbContext.QueuePriorityHints.AddRange(
            new QueuePriorityHint
            {
                QueueItemId = activeQueueItemId,
                Score = 500,
                EffectivePriority = QueueItem.PriorityOption.High,
                ApplyToScheduling = true,
                ComputedAt = now,
                ExpiresAt = now.AddMinutes(10)
            },
            new QueuePriorityHint
            {
                QueueItemId = staleQueueItemId,
                Score = 100,
                EffectivePriority = QueueItem.PriorityOption.Low,
                ApplyToScheduling = true,
                ComputedAt = now.AddHours(-2),
                ExpiresAt = now.AddMinutes(-10)
            });
        dbContext.ArrSearchNudgeCommands.AddRange(
            CreateNudge("planned", now.AddMinutes(-5)),
            CreateNudge("pending_apply", now.AddMinutes(-4)),
            CreateNudge("executing", now.AddMinutes(-3)),
            CreateNudge("executed", now.AddMinutes(-2)),
            CreateNudge("failed", now.AddMinutes(-1)));
        dbContext.ArrDownloadLifecycleEvents.AddRange(
            CreateLifecycle("Downloading", now.AddMinutes(-5)),
            CreateLifecycle("Downloading", now.AddMinutes(-4)),
            CreateLifecycle("Completed", now.AddMinutes(-3)),
            CreateLifecycle("Old", now.AddHours(-25)));
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var stats = await new DavDatabaseClient(dbContext).GetArrIntegrationStatsAsync(now);

        Assert.Equal(4, interceptor.Count);
        Assert.Equal(2, stats.TotalCorrelations);
        Assert.Equal(1, stats.StaleCorrelations);
        Assert.Equal(1, stats.DuplicateCorrelations);
        Assert.Equal(1, stats.ActivePriorityHints);
        Assert.Equal(1, stats.StalePriorityHints);
        Assert.Equal(3, stats.PlannedSearchNudges);
        Assert.Equal(1, stats.ExecutedSearchNudges);
        Assert.Equal(1, stats.FailedSearchNudges);
        Assert.Equal(now.AddMinutes(-1), stats.LastSearchNudgeAt);
        Assert.Equal(2, stats.LifecycleStates.Single(x => x.State == "Downloading").Count);
        Assert.Equal(1, stats.LifecycleStates.Single(x => x.State == "Completed").Count);
        Assert.DoesNotContain(stats.LifecycleStates, x => x.State == "Old");
    }

    [Fact]
    public async Task GetHealthWorkerQueueStatsAsync_CountsOnlyLatestActionNeededResultsPerFile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var recoveredItemId = Guid.NewGuid();
        var brokenItemId = Guid.NewGuid();
        dbContext.HealthCheckResults.AddRange(
            CreateHealthCheckResult(
                recoveredItemId,
                now.AddMinutes(-20),
                HealthCheckResult.RepairAction.ActionNeeded),
            CreateHealthCheckResult(
                recoveredItemId,
                now.AddMinutes(-5),
                HealthCheckResult.RepairAction.None),
            CreateHealthCheckResult(
                brokenItemId,
                now.AddMinutes(-10),
                HealthCheckResult.RepairAction.ActionNeeded));
        await dbContext.SaveChangesAsync();

        var stats = await new DavDatabaseClient(dbContext).GetHealthWorkerQueueStatsAsync(now);

        Assert.Equal(1, stats.RepairActionNeeded);
    }

    private static ArrSearchNudgeCommand CreateNudge(string status, DateTimeOffset createdAt)
    {
        return new ArrSearchNudgeCommand
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = "sonarr:http://sonarr:8989",
            InstanceHost = "http://sonarr:8989",
            CommandName = "EpisodeSearch",
            Status = status,
            CooldownKey = status,
            CreatedAt = createdAt,
            NextAllowedAt = createdAt
        };
    }

    private static QueueItem CreateQueueItem(Guid id, string jobName)
    {
        return new QueueItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = null
        };
    }

    private static DavItem CreateDavItem
    (
        string path,
        DateTimeOffset? releaseDate,
        DateTimeOffset? lastHealthCheck,
        DateTimeOffset? nextHealthCheck
    )
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
            ReleaseDate = releaseDate,
            LastHealthCheck = lastHealthCheck,
            NextHealthCheck = nextHealthCheck
        };
    }

    private static HealthCheckResult CreateHealthCheckResult
    (
        Guid davItemId,
        DateTimeOffset createdAt,
        HealthCheckResult.RepairAction repairAction
    )
    {
        return new HealthCheckResult
        {
            Id = Guid.NewGuid(),
            DavItemId = davItemId,
            Path = $"/content/{davItemId:N}.mkv",
            CreatedAt = createdAt,
            Result = repairAction == HealthCheckResult.RepairAction.None
                ? HealthCheckResult.HealthResult.Healthy
                : HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = repairAction
        };
    }

    private static ArrDownloadLifecycleEvent CreateLifecycle(string state, DateTimeOffset createdAt)
    {
        return new ArrDownloadLifecycleEvent
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = "sonarr:http://sonarr:8989",
            State = state,
            CreatedAt = createdAt
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
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Database;

public sealed class DavDatabaseClientProviderSqlTests
{
    [Fact]
    public async Task GetRecursiveSize_UsesProviderNeutralSqlOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "DavItems" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ParentId" TEXT NULL,
                "FileSize" INTEGER NULL
            );
            """);

        var parentId = Guid.NewGuid();
        await SeedRecursiveTreeAsync(context, parentId);

        Assert.Equal(60, await new DavDatabaseClient(context).GetRecursiveSize(parentId));
    }

    [PostgreSqlFact]
    public async Task GetRecursiveSize_UsesProviderNeutralSqlOnPostgreSql()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("recursive_size");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var parentId = Guid.NewGuid();
        await SeedRecursiveTreeAsync(context, parentId, includeRequiredDavColumns: true);

        Assert.Equal(60, await new DavDatabaseClient(context).GetRecursiveSize(parentId));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlPauseUntilStatsUseAnInclusiveUpperBoundAtWholeMicroseconds()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("pause_stats_bound");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var capture = new CommandCaptureInterceptor();
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions(capture));
        var rawNow = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero).AddTicks(9);
        var localRawNow = rawNow.LocalDateTime;
        var localFloor = DateTime.SpecifyKind(
            new DateTime(localRawNow.Ticks - localRawNow.Ticks % 10),
            DateTimeKind.Unspecified);
        var nextMicrosecond = localFloor.AddTicks(10);
        var queueItemId = Guid.NewGuid();
        context.QueueItems.Add(new QueueItem
        {
            Id = queueItemId,
            CreatedAt = localFloor,
            FileName = $"{queueItemId:N}.nzb",
            JobName = queueItemId.ToString("N"),
            NzbFileSize = 1,
            TotalSegmentBytes = 1,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = nextMicrosecond
        });
        await context.SaveChangesAsync();
        capture.Clear();
        var client = new DavDatabaseClient(context);

        var beforeBoundary = await client.GetWorkerJobQueueStatsAsync(rawNow);

        Assert.Equal(0, beforeBoundary.Download.Ready);
        var command = Assert.Single(capture.Commands, item =>
            item.CommandText.Contains("QueueItems", StringComparison.Ordinal)
            && item.CommandText.Contains("PauseUntil", StringComparison.Ordinal));
        Assert.Contains(command.Parameters.Values.OfType<DateTime>(), parameter =>
            parameter == localFloor && parameter.Kind == DateTimeKind.Unspecified);

        var atBoundary = await client.GetWorkerJobQueueStatsAsync(rawNow.AddTicks(1));
        Assert.Equal(1, atBoundary.Download.Ready);
    }

    [PostgreSqlFact]
    public async Task PostgreSqlDownloadStatsIgnoreOrphanWorkerAndCountQueueItemWithoutJob()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("download_orphan_stats");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var now = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero);
        var eligibleQueueItemId = Guid.NewGuid();
        context.QueueItems.Add(new QueueItem
        {
            Id = eligibleQueueItemId,
            CreatedAt = DateTime.SpecifyKind(now.LocalDateTime, DateTimeKind.Unspecified),
            FileName = $"{eligibleQueueItemId:N}.nzb",
            JobName = eligibleQueueItemId.ToString("N"),
            NzbFileSize = 1,
            TotalSegmentBytes = 1,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        });
        context.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Download,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = Guid.NewGuid(),
            Priority = 100,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-1),
            AvailableAt = now.AddMinutes(-1)
        });
        await context.SaveChangesAsync();

        var stats = await new DavDatabaseClient(context).GetWorkerJobQueueStatsAsync(now);
        var leases = await new NzbWebDAV.Coordination.DatabaseWorkerJobCoordinator(
            context,
            new FixedDownloadCapacityPolicy(),
            Microsoft.Extensions.Options.Options.Create(new NzbWebDAV.Coordination.WorkerLeaseOptions()))
            .LeaseAsync(
                WorkerJob.JobKind.Download,
                "download-worker",
                1,
                now,
                CancellationToken.None);

        Assert.Equal(1, stats.Download.Ready);
        Assert.Empty(leases);
        var orphan = await context.WorkerJobs.AsNoTracking().SingleAsync();
        Assert.Equal(WorkerJob.JobStatus.Cancelled, orphan.Status);
        Assert.Equal(WorkerJob.FailureClass.Cancelled, orphan.FailureKind);
        Assert.NotNull(orphan.CompletedAt);
        Assert.Contains("target", orphan.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, orphan.Attempts);
    }

    private sealed class FixedDownloadCapacityPolicy : NzbWebDAV.Coordination.IWorkerLaneCapacityPolicy
    {
        public int GetMaximum(WorkerJob.JobKind kind) => 1;
    }

    private static async Task SeedRecursiveTreeAsync(
        DavDatabaseContext context,
        Guid parentId,
        bool includeRequiredDavColumns = false)
    {
        var firstChildId = Guid.NewGuid();
        var secondChildId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();
        var unrelatedParentId = Guid.NewGuid();
        if (includeRequiredDavColumns)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DavItems"
                    ("Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "FileSize", "Type", "SubType", "Path")
                VALUES
                    ({parentId}, {"root0"}, timestamp '2026-07-12 12:00:00', {Guid.Parse("00000000-0000-0000-0000-000000000002")},
                     {"recursive"}, {(long?)null}, {1}, {101}, {"/content/recursive"}),
                    ({unrelatedParentId}, {"other"}, timestamp '2026-07-12 12:00:00', {Guid.Parse("00000000-0000-0000-0000-000000000002")},
                     {"unrelated"}, {(long?)null}, {1}, {101}, {"/content/unrelated"}),
                    ({firstChildId}, {"file1"}, timestamp '2026-07-12 12:00:00', {parentId}, {"one.bin"}, {10L}, {2}, {201},
                     {"/content/recursive/one.bin"}),
                    ({secondChildId}, {"file2"}, timestamp '2026-07-12 12:00:00', {parentId}, {"two.bin"}, {20L}, {2}, {201},
                     {"/content/recursive/two.bin"}),
                    ({grandchildId}, {"file3"}, timestamp '2026-07-12 12:00:00', {secondChildId}, {"three.bin"}, {30L}, {2}, {201},
                     {"/content/recursive/two.bin/three.bin"}),
                    ({Guid.NewGuid()}, {"file4"}, timestamp '2026-07-12 12:00:00', {unrelatedParentId}, {"other.bin"}, {100L}, {2}, {201},
                     {"/content/unrelated/other.bin"});
                """);
            return;
        }

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DavItems" ("Id", "ParentId", "FileSize") VALUES
                ({firstChildId}, {parentId}, {10L}),
                ({secondChildId}, {parentId}, {20L}),
                ({grandchildId}, {secondChildId}, {30L}),
                ({Guid.NewGuid()}, {unrelatedParentId}, {100L});
            """);
    }

}

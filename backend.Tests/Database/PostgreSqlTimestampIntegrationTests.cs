using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Database;

public sealed class PostgreSqlTimestampIntegrationTests
{
    [PostgreSqlFact]
    public async Task MicrosecondAlignedLocalWallValuesRoundTripExactlyAsUnspecified()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_exact");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var createdAt = DateTime.SpecifyKind(
            new DateTime(2026, 7, 12, 13, 45, 30).AddTicks(1_234_560),
            DateTimeKind.Unspecified);
        var pauseUntil = createdAt.AddMinutes(1);
        var id = Guid.NewGuid();

        await using (var context = CreateContext(schema.ConnectionString))
        {
            context.QueueItems.Add(CreateQueueItem(id, createdAt, pauseUntil));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(schema.ConnectionString))
        {
            var actual = await context.QueueItems.SingleAsync(item => item.Id == id);
            Assert.Equal(createdAt, actual.CreatedAt);
            Assert.Equal(DateTimeKind.Unspecified, actual.CreatedAt.Kind);
            Assert.Equal(pauseUntil, actual.PauseUntil);
            Assert.Equal(DateTimeKind.Unspecified, actual.PauseUntil!.Value.Kind);
        }

        Assert.Equal(DateTime.MinValue, await ReadRootCreatedAtAsync(schema.ConnectionString));
        Assert.True(await schema.ScalarAsync<bool>(
            """
            SELECT isfinite("CreatedAt")
            FROM "DavItems"
            WHERE "Id" = '00000000-0000-0000-0000-000000000000'
            """));
    }

    [PostgreSqlFact]
    public async Task SubMicrosecondLocalWallTicksAreRejectedBeforeMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_precision");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var microsecondAligned = DateTime.SpecifyKind(
            new DateTime(2026, 7, 12, 13, 45, 30).AddTicks(1_234_560),
            DateTimeKind.Unspecified);
        var subMicrosecond = microsecondAligned.AddTicks(7);
        var id = Guid.NewGuid();

        await using var context = CreateContext(schema.ConnectionString);
        context.QueueItems.Add(CreateQueueItem(id, subMicrosecond, pauseUntil: null));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var contractError = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(
            "QueueItem.CreatedAt must use DateTimeKind.Unspecified and whole PostgreSQL microseconds (Ticks % 10 == 0).",
            contractError.Message);
        Assert.Equal(0L, await schema.ScalarAsync<long>("SELECT count(*) FROM \"QueueItems\""));
    }

    [PostgreSqlFact]
    public async Task UtcDateTimeCannotBeWrittenToALocalWallColumn()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_utc");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var context = CreateContext(schema.ConnectionString);
        context.QueueItems.Add(CreateQueueItem(
            Guid.NewGuid(),
            new DateTime(2026, 7, 12, 13, 45, 30, DateTimeKind.Utc),
            pauseUntil: null));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var contractError = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(
            "QueueItem.CreatedAt must use DateTimeKind.Unspecified and whole PostgreSQL microseconds (Ticks % 10 == 0).",
            contractError.Message);
        Assert.Equal(0L, await schema.ScalarAsync<long>("SELECT count(*) FROM \"QueueItems\""));
    }

    [PostgreSqlFact]
    public async Task EveryLocalWallFieldRejectsSubMicrosecondValuesBeforeMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_all_fields");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var exact = DateTime.SpecifyKind(
            new DateTime(2026, 7, 12, 13, 45, 30).AddTicks(1_234_560),
            DateTimeKind.Unspecified);
        var invalid = exact.AddTicks(1);

        await AssertRejectedAsync(
            schema.ConnectionString,
            new DavItem
            {
                Id = Guid.NewGuid(),
                IdPrefix = "bad00",
                CreatedAt = invalid,
                ParentId = DavItem.ContentFolder.Id,
                Name = "invalid-created-at.bin",
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.NzbFile,
                Path = "/content/invalid-created-at.bin"
            },
            "DavItem.CreatedAt");
        await AssertRejectedAsync(
            schema.ConnectionString,
            new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = invalid,
                FileName = "invalid.nzb",
                JobName = "invalid",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1,
                DownloadTimeSeconds = 1
            },
            "HistoryItem.CreatedAt");
        await AssertRejectedAsync(
            schema.ConnectionString,
            CreateQueueItem(Guid.NewGuid(), invalid, pauseUntil: null),
            "QueueItem.CreatedAt");
        await AssertRejectedAsync(
            schema.ConnectionString,
            CreateQueueItem(Guid.NewGuid(), exact, invalid),
            "QueueItem.PauseUntil");

        Assert.Equal(0L, await schema.ScalarAsync<long>(
            "SELECT count(*) FROM \"DavItems\" WHERE \"Id\" NOT IN " +
            "('00000000-0000-0000-0000-000000000000','00000000-0000-0000-0000-000000000001'," +
            "'00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000003'," +
            "'00000000-0000-0000-0000-000000000004')"));
        Assert.Equal(0L, await schema.ScalarAsync<long>("SELECT count(*) FROM \"HistoryItems\""));
        Assert.Equal(0L, await schema.ScalarAsync<long>("SELECT count(*) FROM \"QueueItems\""));
    }

    [PostgreSqlFact]
    public async Task ExecuteUpdateCannotBypassTheLocalWallTimestampContract()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_execute_update");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var exact = DateTime.SpecifyKind(
            new DateTime(2026, 7, 12, 13, 45, 30).AddTicks(1_234_560),
            DateTimeKind.Unspecified);
        var id = Guid.NewGuid();
        await using (var setup = CreateContext(schema.ConnectionString))
        {
            setup.QueueItems.Add(CreateQueueItem(id, exact, exact.AddMinutes(1)));
            await setup.SaveChangesAsync();
        }

        await using var context = CreateContext(schema.ConnectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.QueueItems
                .Where(item => item.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.PauseUntil, exact.AddTicks(1))));

        Assert.Equal(
            "QueueItem.PauseUntil must use DateTimeKind.Unspecified and whole PostgreSQL microseconds (Ticks % 10 == 0).",
            error.Message);
        await using var assertionContext = CreateContext(schema.ConnectionString);
        var actual = await assertionContext.QueueItems.SingleAsync(item => item.Id == id);
        Assert.Equal(exact.AddMinutes(1), actual.PauseUntil);
    }

    [PostgreSqlFact]
    public async Task InfinityTimestampIsRejectedWhenReadingWhileYearOneRemainsFinite()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_infinity");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var connection = await schema.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT '-infinity'::timestamp without time zone";

        var error = await Assert.ThrowsAsync<InvalidCastException>(async () =>
            _ = (DateTime)(await command.ExecuteScalarAsync())!);

        Assert.Contains("infinity", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DateTime.MinValue, await ReadRootCreatedAtAsync(schema.ConnectionString));
    }

    [PostgreSqlFact]
    public async Task InclusiveHistoryLowerBoundCeilsToTheNextPostgreSqlMicrosecond()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("timestamp_lower_bound");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var capture = new CommandCaptureInterceptor();
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions(capture));
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(7),
            TimeZoneInfo.Local);
        var rawCutoff = timeProvider.GetLocalNow().DateTime.AddDays(-14);
        var floor = DateTime.SpecifyKind(
            new DateTime(rawCutoff.Ticks - rawCutoff.Ticks % 10),
            DateTimeKind.Unspecified);
        var ceiling = floor.AddTicks(10);
        var below = CreateHistoryItem("below", floor);
        var included = CreateHistoryItem("included", ceiling);
        context.HistoryItems.AddRange(below, included);
        await context.SaveChangesAsync();
        capture.Clear();

        var result = await ArrCorrelationService.GetRecentHistoryAsync(
            context,
            timeProvider,
            CancellationToken.None);

        Assert.Collection(result, item => Assert.Equal(included.Id, item.Id));
        var command = Assert.Single(capture.Commands, item =>
            item.CommandText.Contains("HistoryItems", StringComparison.Ordinal)
            && item.CommandText.Contains("CreatedAt", StringComparison.Ordinal));
        var cutoff = Assert.Single(command.Parameters.Values.OfType<DateTime>());
        Assert.Equal(ceiling, cutoff);
        Assert.Equal(DateTimeKind.Unspecified, cutoff.Kind);
    }

    private static PostgreSqlDavDatabaseContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(DatabaseMigrationPolicy.PostgreSqlHistoryTableName))
            .Options;
        return new PostgreSqlDavDatabaseContext(options);
    }

    private static QueueItem CreateQueueItem(
        Guid id,
        DateTime createdAt,
        DateTime? pauseUntil) => new()
    {
        Id = id,
        CreatedAt = createdAt,
        FileName = $"{id:N}.nzb",
        JobName = id.ToString("N"),
        NzbFileSize = 1,
        TotalSegmentBytes = 1,
        Category = "movies",
        Priority = QueueItem.PriorityOption.Normal,
        PostProcessing = QueueItem.PostProcessingOption.None,
        PauseUntil = pauseUntil
    };

    private static HistoryItem CreateHistoryItem(string name, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        FileName = $"{name}.nzb",
        JobName = name,
        Category = "movies",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1,
        DownloadTimeSeconds = 1
    };

    private static async Task AssertRejectedAsync(
        string connectionString,
        object entity,
        string field)
    {
        await using var context = CreateContext(connectionString);
        context.Add(entity);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var contractError = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(
            $"{field} must use DateTimeKind.Unspecified and whole PostgreSQL microseconds (Ticks % 10 == 0).",
            contractError.Message);
    }

    private static async Task<DateTime> ReadRootCreatedAtAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        context.ChangeTracker.Clear();
        return (await context.Items.SingleAsync(item => item.Id == DavItem.Root.Id)).CreatedAt;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }
}

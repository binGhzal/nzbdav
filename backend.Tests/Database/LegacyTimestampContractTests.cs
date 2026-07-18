using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

public sealed class LegacyTimestampContractTests
{
    public static TheoryData<Type, string, Type, bool> LegacyProperties => new()
    {
        { typeof(DavItem), nameof(DavItem.CreatedAt), typeof(DateTime), false },
        { typeof(HistoryItem), nameof(HistoryItem.CreatedAt), typeof(DateTime), false },
        { typeof(QueueItem), nameof(QueueItem.CreatedAt), typeof(DateTime), false },
        { typeof(QueueItem), nameof(QueueItem.PauseUntil), typeof(DateTime?), true }
    };

    [Theory]
    [MemberData(nameof(LegacyProperties))]
    public void LegacyPropertiesUseExactClrNullabilityAndNativeSqliteTextMapping(
        Type entityType,
        string propertyName,
        Type expectedClrType,
        bool expectedNullable)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new DavDatabaseContext(options);

        var property = dbContext.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        Assert.Equal(expectedClrType, property.ClrType);
        Assert.Equal(expectedNullable, property.IsNullable);
        Assert.Equal("TEXT", property.GetRelationalTypeMapping().StoreType);
        Assert.Null(property.GetProviderClrType());
        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public async Task SqliteRoundTripPreservesExactUnspecifiedLocalWallValuesWithoutUtcConversion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var davCreatedAt = LocalWall(2026, 7, 12, 9, 10, 11, 1234567);
        var historyCreatedAt = LocalWall(2026, 7, 12, 10, 11, 12, 2345678);
        var queueCreatedAt = LocalWall(2026, 7, 12, 11, 12, 13, 3456789);
        var pauseUntil = LocalWall(2026, 7, 12, 12, 13, 14, 4567890);
        var davId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var queueId = Guid.NewGuid();
        dbContext.Items.Add(new DavItem
        {
            Id = davId,
            IdPrefix = davId.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = davCreatedAt,
            ParentId = DavItem.ContentFolder.Id,
            Name = "local-wall-round-trip.mkv",
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = "/content/local-wall-round-trip.mkv"
        });
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = historyCreatedAt,
            FileName = "local-wall-history.nzb",
            JobName = "local-wall-history",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1,
            DownloadTimeSeconds = 1
        });
        dbContext.QueueItems.Add(new QueueItem
        {
            Id = queueId,
            CreatedAt = queueCreatedAt,
            FileName = "local-wall-queue.nzb",
            JobName = "local-wall-queue",
            Category = "tv",
            NzbFileSize = 1,
            TotalSegmentBytes = 1,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = pauseUntil
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var dav = await dbContext.Items.SingleAsync(x => x.Id == davId);
        var history = await dbContext.HistoryItems.SingleAsync(x => x.Id == historyId);
        var queue = await dbContext.QueueItems.SingleAsync(x => x.Id == queueId);
        AssertExactLocalWall(davCreatedAt, dav.CreatedAt);
        AssertExactLocalWall(historyCreatedAt, history.CreatedAt);
        AssertExactLocalWall(queueCreatedAt, queue.CreatedAt);
        AssertExactLocalWall(pauseUntil, queue.PauseUntil!.Value);

        await AssertStoredAsOffsetFreeTextAsync(connection, "DavItems", "CreatedAt", davId);
        await AssertStoredAsOffsetFreeTextAsync(connection, "HistoryItems", "CreatedAt", historyId);
        await AssertStoredAsOffsetFreeTextAsync(connection, "QueueItems", "CreatedAt", queueId);
        await AssertStoredAsOffsetFreeTextAsync(connection, "QueueItems", "PauseUntil", queueId);
    }

    [Fact]
    public void DavItemFactoryUsesInjectedDeploymentLocalWallTime()
    {
        var localZone = FixedLocalZone();
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero),
            localZone);

        var item = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "fixed-local-wall",
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null,
            timeProvider: timeProvider);

        AssertExactLocalWall(new DateTime(2026, 7, 12, 5, 2, 3, DateTimeKind.Unspecified), item.CreatedAt);
    }

    private static DateTime LocalWall(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        int fractionalTicks) =>
        new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(fractionalTicks);

    private static void AssertExactLocalWall(DateTime expected, DateTime actual)
    {
        Assert.Equal(expected.Ticks, actual.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
    }

    private static async Task AssertStoredAsOffsetFreeTextAsync(
        SqliteConnection connection,
        string table,
        string column,
        Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT typeof(\"{column}\"), \"{column}\" FROM \"{table}\" WHERE \"Id\" = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("text", reader.GetString(0));
        var stored = reader.GetString(1);
        Assert.DoesNotContain('Z', stored);
        Assert.DoesNotContain('+', stored);
    }

    internal static TimeZoneInfo FixedLocalZone() => TimeZoneInfo.CreateCustomTimeZone(
        "legacy-local-plus-four",
        TimeSpan.FromHours(4),
        "legacy-local-plus-four",
        "legacy-local-plus-four");

    internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }
}

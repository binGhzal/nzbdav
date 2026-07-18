using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using static backend.Tests.Database.LegacyTimestampContractTests;
using static backend.Tests.Services.ArrCorrelationServiceTests;

namespace backend.Tests.Services;

public sealed class ArrSearchNudgeServiceTests
{
    [Fact]
    public async Task ActiveMediaKeysUseBoundTwentyFourHourDeploymentLocalCutoff()
    {
        var capture = new CommandCaptureInterceptor();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(capture)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            FixedLocalZone());
        var expectedCutoff = new DateTime(2026, 7, 11, 4, 0, 0, DateTimeKind.Unspecified);
        var insideHistory = CreateHistory("inside", expectedCutoff.AddMinutes(1));
        var outsideHistory = CreateHistory("outside", expectedCutoff.AddMinutes(-1));
        dbContext.HistoryItems.AddRange(insideHistory, outsideHistory);
        dbContext.ArrDownloadCorrelations.AddRange(
            CreateCorrelation(insideHistory.Id, "sonarr:episode:inside"),
            CreateCorrelation(outsideHistory.Id, "sonarr:episode:outside"));
        await dbContext.SaveChangesAsync();
        capture.Clear();

        var result = await ArrSearchNudgeService.GetActiveMediaKeysAsync(
            dbContext,
            timeProvider,
            CancellationToken.None);

        Assert.Contains("sonarr:episode:inside", result);
        Assert.DoesNotContain("sonarr:episode:outside", result);
        AssertBoundLocalCutoff(capture, expectedCutoff);
    }

    private static HistoryItem CreateHistory(string name, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = createdAt,
        FileName = $"{name}.nzb",
        JobName = name,
        Category = "tv",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1,
        DownloadTimeSeconds = 1
    };

    private static ArrDownloadCorrelation CreateCorrelation(Guid historyItemId, string mediaKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new ArrDownloadCorrelation
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyItemId,
            ArrApp = "sonarr",
            InstanceKey = "sonarr:test",
            InstanceHost = "http://sonarr.test",
            MediaKey = mediaKey,
            Source = "test",
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };
    }
}

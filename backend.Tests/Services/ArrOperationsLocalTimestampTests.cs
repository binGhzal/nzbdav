using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using static backend.Tests.Database.LegacyTimestampContractTests;
using static backend.Tests.Services.ArrCorrelationServiceTests;

namespace backend.Tests.Services;

public sealed class ArrOperationsLocalTimestampTests
{
    [Fact]
    public async Task DuplicateHistoryUsesBoundTwentyFourHourDeploymentLocalCutoff()
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
        dbContext.HistoryItems.AddRange(
            CreateHistory("Outside Movie", expectedCutoff.AddMinutes(-1)),
            CreateHistory("Inside Movie", expectedCutoff.AddMinutes(1)));
        await dbContext.SaveChangesAsync();
        capture.Clear();
        var service = new ArrOperationsService(null!, timeProvider);

        var outsideDuplicate = await service.HasRejectableDuplicateAsync(
            dbContext,
            "Outside.Movie.nzb",
            "Outside Movie",
            "movies");

        Assert.False(outsideDuplicate);
        AssertBoundLocalCutoff(capture, expectedCutoff);
    }

    private static HistoryItem CreateHistory(string name, DateTime createdAt) => new()
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
}

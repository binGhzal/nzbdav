using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using static backend.Tests.Database.LegacyTimestampContractTests;

namespace backend.Tests.Services;

public sealed class ArrCorrelationServiceTests
{
    [Fact]
    public async Task RecentHistoryUsesBoundFourteenDayDeploymentLocalCutoff()
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
        var expectedCutoff = new DateTime(2026, 6, 28, 4, 0, 0, DateTimeKind.Unspecified);
        var inside = CreateHistory("inside", expectedCutoff.AddMinutes(1));
        var outside = CreateHistory("outside", expectedCutoff.AddMinutes(-1));
        dbContext.HistoryItems.AddRange(inside, outside);
        await dbContext.SaveChangesAsync();
        capture.Clear();

        var result = await ArrCorrelationService.GetRecentHistoryAsync(
            dbContext,
            timeProvider,
            CancellationToken.None);

        Assert.Collection(result, x => Assert.Equal(inside.Id, x.Id));
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

    internal static void AssertBoundLocalCutoff(CommandCaptureInterceptor capture, DateTime expectedCutoff)
    {
        var command = Assert.Single(capture.Commands, x =>
            x.CommandText.Contains("HistoryItems", StringComparison.Ordinal)
            && x.CommandText.Contains("CreatedAt", StringComparison.Ordinal));
        var parameter = Assert.Single(command.Parameters.Values.OfType<DateTime>());
        Assert.Equal(expectedCutoff.Ticks, parameter.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, parameter.Kind);
        Assert.DoesNotContain("CURRENT_TIMESTAMP", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CURRENT_DATE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("datetime(", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strftime(", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("now()", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }
}

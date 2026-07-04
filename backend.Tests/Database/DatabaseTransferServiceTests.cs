using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DatabaseTransferServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DatabaseTransferServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExportImportJson_RoundTripsApplicationRows()
    {
        await using (var source = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var queueItem = new QueueItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "Example.nzb",
                JobName = "Example",
                Category = "tv",
                NzbFileSize = 100,
                TotalSegmentBytes = 200,
                Priority = QueueItem.PriorityOption.High,
                PostProcessing = QueueItem.PostProcessingOption.None
            };
            source.QueueItems.Add(queueItem);
            source.QueuePriorityHints.Add(new QueuePriorityHint
            {
                QueueItemId = queueItem.Id,
                Score = 500,
                EffectivePriority = QueueItem.PriorityOption.High,
                ApplyToScheduling = true,
                ReasonsJson = """["test"]""",
                Source = "test",
                ComputedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            source.ArrSearchNudgeCommands.Add(new ArrSearchNudgeCommand
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = "sonarr:test",
                InstanceHost = "http://sonarr:8989",
                CommandName = "EpisodeSearch",
                TargetsJson = "[123]",
                Mode = "report",
                Status = "planned",
                CooldownKey = "sonarr:test:123",
                ReasonsJson = """["recently-aired"]""",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAllowedAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await source.SaveChangesAsync();
        }

        var exportPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        await using (var source = await _fixture.CreateMigratedContextAsync())
        {
            await DatabaseTransferService.ExportJsonAsync(source, exportPath);
        }

        await _fixture.ResetAsync();
        await using (var target = await _fixture.CreateMigratedContextAsync())
        {
            var result = await DatabaseTransferService.ImportJsonAsync(target, exportPath, replace: true);
            Assert.True(result.ImportedRows > 0);
        }

        await using var imported = await _fixture.CreateMigratedContextAsync();
        Assert.Equal("Example.nzb", (await imported.QueueItems.SingleAsync()).FileName);
        Assert.Equal(500, (await imported.QueuePriorityHints.SingleAsync()).Score);
        Assert.Equal("EpisodeSearch", (await imported.ArrSearchNudgeCommands.SingleAsync()).CommandName);
    }
}

using Microsoft.EntityFrameworkCore;
using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class HistoryCleanupServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public HistoryCleanupServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeleteMountedFilesPersistsContentSnapshotBeforeQueueDrains()
    {
        var historyItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var historyItem = new HistoryItem
            {
                Id = historyItemId,
                JobName = "Removed",
                FileName = "Removed.nzb",
                Category = "tv",
                CreatedAt = DateTime.UtcNow,
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed
            };
            var directory = CreateDirectory(Guid.NewGuid(), DavItem.ContentFolder.Id, "/content/Removed");
            var file = CreateFile(Guid.NewGuid(), directory.Id, "/content/Removed/Episode.mkv", historyItemId);

            dbContext.HistoryItems.Add(historyItem);
            dbContext.Items.AddRange(directory, file);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = file.Id,
                SegmentIds = ["segment-1"]
            });
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

            dbContext.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyItemId,
                DeleteMountedFiles = true
            });
            await dbContext.SaveChangesAsync();
        }

        using var service = new HistoryCleanupService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForCleanupQueueToDrainAsync(timeout.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var recoveredPaths = await assertionContext.Items
            .AsNoTracking()
            .Select(x => x.Path)
            .ToListAsync();

        Assert.DoesNotContain("/content/Removed/Episode.mkv", recoveredPaths);
    }

    private async Task WaitForCleanupQueueToDrainAsync(CancellationToken ct)
    {
        while (true)
        {
            await using var dbContext = await _fixture.CreateMigratedContextAsync();
            if (!await dbContext.HistoryCleanupItems.AnyAsync(ct))
                return;

            await Task.Delay(25, ct);
        }
    }

    private static DavItem CreateDirectory(Guid id, Guid parentId, string path)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = path
        };
    }

    private static DavItem CreateFile(Guid id, Guid parentId, string path, Guid historyItemId)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = path,
            HistoryItemId = historyItemId
        };
    }
}

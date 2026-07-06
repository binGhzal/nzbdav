using Microsoft.EntityFrameworkCore;
using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DavCleanupServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DavCleanupServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExecuteAsyncDeletesNestedDescendantsForRemovedDirectory()
    {
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var removedDirectoryId = Guid.NewGuid();
            var childDirectory = CreateDirectory(
                id: Guid.NewGuid(),
                parentId: removedDirectoryId,
                path: "/content/Removed/Season 1");
            var nestedFile = CreateFile(
                id: Guid.NewGuid(),
                parentId: childDirectory.Id,
                path: "/content/Removed/Season 1/Episode.mkv");
            dbContext.Items.AddRange(childDirectory, nestedFile);
            dbContext.DavCleanupItems.Add(new DavCleanupItem { Id = removedDirectoryId });
            await dbContext.SaveChangesAsync();
        }

        using var service = new DavCleanupService();
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

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var remainingPaths = await assertionContext.Items
            .AsNoTracking()
            .Select(x => x.Path)
            .ToListAsync();

        Assert.DoesNotContain("/content/Removed/Season 1", remainingPaths);
        Assert.DoesNotContain("/content/Removed/Season 1/Episode.mkv", remainingPaths);
    }

    [Fact]
    public async Task ExecuteAsyncPersistsContentSnapshotBeforeQueueDrains()
    {
        var removedDirectoryId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var childDirectory = CreateDirectory(
                id: Guid.NewGuid(),
                parentId: removedDirectoryId,
                path: "/content/Removed/Season 1");
            var nestedFile = CreateFile(
                id: Guid.NewGuid(),
                parentId: childDirectory.Id,
                path: "/content/Removed/Season 1/Episode.mkv");

            dbContext.Items.AddRange(childDirectory, nestedFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = nestedFile.Id,
                SegmentIds = ["segment-1"]
            });
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

            dbContext.DavCleanupItems.Add(new DavCleanupItem { Id = removedDirectoryId });
            await dbContext.SaveChangesAsync();
        }

        using var service = new DavCleanupService();
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

        Assert.DoesNotContain("/content/Removed/Season 1", recoveredPaths);
        Assert.DoesNotContain("/content/Removed/Season 1/Episode.mkv", recoveredPaths);
    }

    private async Task WaitForCleanupQueueToDrainAsync(CancellationToken ct)
    {
        while (true)
        {
            await using var dbContext = await _fixture.CreateMigratedContextAsync();
            if (!await dbContext.DavCleanupItems.AnyAsync(ct))
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

    private static DavItem CreateFile(Guid id, Guid parentId, string path)
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
            Path = path
        };
    }
}

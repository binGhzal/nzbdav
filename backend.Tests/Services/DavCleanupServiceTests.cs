using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [Fact]
    public async Task DeleteChildrenAndEnqueueInvalidations_RollsBackEverythingWhenSaveFails()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var removedDirectoryId = Guid.NewGuid();
        var child = CreateFile(Guid.NewGuid(), removedDirectoryId, "/content/Removed/Episode.mkv");
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Items.Add(child);
            setup.DavCleanupItems.Add(new DavCleanupItem { Id = removedDirectoryId });
            await setup.SaveChangesAsync();
            await setup.RcloneInvalidationItems.ExecuteDeleteAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using (var failingContext = new DavDatabaseContext(failingOptions))
        {
            var cleanupItems = await failingContext.DavCleanupItems.ToListAsync();
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                DavCleanupService.DeleteChildrenAndEnqueueInvalidationsAsync(
                    failingContext,
                    cleanupItems,
                    CancellationToken.None));
        }

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.Items.SingleOrDefaultAsync(x => x.Id == child.Id));
        Assert.NotNull(await assertionContext.DavCleanupItems.SingleOrDefaultAsync(x => x.Id == removedDirectoryId));
        Assert.Empty(await assertionContext.RcloneInvalidationItems.ToListAsync());
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

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("forced DAV cleanup save failure"));
    }
}

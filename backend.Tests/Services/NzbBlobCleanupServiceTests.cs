using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class NzbBlobCleanupServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public NzbBlobCleanupServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CleanupSkipsLockedIntentButProcessesUnlockedIntent()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var lockedId = Guid.NewGuid();
        var unlockedId = Guid.NewGuid();
        await BlobStore.WriteBlob(lockedId, (Stream)new MemoryStream([1, 2, 3]));
        await BlobStore.WriteBlob(unlockedId, (Stream)new MemoryStream([4, 5, 6]));
        dbContext.NzbBlobCleanupItems.AddRange(
            new NzbBlobCleanupItem { Id = lockedId },
            new NzbBlobCleanupItem { Id = unlockedId });
        await dbContext.SaveChangesAsync();

        var coordinator = new NzbBlobIngestCoordinator();
        using var lockedLease = await coordinator.AcquireAsync(lockedId, CancellationToken.None);
        using var service = new NzbBlobCleanupService(coordinator);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var assertionContext = new DavDatabaseContext();
                return !await assertionContext.NzbBlobCleanupItems
                    .AsNoTracking()
                    .AnyAsync(x => x.Id == unlockedId);
            });

            Assert.NotNull(BlobStore.ReadBlob(lockedId));
            Assert.Null(BlobStore.ReadBlob(unlockedId));
            await using var assertionContext = new DavDatabaseContext();
            Assert.True(await assertionContext.NzbBlobCleanupItems.AnyAsync(x => x.Id == lockedId));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            BlobStore.Delete(lockedId);
            BlobStore.Delete(unlockedId);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for NZB cleanup pass.");
    }
}

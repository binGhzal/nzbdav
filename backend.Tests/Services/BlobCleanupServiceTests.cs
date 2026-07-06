using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class BlobCleanupServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public BlobCleanupServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DavItemDeleteTriggerQueuesSharedFileBlobOnlyOnce()
    {
        var blobId = Guid.NewGuid();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.Items.AddRange(
            CreateFile("First.mkv", blobId),
            CreateFile("Second.mkv", blobId));
        await dbContext.SaveChangesAsync();

        await dbContext.Items
            .Where(x => x.FileBlobId == blobId)
            .ExecuteDeleteAsync();

        var cleanupItem = await dbContext.BlobCleanupItems.SingleAsync(x => x.Id == blobId);
        Assert.Equal(blobId, cleanupItem.Id);
    }

    [Fact]
    public async Task DavItemFileBlobUpdateTriggerQueuesSharedOldBlobOnlyOnce()
    {
        var oldBlobId = Guid.NewGuid();
        var newBlobId = Guid.NewGuid();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.Items.AddRange(
            CreateFile("First.mkv", oldBlobId),
            CreateFile("Second.mkv", oldBlobId));
        await dbContext.SaveChangesAsync();

        await dbContext.Items
            .Where(x => x.FileBlobId == oldBlobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.FileBlobId, newBlobId));

        var cleanupItem = await dbContext.BlobCleanupItems.SingleAsync(x => x.Id == oldBlobId);
        Assert.Equal(oldBlobId, cleanupItem.Id);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotDeleteBlobStillReferencedByHistoryItem()
    {
        var blobId = Guid.NewGuid();
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            await BlobStore.WriteBlob(blobId, new DavNzbFile
            {
                Id = blobId,
                SegmentIds = ["segment-1"]
            });
            dbContext.HistoryItems.Add(new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "Example.nzb",
                JobName = "Example",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1,
                DownloadTimeSeconds = 1,
                NzbBlobId = blobId
            });
            dbContext.BlobCleanupItems.Add(new BlobCleanupItem { Id = blobId });
            await dbContext.SaveChangesAsync();
        }

        using var service = new BlobCleanupService();
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
        Assert.False(await assertionContext.BlobCleanupItems.AnyAsync(x => x.Id == blobId));
        var blob = await BlobStore.ReadBlob<DavNzbFile>(blobId);
        Assert.NotNull(blob);
        Assert.Equal(["segment-1"], blob.SegmentIds);

        BlobStore.Delete(blobId);
    }

    private static DavItem CreateFile(string name, Guid fileBlobId)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = name,
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{name}",
            FileBlobId = fileBlobId
        };
    }

    private async Task WaitForCleanupQueueToDrainAsync(CancellationToken ct)
    {
        while (true)
        {
            await using var dbContext = await _fixture.CreateMigratedContextAsync();
            if (!await dbContext.BlobCleanupItems.AnyAsync(ct))
                return;

            await Task.Delay(25, ct);
        }
    }
}

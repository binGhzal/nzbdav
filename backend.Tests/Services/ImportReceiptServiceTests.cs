using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ImportReceiptServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ImportReceiptServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimPersistsAcrossContextsAndIsIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            dbContext.ImportReceipts.Add(CreateReceipt(davItemId, historyId, now));
            await dbContext.SaveChangesAsync();

            var service = new ImportReceiptService(dbContext);
            var first = await service.ClaimAsync(
                new ImportClaimRequest(davItemId, historyId, now.AddSeconds(1)), CancellationToken.None);
            var second = await service.ClaimAsync(
                new ImportClaimRequest(davItemId, historyId, now.AddSeconds(2)), CancellationToken.None);

            Assert.Equal(ImportReceiptState.UnlinkClaimed, first.State);
            Assert.True(first.Changed);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(ImportReceiptState.UnlinkClaimed, second.State);
            Assert.False(second.Changed);
        }

        await using var reopened = await _fixture.CreateMigratedContextAsync();
        var saved = await reopened.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(ImportReceiptState.UnlinkClaimed, saved.State);
        Assert.Equal(now.AddSeconds(1), saved.UpdatedAt);
    }

    [Fact]
    public async Task StageAvailableReceiptsStagesTrackedAndPersistedFilesWithoutSaving()
    {
        var now = DateTimeOffset.UtcNow;
        var historyId = Guid.NewGuid();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var directory = CreateDirectory(historyId);
        var persistedFile = CreateFile(directory, historyId, "persisted.mkv");
        dbContext.Items.AddRange(directory, persistedFile);
        await dbContext.SaveChangesAsync();
        var trackedFile = CreateFile(directory, historyId, "tracked.mkv");
        dbContext.Items.Add(trackedFile);

        var service = new ImportReceiptService(dbContext);
        await service.StageAvailableReceiptsAsync(historyId, now, CancellationToken.None);

        Assert.Equal(2, dbContext.ChangeTracker.Entries<ImportReceipt>().Count(x => x.State == EntityState.Added));
        await using var beforeSave = await _fixture.CreateMigratedContextAsync();
        Assert.Empty(await beforeSave.ImportReceipts.Where(x => x.HistoryItemId == historyId).ToListAsync());

        await dbContext.SaveChangesAsync();
        Assert.Equal(2, await dbContext.ImportReceipts.CountAsync(x => x.HistoryItemId == historyId));
    }

    [Fact]
    public async Task RemovedReceiptNeverMovesBackward()
    {
        var now = DateTimeOffset.UtcNow;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(CreateReceipt(davItemId, historyId, now));
        await dbContext.SaveChangesAsync();
        var service = new ImportReceiptService(dbContext);

        var removed = await service.MarkRemovedAsync(historyId, now.AddMinutes(1), CancellationToken.None);
        var imported = await service.MarkImportedAsync(historyId, now.AddMinutes(2), CancellationToken.None);
        var review = await service.MarkNeedsReviewAsync(davItemId, historyId, now.AddMinutes(3), "missing", CancellationToken.None);

        Assert.All(removed, x => Assert.Equal(ImportReceiptState.Removed, x.State));
        Assert.All(imported, x => Assert.Equal(ImportReceiptState.Removed, x.State));
        Assert.Equal(ImportReceiptState.Removed, review.State);
        Assert.Equal(now.AddMinutes(1), (await dbContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).UpdatedAt);
    }

    private static ImportReceipt CreateReceipt(Guid davItemId, Guid historyId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = davItemId,
        HistoryItemId = historyId,
        State = ImportReceiptState.Available,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static DavItem CreateDirectory(Guid historyId) => DavItem.New(
        Guid.NewGuid(), DavItem.ContentFolder, "movies", null,
        DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, historyId, null);

    private static DavItem CreateFile(DavItem parent, Guid historyId, string name) => DavItem.New(
        Guid.NewGuid(), parent, name, 1024,
        DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile, null, null, historyId, Guid.NewGuid());
}

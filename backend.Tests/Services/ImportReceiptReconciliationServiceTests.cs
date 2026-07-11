using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ImportReceiptReconciliationServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ImportReceiptReconciliationServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReconcileMarksLinkedClaimImportedAndExpiredClaimNeedsReview()
    {
        if (OperatingSystem.IsWindows()) return;

        var now = DateTimeOffset.UtcNow;
        var libraryPath = _fixture.CreateLibraryDirectory();
        var config = _fixture.CreateConfigManager(libraryPath);
        var sharedHistoryId = Guid.NewGuid();
        var linked = CreateFile(Guid.NewGuid(), sharedHistoryId, "Linked.mkv");
        var unresolved = CreateFile(Guid.NewGuid(), sharedHistoryId, "Unresolved.mkv");
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            Directory.CreateDirectory(libraryPath);
            dbContext.Items.AddRange(linked, unresolved);
            dbContext.ImportReceipts.AddRange(
                CreateReceipt(linked, ImportReceiptState.UnlinkClaimed, now.AddMinutes(-10)),
                CreateReceipt(unresolved, ImportReceiptState.UnlinkClaimed, now.AddMinutes(-31)));
            await dbContext.SaveChangesAsync();
        }
        File.CreateSymbolicLink(
            Path.Join(libraryPath, "Linked.mkv"),
            $"/mnt/nzbdav/.ids/{linked.Id}.mkv");

        await new ImportReceiptReconciliationService(config).RunOnceAsync(now, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == linked.Id)).State);
        var unresolvedReceipt = await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == unresolved.Id);
        Assert.Equal(ImportReceiptState.NeedsReview, unresolvedReceipt.State);
        Assert.NotNull(unresolvedReceipt.Detail);
    }

    [Fact]
    public async Task ReconcileProcessesAtMostOneHundredClaimsAndNeverRestoresAvailable()
    {
        var now = DateTimeOffset.UtcNow;
        var historyId = Guid.NewGuid();
        var claimedItems = Enumerable.Range(0, 101)
            .Select(index => CreateFile(Guid.NewGuid(), historyId, $"Unresolved-{index:D3}.mkv"))
            .ToList();
        var available = CreateFile(Guid.NewGuid(), historyId, "Available.mkv");
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            dbContext.Items.AddRange(claimedItems.Append(available));
            dbContext.ImportReceipts.AddRange(claimedItems
                .Select(x => CreateReceipt(x, ImportReceiptState.UnlinkClaimed, now.AddMinutes(-31)))
                .Append(CreateReceipt(available, ImportReceiptState.Available, now.AddMinutes(-31))));
            await dbContext.SaveChangesAsync();
        }

        var traversalCount = 0;
        await new ImportReceiptReconciliationService(
                () =>
                {
                    traversalCount++;
                    return [];
                })
            .RunOnceAsync(now, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            100,
            await assertionContext.ImportReceipts.CountAsync(x =>
                x.HistoryItemId == historyId && x.State == ImportReceiptState.NeedsReview));
        Assert.Equal(
            1,
            await assertionContext.ImportReceipts.CountAsync(x =>
                x.HistoryItemId == historyId && x.State == ImportReceiptState.UnlinkClaimed));
        Assert.Equal(
            ImportReceiptState.Available,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == available.Id)).State);
        Assert.Equal(1, traversalCount);
    }

    private static DavItem CreateFile(Guid id, Guid historyId, string name)
    {
        return DavItem.New(
            id, DavItem.ContentFolder, name, 1024,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile,
            null, null, historyId, Guid.NewGuid());
    }

    private static ImportReceipt CreateReceipt(
        DavItem item,
        ImportReceiptState state,
        DateTimeOffset updatedAt) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = item.Id,
        HistoryItemId = item.HistoryItemId!.Value,
        State = state,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };
}

using backend.Tests.Security;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;
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

    [Theory]
    [InlineData(ImportReceiptState.UnlinkClaimed)]
    [InlineData(ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.Removed)]
    [InlineData(ImportReceiptState.NeedsReview)]
    [InlineData(ImportReceiptState.VerificationQuarantined)]
    public async Task ClaimOnlyChangesAvailable(ImportReceiptState initialState)
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now, initialState);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        var result = await new ImportReceiptService(dbContext).ClaimAsync(
            new ImportClaimRequest(receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(1)),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(initialState, result.State);
    }

    [Theory]
    [InlineData(ImportReceiptState.Available, true, ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.UnlinkClaimed, true, ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.NeedsReview, true, ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.VerificationQuarantined, false, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.Imported, false, ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.Removed, false, ImportReceiptState.Removed)]
    public async Task MarkImportedUsesExplicitAllowedStates(
        ImportReceiptState initialState,
        bool expectedChanged,
        ImportReceiptState expectedState)
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now, initialState);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        var result = await new ImportReceiptService(dbContext).MarkImportedAsync(
            receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(expectedChanged, result.Changed);
        Assert.Equal(expectedState, result.State);
    }

    [Theory]
    [InlineData(ImportReceiptState.Available, true, ImportReceiptState.Removed)]
    [InlineData(ImportReceiptState.UnlinkClaimed, true, ImportReceiptState.Removed)]
    [InlineData(ImportReceiptState.Imported, true, ImportReceiptState.Removed)]
    [InlineData(ImportReceiptState.NeedsReview, true, ImportReceiptState.Removed)]
    [InlineData(ImportReceiptState.VerificationQuarantined, false, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.Removed, false, ImportReceiptState.Removed)]
    public async Task MarkRemovedPreservesTerminalVerificationQuarantine(
        ImportReceiptState initialState,
        bool expectedChanged,
        ImportReceiptState expectedState)
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now, initialState);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        var result = Assert.Single(await new ImportReceiptService(dbContext).MarkRemovedAsync(
            receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None));

        Assert.Equal(expectedChanged, result.Changed);
        Assert.Equal(expectedState, result.State);
    }

    [Theory]
    [InlineData(ImportReceiptState.Available, false, ImportReceiptState.Available)]
    [InlineData(ImportReceiptState.UnlinkClaimed, true, ImportReceiptState.NeedsReview)]
    [InlineData(ImportReceiptState.Imported, false, ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.NeedsReview, false, ImportReceiptState.NeedsReview)]
    [InlineData(ImportReceiptState.VerificationQuarantined, false, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.Removed, false, ImportReceiptState.Removed)]
    public async Task MarkNeedsReviewOnlyChangesUnlinkClaimed(
        ImportReceiptState initialState,
        bool expectedChanged,
        ImportReceiptState expectedState)
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now, initialState);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        var result = await new ImportReceiptService(dbContext).MarkNeedsReviewAsync(
            receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(1), "review", CancellationToken.None);

        Assert.Equal(expectedChanged, result.Changed);
        Assert.Equal(expectedState, result.State);
        var durable = await dbContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receipt.Id);
        Assert.Equal(
            expectedChanged ? PublicDiagnosticContract.ArrImportFailureMessage : null,
            durable.Detail);
    }

    [Theory]
    [InlineData(ImportReceiptState.Available, true, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.UnlinkClaimed, true, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.Imported, true, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.NeedsReview, true, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.VerificationQuarantined, true, ImportReceiptState.VerificationQuarantined)]
    [InlineData(ImportReceiptState.Removed, false, ImportReceiptState.Removed)]
    public async Task MarkVerificationQuarantineChangesEveryNonRemovedStateWithoutWeakeningReconciliation(
        ImportReceiptState initialState,
        bool expectedChanged,
        ImportReceiptState expectedState
    )
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now, initialState);
        receipt.Detail = "previous receipt detail";
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        var results = await new ImportReceiptService(dbContext).MarkVerificationQuarantineAsync(
            receipt.HistoryItemId,
            now.AddMinutes(1),
            PublicFailureCanary.Composite,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(expectedChanged, result.Changed);
        Assert.Equal(expectedState, result.State);
        var durable = await dbContext.ImportReceipts
            .AsNoTracking()
            .SingleAsync(x => x.Id == receipt.Id);
        Assert.Equal(expectedChanged ? now.AddMinutes(1) : now, durable.UpdatedAt);
        Assert.Equal(
            expectedChanged ? PublicDiagnosticContract.ArrImportFailureMessage : null,
            durable.Detail);
        PublicFailureCanary.AssertSafe(durable.Detail);

        if (initialState == ImportReceiptState.Available)
        {
            var reconciliation = await new ImportReceiptService(dbContext).MarkNeedsReviewAsync(
                receipt.DavItemId,
                receipt.HistoryItemId,
                now.AddMinutes(2),
                "reconciliation",
                CancellationToken.None);
            Assert.False(reconciliation.Changed);
            Assert.Equal(ImportReceiptState.VerificationQuarantined, reconciliation.State);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task MarkVerificationQuarantineMapsAbsentDetailToGeneric(string? detail)
    {
        var now = DateTimeOffset.UtcNow;
        var receipt = CreateReceipt(Guid.NewGuid(), Guid.NewGuid(), now);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.ImportReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        await new ImportReceiptService(dbContext).MarkVerificationQuarantineAsync(
            receipt.HistoryItemId,
            now.AddMinutes(1),
            detail!,
            CancellationToken.None);

        var durable = await dbContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receipt.Id);
        Assert.Equal(PublicDiagnosticContract.ArrImportFailureMessage, durable.Detail);
        PublicFailureCanary.AssertSafe(durable.Detail);
    }

    private static ImportReceipt CreateReceipt(
        Guid davItemId,
        Guid historyId,
        DateTimeOffset now,
        ImportReceiptState state = ImportReceiptState.Available) => new()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItemId,
            HistoryItemId = historyId,
            State = state,
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

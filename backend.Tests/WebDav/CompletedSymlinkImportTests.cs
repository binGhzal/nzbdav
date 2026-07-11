using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NWebDav.Server;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;
using backend.Tests.Services;

namespace backend.Tests.WebDav;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class CompletedSymlinkImportTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public CompletedSymlinkImportTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimedItemRemainsHiddenAfterStoreAndContextRestartWithoutDeletingContent()
    {
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory(historyId);
        var file = CreateFile(directory, historyId);
        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            dbContext.Items.AddRange(directory, file);
            dbContext.HistoryItems.Add(CreateHistory(historyId, directory.Id));
            dbContext.ImportReceipts.Add(CreateAvailableReceipt(file.Id, historyId));
            await dbContext.SaveChangesAsync();

            var store = CreateStore(directory, dbContext);
            Assert.NotNull(await store.GetItemAsync(file.Name, CancellationToken.None));
            Assert.Equal(DavStatusCode.NoContent, await store.DeleteItemAsync(file.Name, CancellationToken.None));
            Assert.Null(await store.GetItemAsync(file.Name, CancellationToken.None));
            Assert.NotNull(await dbContext.Items.SingleOrDefaultAsync(x => x.Id == file.Id));
        }

        await using var reopened = await _fixture.CreateMigratedContextAsync();
        var restartedStore = CreateStore(directory, reopened);
        Assert.Null(await restartedStore.GetItemAsync(file.Name, CancellationToken.None));
        Assert.NotNull(await reopened.Items.SingleOrDefaultAsync(x => x.Id == file.Id));
        Assert.Equal(
            ImportReceiptState.UnlinkClaimed,
            (await reopened.ImportReceipts.SingleAsync(x => x.DavItemId == file.Id)).State);
    }

    [Fact]
    public async Task DeleteReturnsServiceUnavailableWhenClaimPersistenceFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory(historyId);
        var file = CreateFile(directory, historyId);
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Items.AddRange(directory, file);
            setup.HistoryItems.Add(CreateHistory(historyId, directory.Id));
            setup.ImportReceipts.Add(CreateAvailableReceipt(file.Id, historyId));
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingClaimInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var store = CreateStore(directory, failingContext);

        Assert.Equal(DavStatusCode.ServiceUnavailable, await store.DeleteItemAsync(file.Name, CancellationToken.None));
        failingContext.ChangeTracker.Clear();
        Assert.Equal(
            ImportReceiptState.Available,
            (await failingContext.ImportReceipts.SingleAsync(x => x.DavItemId == file.Id)).State);
        Assert.NotNull(await failingContext.Items.SingleOrDefaultAsync(x => x.Id == file.Id));
    }

    private DatabaseStoreSymlinkCollection CreateStore(DavItem directory, DavDatabaseContext dbContext) =>
        new(directory, new DavDatabaseClient(dbContext), _fixture.CreateConfigManager());

    private static DavItem CreateDirectory(Guid historyId) => DavItem.New(
        Guid.NewGuid(), DavItem.ContentFolder, "Example Movie", null,
        DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, historyId, null);

    private static DavItem CreateFile(DavItem directory, Guid historyId) => DavItem.New(
        Guid.NewGuid(), directory, "Example.mkv", 1024,
        DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile, null, null, historyId, Guid.NewGuid());

    private static HistoryItem CreateHistory(Guid historyId, Guid directoryId) => new()
    {
        Id = historyId,
        CreatedAt = DateTime.UtcNow,
        FileName = "Example.nzb",
        JobName = "Example Movie",
        Category = "movies",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1024,
        DownloadTimeSeconds = 1,
        DownloadDirId = directoryId
    };

    private static ImportReceipt CreateAvailableReceipt(Guid fileId, Guid historyId) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = fileId,
        HistoryItemId = historyId,
        State = ImportReceiptState.Available,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FailingClaimInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new DbUpdateException("receipt write failed"));
    }
}

using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using backend.Tests.Services;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ContentIndexSnapshotTransactionTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ContentIndexSnapshotTransactionTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExplicitTransactionRequestsSnapshotOnlyAfterCommit()
    {
        await PrepareSnapshotStateAsync();
        var name = $"commit-{Guid.NewGuid():N}";

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.Items.Add(CreateContentDirectory(name));
        await dbContext.SaveChangesAsync();

        Assert.True(await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None));
        Assert.False(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));

        await transaction.CommitAsync();

        Assert.True(await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None));
        var snapshot = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        Assert.Contains(name, snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolledBackTransactionDoesNotRequestSnapshot()
    {
        await PrepareSnapshotStateAsync();

        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        dbContext.Items.Add(CreateContentDirectory($"rollback-{Guid.NewGuid():N}"));
        await dbContext.SaveChangesAsync();
        await transaction.RollbackAsync();

        Assert.True(await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None));
        Assert.False(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));
    }

    private async Task PrepareSnapshotStateAsync()
    {
        await _fixture.ResetAsync();
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        File.Delete(ContentIndexSnapshotStore.SnapshotFilePath);
        File.Delete(ContentIndexSnapshotStore.BackupSnapshotFilePath);
    }

    private static DavItem CreateContentDirectory(string name)
    {
        return DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null);
    }
}

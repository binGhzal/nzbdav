using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Tasks;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RemoveUnlinkedFilesTaskTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public RemoveUnlinkedFilesTaskTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RemoveUnlinkedItems_SkipsMalformedDavItemIdsWithoutAbortingBatch()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS TMP_LINKED_FILES; CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL PRIMARY KEY);");

        var validItem = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Movie.mkv",
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-2),
            null,
            null,
            null);
        dbContext.Items.Add(validItem);
        await dbContext.SaveChangesAsync();
        var updatedRows = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE DavItems SET CreatedAt = {0} WHERE Id = {1}",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            validItem.Id.ToString().ToUpperInvariant());
        Assert.Equal(1, updatedRows);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO DavItems
                (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path,
                 ReleaseDate, LastHealthCheck, NextHealthCheck, HistoryItemId, FileBlobId, NzbBlobId)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8},
                 NULL, NULL, NULL, NULL, NULL, NULL);
            """,
            "not-a-guid",
            "not-a",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DavItem.ContentFolder.Id.ToString(),
            "Broken.mkv",
            2048,
            (int)DavItem.ItemType.UsenetFile,
            (int)DavItem.ItemSubType.NzbFile,
            "/content/Broken.mkv");

        var candidateCount = await dbContext.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(Id) AS Value FROM DavItems
                WHERE Type = {0}
                  AND HistoryItemId IS NULL
                  AND CreatedAt < {1}
                  AND Id NOT IN (SELECT Id FROM TMP_LINKED_FILES)
                """,
                (int)DavItem.ItemType.UsenetFile,
                DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .SingleAsync();
        Assert.Equal(2, candidateCount);

        var task = new RemoveUnlinkedFilesTask(new ConfigManager(), new WebsocketManager(), isDryRun: false);

        await InvokeRemoveUnlinkedItemsAsync(task, DateTime.UtcNow.AddDays(-1), totalCount: 2);

        dbContext.ChangeTracker.Clear();
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await assertionContext.Items.AnyAsync(x => x.Id == validItem.Id));
        var malformedRows = await assertionContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM DavItems WHERE Id = 'not-a-guid'")
            .SingleAsync();
        Assert.Equal(0, malformedRows);
    }

    [Fact]
    public async Task RemoveUnlinkedItems_PersistsContentSnapshotAfterDeletingItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS TMP_LINKED_FILES; CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL PRIMARY KEY);");

        var removedItem = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Removed.mkv",
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-2),
            null,
            null,
            null);
        dbContext.Items.Add(removedItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = removedItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        var updatedRows = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE DavItems SET CreatedAt = {0} WHERE Id = {1}",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            removedItem.Id.ToString().ToUpperInvariant());
        Assert.Equal(1, updatedRows);

        var task = new RemoveUnlinkedFilesTask(new ConfigManager(), new WebsocketManager(), isDryRun: false);

        await InvokeRemoveUnlinkedItemsAsync(task, DateTime.UtcNow.AddDays(-1), totalCount: 1);

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.DoesNotContain(
            "/content/Removed.mkv",
            await assertionContext.Items.AsNoTracking().Select(x => x.Path).ToListAsync());
    }

    [Fact]
    public async Task Execute_ClearsStaleAuditReportWhenCleanupAbortsBeforeScanningUnlinkedItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        SetAuditReport(["/content/stale-file.mkv"]);
        var task = new RemoveUnlinkedFilesTask(new ConfigManager(), new WebsocketManager(), isDryRun: true);

        await task.Execute();

        Assert.Equal(
            "This list is Empty.\nYou must first run the task.",
            RemoveUnlinkedFilesTask.GetAuditReport());
    }

    private static Task InvokeRemoveUnlinkedItemsAsync(RemoveUnlinkedFilesTask task, DateTime createdBefore, int totalCount)
    {
        var method = typeof(RemoveUnlinkedFilesTask).GetMethod(
            "RemoveUnlinkedItems",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method.Invoke(task, [createdBefore, totalCount])!;
    }

    private static void SetAuditReport(List<string> paths)
    {
        var field = typeof(RemoveUnlinkedFilesTask).GetField(
            "_allRemovedPaths",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, paths);
    }
}

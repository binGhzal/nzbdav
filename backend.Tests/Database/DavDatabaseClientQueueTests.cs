using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DavDatabaseClientQueueTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DavDatabaseClientQueueTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetTopQueueItem_OrdersByPriorityAndSkipsActiveIds()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var lowPriorityItem = CreateQueueItem("Low priority", QueueItem.PriorityOption.Low, DateTime.UtcNow);
        var highPriorityItem = CreateQueueItem("High priority", QueueItem.PriorityOption.High, DateTime.UtcNow.AddSeconds(1));
        dbContext.QueueItems.AddRange(lowPriorityItem, highPriorityItem);
        dbContext.QueueNzbContents.AddRange(
            CreateQueueNzbContents(lowPriorityItem.Id),
            CreateQueueNzbContents(highPriorityItem.Id)
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var first = await dbClient.GetTopQueueItem();
        await first.queueNzbStream!.DisposeAsync();
        var second = await dbClient.GetTopQueueItem([highPriorityItem.Id]);
        await second.queueNzbStream!.DisposeAsync();

        Assert.Equal(highPriorityItem.Id, first.queueItem?.Id);
        Assert.Equal(lowPriorityItem.Id, second.queueItem?.Id);
    }

    [Fact]
    public async Task UpdateQueueItemsPriorityAsync_ReordersQueue()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var firstItem = CreateQueueItem("First", QueueItem.PriorityOption.Normal, DateTime.UtcNow);
        var secondItem = CreateQueueItem("Second", QueueItem.PriorityOption.Low, DateTime.UtcNow.AddSeconds(1));
        dbContext.QueueItems.AddRange(firstItem, secondItem);
        dbContext.QueueNzbContents.AddRange(
            CreateQueueNzbContents(firstItem.Id),
            CreateQueueNzbContents(secondItem.Id)
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.UpdateQueueItemsPriorityAsync([secondItem.Id], QueueItem.PriorityOption.Force);

        var topItem = await dbClient.GetTopQueueItem();
        await topItem.queueNzbStream!.DisposeAsync();

        Assert.Equal(secondItem.Id, topItem.queueItem?.Id);
    }

    [Fact]
    public async Task GetTopQueueItem_SkipsPausedQueueItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var pausedItem = CreateQueueItem("Paused", QueueItem.PriorityOption.Paused, DateTime.UtcNow);
        var normalItem = CreateQueueItem("Normal", QueueItem.PriorityOption.Normal, DateTime.UtcNow.AddSeconds(1));
        dbContext.QueueItems.AddRange(pausedItem, normalItem);
        dbContext.QueueNzbContents.AddRange(
            CreateQueueNzbContents(pausedItem.Id),
            CreateQueueNzbContents(normalItem.Id)
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var topItem = await dbClient.GetTopQueueItem();
        await topItem.queueNzbStream!.DisposeAsync();

        Assert.Equal(normalItem.Id, topItem.queueItem?.Id);
    }

    [Fact]
    public async Task GetQueueItemsCount_FiltersStatusAndExcludesActiveIds()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var activeItem = CreateQueueItem("Active", QueueItem.PriorityOption.Normal, DateTime.UtcNow);
        var queuedItem = CreateQueueItem("Queued", QueueItem.PriorityOption.Low, DateTime.UtcNow.AddSeconds(1));
        var pausedItem = CreateQueueItem("Paused", QueueItem.PriorityOption.Paused, DateTime.UtcNow.AddSeconds(2));
        dbContext.QueueItems.AddRange(activeItem, queuedItem, pausedItem);
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var queuedCount = await dbClient.GetQueueItemsCount(
            category: "movies",
            nzoIds: null,
            search: null,
            priorities: null,
            statuses: ["queued"],
            excludeIds: [activeItem.Id]);
        var pausedCount = await dbClient.GetQueueItemsCount(
            category: "movies",
            nzoIds: null,
            search: null,
            priorities: null,
            statuses: ["paused"],
            excludeIds: [activeItem.Id]);

        Assert.Equal(1, queuedCount);
        Assert.Equal(1, pausedCount);
    }

    [Fact]
    public async Task RemoveHistoryItemsAsync_SkipsExistingCleanupItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyItem = CreateHistoryItem();
        dbContext.HistoryItems.Add(historyItem);
        dbContext.HistoryCleanupItems.Add(new HistoryCleanupItem
        {
            Id = historyItem.Id,
            DeleteMountedFiles = false
        });
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.RemoveHistoryItemsAsync([historyItem.Id], deleteFiles: false);
        await dbContext.SaveChangesAsync();

        Assert.Empty(dbContext.HistoryItems);
        Assert.Single(dbContext.HistoryCleanupItems);
    }

    [Fact]
    public async Task RemoveHistoryItemsAsync_DeleteFilesSkipsExistingCleanupItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyItem = CreateHistoryItem();
        dbContext.HistoryItems.Add(historyItem);
        dbContext.HistoryCleanupItems.Add(new HistoryCleanupItem
        {
            Id = historyItem.Id,
            DeleteMountedFiles = true
        });
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.RemoveHistoryItemsAsync([historyItem.Id], deleteFiles: true);
        await dbContext.SaveChangesAsync();

        Assert.Empty(dbContext.HistoryItems);
        Assert.Single(dbContext.HistoryCleanupItems);
    }

    private static QueueItem CreateQueueItem
    (
        string jobName,
        QueueItem.PriorityOption priority,
        DateTime createdAt
    )
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = null
        };
    }

    private static QueueNzbContents CreateQueueNzbContents(Guid id)
    {
        return new QueueNzbContents
        {
            Id = id,
            NzbContents = "<nzb />"
        };
    }

    private static HistoryItem CreateHistoryItem()
    {
        return new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1
        };
    }
}

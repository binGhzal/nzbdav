using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class GetQueueControllerTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public GetQueueControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Queue_LimitZeroReturnsQueuedRowsUsingBoundedUnlimitedLimit()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = "Queued.Movie.nzb",
            JobName = "Queued Movie",
            Category = "movies",
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var configManager = CreateConfigManager(queuePaused: false);
        var websocketManager = new WebsocketManager();
        using var queueManager = new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var controller = new GetQueueController(
            CreateHttpContext("?start=0&limit=0"),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var response = Assert.IsType<GetQueueResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(queueItem.Id.ToString(), Assert.Single(response.Queue.Slots).NzoId);
        Assert.Equal(1, response.Queue.TotalCount);
        Assert.Equal(1, response.Queue.TotalCountAll);
        Assert.Equal(SabPagination.MaxLimit, response.Queue.Limit);
        Assert.Equal("Queued", response.Queue.Status);
    }

    [Fact]
    public async Task Queue_QueuedFilterDoesNotDuplicateAnActiveDownloadFromTheDatabase()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = "Downloading.Movie.nzb",
            JobName = "Downloading Movie",
            Category = "movies",
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var configManager = CreateConfigManager(queuePaused: false);
        var websocketManager = new WebsocketManager();
        using var queueManager = new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        AddInProgressQueueItem(queueManager, queueItem);
        var controller = new GetQueueController(
            CreateHttpContext("?status=queued&limit=10"),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var response = Assert.IsType<GetQueueResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(response.Queue.Slots);
        Assert.Equal(0, response.Queue.TotalCount);
        Assert.Equal(1, response.Queue.TotalCountAll);
        Assert.Equal("Idle", response.Queue.Status);
    }

    [Fact]
    public async Task Queue_ExcludesActivePostDownloadVerifyJobsFromDownloadSurface()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var mountFolder = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Example Movie",
            fileSize: null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: null);
        dbContext.Items.Add(mountFolder);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.Movie.nzb",
            JobName = "Example Movie",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024 * 1024 * 1024,
            DownloadTimeSeconds = 10,
            DownloadDirId = mountFolder.Id,
            NzbBlobId = historyId
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Verify,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = mountFolder.Id,
            Priority = 50,
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            PayloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson()
        });
        await dbContext.SaveChangesAsync();

        var configManager = CreateConfigManager(queuePaused: false);
        var websocketManager = new WebsocketManager();
        using var queueManager = new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var controller = new GetQueueController(
            CreateHttpContext("?limit=10"),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetQueueResponse>(ok.Value);
        Assert.Empty(response.Queue.Slots);
        Assert.Equal(0, response.Queue.TotalCount);
        Assert.Equal(0, response.Queue.TotalCountAll);
        Assert.Equal("Idle", response.Queue.Status);
    }

    [Fact]
    public async Task Queue_DoesNotLabelUnrelatedVerifyPayloadContainingMarkerAsVerifying()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var mountFolder = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Unrelated Verify",
            fileSize: null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: null);
        dbContext.Items.Add(mountFolder);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Unrelated.Verify.nzb",
            JobName = "Unrelated Verify",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = mountFolder.Id,
            NzbBlobId = historyId
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Verify,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = mountFolder.Id,
            Priority = 50,
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            PayloadJson = """{"Kind":"not_post_download_verify"}"""
        });
        await dbContext.SaveChangesAsync();

        var configManager = CreateConfigManager(queuePaused: false);
        var websocketManager = new WebsocketManager();
        using var queueManager = new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var controller = new GetQueueController(
            CreateHttpContext("?status=verifying&limit=10"),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetQueueResponse>(ok.Value);
        Assert.Empty(response.Queue.Slots);
        Assert.Equal(0, response.Queue.TotalCount);
    }

    [Fact]
    public async Task Queue_IncludesActivePostDownloadRepairJobsAsRepairingSlots()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var mountFolder = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Example Movie",
            fileSize: null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: null);
        var repairedFile = DavItem.New(
            Guid.NewGuid(),
            mountFolder,
            "Example Movie.mkv",
            fileSize: 1024 * 1024 * 1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.MultipartFile,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: Guid.NewGuid());
        dbContext.Items.AddRange(mountFolder, repairedFile);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.Movie.nzb",
            JobName = "Example Movie",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024 * 1024 * 1024,
            DownloadTimeSeconds = 10,
            DownloadDirId = mountFolder.Id,
            NzbBlobId = historyId
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Repair,
            Status = WorkerJob.JobStatus.Leased,
            TargetId = repairedFile.Id,
            Priority = 50,
            Attempts = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await dbContext.SaveChangesAsync();

        var configManager = CreateConfigManager(queuePaused: false);
        var websocketManager = new WebsocketManager();
        using var queueManager = new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
        var controller = new GetQueueController(
            CreateHttpContext("?status=repairing&limit=10"),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetQueueResponse>(ok.Value);
        var slot = Assert.Single(response.Queue.Slots);
        Assert.Equal(historyId.ToString(), slot.NzoId);
        Assert.Equal("Example.Movie.nzb", slot.Filename);
        Assert.Equal("movies", slot.Category);
        Assert.Equal("Repairing", slot.Status);
        Assert.Equal("100", slot.Percentage);
        Assert.Equal("0.00", slot.SizeLeftInMB);
        Assert.Contains("\"can_manage\":false", JsonSerializer.Serialize(slot));
        Assert.Equal(1, response.Queue.TotalCount);
        Assert.Equal(1, response.Queue.TotalCountAll);
        Assert.Equal("Repairing", response.Queue.Status);
    }

    private static ConfigManager CreateConfigManager(bool queuePaused = true)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "test-api-key" },
            new ConfigItem { ConfigName = "queue.paused", ConfigValue = queuePaused.ToString() }
        ]);
        return configManager;
    }

    private static void AddInProgressQueueItem(QueueManager queueManager, QueueItem queueItem)
    {
        var itemType = typeof(QueueManager).GetNestedType(
            "InProgressQueueItem",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(itemType);
        var inProgress = Activator.CreateInstance(itemType, nonPublic: true);
        Assert.NotNull(inProgress);
        itemType.GetProperty("QueueItem")!.SetValue(inProgress, queueItem);
        itemType.GetProperty("ProgressPercentage")!.SetValue(inProgress, 25);

        var itemsField = typeof(QueueManager).GetField(
            "_inProgressQueueItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(itemsField);
        var items = Assert.IsAssignableFrom<System.Collections.IDictionary>(itemsField.GetValue(queueManager));
        items.Add(queueItem.Id, inProgress);
    }

    private static DefaultHttpContext CreateHttpContext(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers["x-api-key"] = "test-api-key";
        return context;
    }

    private static async Task<IActionResult> HandleWithApiKeyAsync(GetQueueController controller)
    {
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            return await controller.HandleRequest();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }
}

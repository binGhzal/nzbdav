using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    public async Task Queue_IncludesActivePostDownloadVerifyJobsAsVerifyingSlots()
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

        var configManager = CreateConfigManager();
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
        var slot = Assert.Single(response.Queue.Slots);
        Assert.Equal(historyId.ToString(), slot.NzoId);
        Assert.Equal("Example.Movie.nzb", slot.Filename);
        Assert.Equal("movies", slot.Category);
        Assert.Equal("Verifying", slot.Status);
        Assert.Equal("100", slot.Percentage);
        Assert.Equal("0.00", slot.SizeLeftInMB);
        Assert.Equal(1, response.Queue.TotalCount);
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

        var configManager = CreateConfigManager();
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
        Assert.Equal(1, response.Queue.TotalCount);
    }

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "test-api-key" },
            new ConfigItem { ConfigName = "queue.paused", ConfigValue = "true" }
        ]);
        return configManager;
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

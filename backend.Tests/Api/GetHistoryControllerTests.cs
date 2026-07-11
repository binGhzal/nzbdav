using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class GetHistoryControllerTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public GetHistoryControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetHistory_DoesNotQueryDavItemsWhenPageHasNoDownloadFolders()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("FROM \"DavItems\"", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Broken.nzb",
            JobName = "Broken",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = null,
            FailMessage = "missing file"
        });
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var configManager = CreateConfigManager();
        var controller = new GetHistoryController(
            CreateHttpContext(),
            new DavDatabaseClient(dbContext),
            configManager);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetHistoryResponse>(ok.Value);
        Assert.Single(response.History.Slots);
        Assert.Null(response.History.Slots[0].DownloadPath);
        Assert.Equal(0, interceptor.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveHistoryItems_MarksReceiptRemovedBeforeHistoryCleanup(bool deleteFiles)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var directory = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "Example", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, historyId, null);
        var file = DavItem.New(
            Guid.NewGuid(), directory, "Example.mkv", 1024,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.MultipartFile, null, null, historyId, Guid.NewGuid());
        dbContext.Items.AddRange(directory, file);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = directory.Id
        });
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = file.Id,
            HistoryItemId = historyId,
            State = ImportReceiptState.Imported,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await dbContext.SaveChangesAsync();

        await new DavDatabaseClient(dbContext).RemoveHistoryItemsAsync([historyId], deleteFiles);
        await dbContext.SaveChangesAsync();

        Assert.Null(await dbContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        var receipt = await dbContext.ImportReceipts.SingleAsync(x => x.HistoryItemId == historyId);
        Assert.Equal(ImportReceiptState.Removed, receipt.State);
        Assert.NotNull(receipt.RemovedAt);
        Assert.Equal(deleteFiles, (await dbContext.HistoryCleanupItems.SingleAsync(x => x.Id == historyId)).DeleteMountedFiles);
    }

    [Fact]
    public async Task RemoveFromHistory_SaveFailureRollsBackReceiptAndHistoryRemoval()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryItems.Add(new HistoryItem
            {
                Id = historyId,
                CreatedAt = DateTime.UtcNow,
                FileName = "Rollback.nzb",
                JobName = "Rollback",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1024,
                DownloadTimeSeconds = 1
            });
            setup.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await setup.SaveChangesAsync();
        }
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var httpContext = CreateHttpContext($"?value={historyId}&del_completed_files=1");
        var request = await RemoveFromHistoryRequest.New(httpContext);
        var controller = new RemoveFromHistoryController(
            httpContext,
            new DavDatabaseClient(failingContext),
            CreateConfigManager(),
            new WebsocketManager());

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.RemoveFromHistory(request));

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
        Assert.Empty(await assertionContext.HistoryCleanupItems.ToListAsync());
    }

    [Fact]
    public async Task RemoveAll_BatchesReceiptTerminalizationIntoBoundedCommands()
    {
        const int itemCount = 1201;
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("UPDATE \"ImportReceipts\"", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        var historyIds = Enumerable.Range(0, itemCount).Select(_ => Guid.NewGuid()).ToArray();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryItems.AddRange(historyIds.Select(CreateCompletedHistory));
            setup.ImportReceipts.AddRange(historyIds.Select(historyId =>
                CreateReceipt(Guid.NewGuid(), historyId, ImportReceiptState.Imported)));
            await setup.SaveChangesAsync();
        }
        interceptor.Reset();
        await using (var removeContext = new DavDatabaseContext(options))
        {
            var httpContext = CreateHttpContext("?value=all");
            var request = await RemoveFromHistoryRequest.New(httpContext);
            var controller = new RemoveFromHistoryController(
                httpContext,
                new DavDatabaseClient(removeContext),
                CreateConfigManager(),
                new WebsocketManager());

            var response = await controller.RemoveFromHistory(request);

            Assert.True(response.Status);
        }

        Assert.Equal(3, interceptor.Count);
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Empty(await assertionContext.HistoryItems.Where(x => historyIds.Contains(x.Id)).ToListAsync());
        Assert.Equal(
            itemCount,
            await assertionContext.ImportReceipts.CountAsync(x =>
                historyIds.Contains(x.HistoryItemId) && x.State == ImportReceiptState.Removed));
    }

    [Fact]
    public async Task GetHistory_HidesCompletedRowsWithActivePostDownloadVerifyJobs()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        AddCompletedHistoryWithPostDownloadVerifyJob(dbContext, WorkerJob.JobStatus.Pending);
        await dbContext.SaveChangesAsync();

        var controller = new GetHistoryController(
            CreateHttpContext("?status=completed&limit=10"),
            new DavDatabaseClient(dbContext),
            CreateConfigManager());

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetHistoryResponse>(ok.Value);
        Assert.Empty(response.History.Slots);
        Assert.Equal(0, response.History.TotalCount);
    }

    [Fact]
    public async Task GetHistory_ShowsCompletedRowsAfterPostDownloadVerifyCompletes()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = AddCompletedHistoryWithPostDownloadVerifyJob(dbContext, WorkerJob.JobStatus.Completed);
        await dbContext.SaveChangesAsync();

        var controller = new GetHistoryController(
            CreateHttpContext("?status=completed&limit=10"),
            new DavDatabaseClient(dbContext),
            CreateConfigManager());

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetHistoryResponse>(ok.Value);
        var slot = Assert.Single(response.History.Slots);
        Assert.Equal(historyId.ToString(), slot.NzoId);
        Assert.Equal(1, response.History.TotalCount);
    }

    [Fact]
    public async Task GetHistory_HidesCompletedRowsWithActiveRepairJobsForChildFiles()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        AddCompletedHistoryWithRepairJob(dbContext, WorkerJob.JobStatus.Pending);
        await dbContext.SaveChangesAsync();

        var controller = new GetHistoryController(
            CreateHttpContext("?status=completed&limit=10"),
            new DavDatabaseClient(dbContext),
            CreateConfigManager());

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetHistoryResponse>(ok.Value);
        Assert.Empty(response.History.Slots);
        Assert.Equal(0, response.History.TotalCount);
    }

    [Fact]
    public async Task GetHistory_ShowsCompletedRowsAfterRepairJobCompletes()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = AddCompletedHistoryWithRepairJob(dbContext, WorkerJob.JobStatus.Completed);
        await dbContext.SaveChangesAsync();

        var controller = new GetHistoryController(
            CreateHttpContext("?status=completed&limit=10"),
            new DavDatabaseClient(dbContext),
            CreateConfigManager());

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetHistoryResponse>(ok.Value);
        var slot = Assert.Single(response.History.Slots);
        Assert.Equal(historyId.ToString(), slot.NzoId);
        Assert.Equal(1, response.History.TotalCount);
    }

    private static Guid AddCompletedHistoryWithPostDownloadVerifyJob(
        DavDatabaseContext dbContext,
        WorkerJob.JobStatus workerStatus)
    {
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
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = mountFolder.Id,
            NzbBlobId = historyId
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Verify,
            Status = workerStatus,
            TargetId = mountFolder.Id,
            Priority = 50,
            Attempts = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            CompletedAt = workerStatus == WorkerJob.JobStatus.Completed ? DateTimeOffset.UtcNow : null,
            PayloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson()
        });
        return historyId;
    }

    private static Guid AddCompletedHistoryWithRepairJob(
        DavDatabaseContext dbContext,
        WorkerJob.JobStatus workerStatus)
    {
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
            fileSize: 1024,
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
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = mountFolder.Id,
            NzbBlobId = historyId
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Repair,
            Status = workerStatus,
            TargetId = repairedFile.Id,
            Priority = 50,
            Attempts = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = workerStatus == WorkerJob.JobStatus.Leased ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            CompletedAt = workerStatus == WorkerJob.JobStatus.Completed ? DateTimeOffset.UtcNow : null
        });
        return historyId;
    }

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "test-api-key" },
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" }
        ]);
        return configManager;
    }

    private static HistoryItem CreateCompletedHistory(Guid historyId) => new()
    {
        Id = historyId,
        CreatedAt = DateTime.UtcNow,
        FileName = $"{historyId}.nzb",
        JobName = historyId.ToString(),
        Category = "movies",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1024,
        DownloadTimeSeconds = 1
    };

    private static ImportReceipt CreateReceipt(
        Guid davItemId,
        Guid historyId,
        ImportReceiptState state) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = davItemId,
        HistoryItemId = historyId,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private static DefaultHttpContext CreateHttpContext(string query = "?limit=10")
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers["x-api-key"] = "test-api-key";
        return context;
    }

    private static async Task<IActionResult> HandleWithApiKeyAsync(GetHistoryController controller)
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

    private sealed class CountingCommandInterceptor(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset()
        {
            Volatile.Write(ref _count, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CountIfMatched(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CountIfMatched(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CountIfMatched(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CountIfMatched(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CountIfMatched(DbCommand command)
        {
            if (predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("forced history transaction failure"));
    }

}

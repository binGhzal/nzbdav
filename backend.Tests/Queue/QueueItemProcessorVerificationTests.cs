using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Queue;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class QueueItemProcessorVerificationTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public QueueItemProcessorVerificationTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessAsync_FailsWhenQueueNzbStreamIsMissing()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                queueItem,
                queueNzbStream: null,
                dbClient,
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.Completed, outcome);
        Assert.Equal(0, await dbContext.QueueItems.CountAsync());
        var historyItem = await dbContext.HistoryItems.SingleAsync();
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Equal("The NZB file is missing from the queue store.", historyItem.FailMessage);
        Assert.Null(queueItem.PauseUntil);
        Assert.Empty(await dbContext.ImportReceipts.Where(x => x.HistoryItemId == historyItem.Id).ToListAsync());
    }

    [Fact]
    public async Task ProcessAsync_WritesHistoryWithInjectedDeploymentLocalWallTime()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var timeProvider = FixedLocalTimeProvider();

        await new QueueItemProcessor(
                queueItem,
                queueNzbStream: null,
                new DavDatabaseClient(dbContext),
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None,
                timeProvider: timeProvider)
            .ProcessAsync();

        var historyItem = await dbContext.HistoryItems.SingleAsync();
        Assert.Equal(new DateTime(2026, 7, 12, 5, 2, 3, DateTimeKind.Unspecified), historyItem.CreatedAt);
        Assert.Equal(DateTimeKind.Unspecified, historyItem.CreatedAt.Kind);
    }

    [Fact]
    public async Task ProcessAsync_WritesRetryPauseWithInjectedDeploymentLocalWallTime()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var timeProvider = FixedLocalTimeProvider();

        var outcome = await new QueueItemProcessor(
                queueItem,
                new FailingReadStream(new RetryableDownloadException("retry")),
                new DavDatabaseClient(dbContext),
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None,
                timeProvider: timeProvider)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.RetryScheduled, outcome);
        dbContext.ChangeTracker.Clear();
        var pauseUntil = (await dbContext.QueueItems.SingleAsync()).PauseUntil;
        Assert.Equal(new DateTime(2026, 7, 12, 5, 3, 3, DateTimeKind.Unspecified), pauseUntil);
        Assert.Equal(DateTimeKind.Unspecified, pauseUntil!.Value.Kind);
    }

    [Fact]
    public void GetDownloadTimeSeconds_UsesMonotonicStopwatchTicks()
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "GetDownloadTimeSeconds",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        const long startTimestamp = 100;
        var endTimestamp = startTimestamp + Stopwatch.Frequency * 2 + Stopwatch.Frequency / 2;

        var result = Assert.IsType<int>(method.Invoke(null, [startTimestamp, endTimestamp]));

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task MarkQueueItemCompleted_StagesAvailableReceiptInCompletionTransaction()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            dbClient,
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);
        DavItem? outputFile = null;

        await InvokeMarkQueueItemCompletedAsync(processor, async () =>
        {
            var mountFolder = CreateDavItem(
                "Example Movie", DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, queueItem.Id);
            outputFile = CreateDavItem(
                "Example.mkv", DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, queueItem.Id);
            dbContext.Items.AddRange(mountFolder, outputFile);
            await Task.Yield();
            return mountFolder;
        });

        var history = await dbContext.HistoryItems.SingleAsync(x => x.Id == queueItem.Id);
        var receipt = await dbContext.ImportReceipts.SingleAsync(x => x.HistoryItemId == history.Id);
        Assert.Equal(outputFile!.Id, receipt.DavItemId);
        Assert.Equal(ImportReceiptState.Available, receipt.State);
    }

    [Fact]
    public async Task MarkQueueItemCompleted_SaveFailureDoesNotPartiallyCommitCompletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var persistedQueueItem = await failingContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            Stream.Null,
            new DavDatabaseClient(failingContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await Assert.ThrowsAsync<DbUpdateException>(() => InvokeMarkQueueItemCompletedAsync(processor, () =>
        {
            var mountFolder = CreateDavItem(
                "Failed Movie", DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, queueItem.Id);
            var output = CreateDavItem(
                "Failed.mkv", DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, queueItem.Id);
            failingContext.Items.AddRange(mountFolder, output);
            return Task.FromResult<DavItem?>(mountFolder);
        }));

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Empty(await assertionContext.ImportReceipts.Where(x => x.HistoryItemId == queueItem.Id).ToListAsync());
    }

    [Fact]
    public async Task MarkQueueItemCompleted_FreshReconciliationReportsUncommittedSaveFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var persistedQueueItem = await failingContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            Stream.Null,
            new DavDatabaseClient(failingContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None,
            completionContextFactory: () => new DavDatabaseContext(baseOptions));

        await Assert.ThrowsAsync<QueueCompletionNotCommittedException>(() =>
            InvokeMarkQueueItemCompletedAsync(processor, () => Task.FromResult<DavItem?>(null)));

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
    }

    [Fact]
    public async Task ProcessAsync_CompletionSaveFailureKeepsDurableWorkerRetryable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }

        var failCompletionSave = new FailNthSaveInterceptor(2);
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(failCompletionSave)
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var persistedQueueItem = await failingContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            queueNzbStream: null,
            new DavDatabaseClient(failingContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        var outcome = await processor.ProcessAsync();

        Assert.Equal(2, failCompletionSave.SaveAttempts);
        Assert.Equal(QueueItemProcessor.ProcessingOutcome.RetryScheduled, outcome);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
    }

    [Theory]
    [InlineData(null, HistoryItem.DownloadStatusOption.Completed)]
    [InlineData("provider failed", HistoryItem.DownloadStatusOption.Failed)]
    public async Task MarkQueueItemCompleted_ReconcilesAmbiguousPostCommitWithFreshContext(
        string? error,
        HistoryItem.DownloadStatusOption expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }

        var interceptor = new ThrowAfterNextCommitInterceptor();
        var completionOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var completionContext = new DavDatabaseContext(completionOptions);
        var persistedQueueItem = await completionContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            Stream.Null,
            new DavDatabaseClient(completionContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None,
            completionContextFactory: () => new DavDatabaseContext(baseOptions));
        interceptor.Arm();

        await InvokeMarkQueueItemCompletedAsync(
            processor,
            () => Task.FromResult<DavItem?>(null),
            error);

        Assert.Equal(1, interceptor.ExceptionsThrown);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Null(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        var history = await assertionContext.HistoryItems.SingleAsync(x => x.Id == queueItem.Id);
        Assert.Equal(expectedStatus, history.DownloadStatus);
        Assert.Equal(error, history.FailMessage);
    }

    [Fact]
    public async Task MarkQueueItemCompleted_PreservesIndeterminateQueueAndHistoryState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            setup.HistoryItems.Add(new HistoryItem
            {
                Id = queueItem.Id,
                CreatedAt = DateTime.Now,
                FileName = queueItem.FileName,
                JobName = queueItem.JobName,
                Category = queueItem.Category,
                DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
                FailMessage = "preexisting terminal state",
                NzbBlobId = queueItem.Id
            });
            await setup.SaveChangesAsync();
        }

        await using var dbContext = new DavDatabaseContext(options);
        var persistedQueueItem = await dbContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            Stream.Null,
            new DavDatabaseClient(dbContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None,
            completionContextFactory: () => new DavDatabaseContext(options));

        await Assert.ThrowsAsync<QueueCompletionIndeterminateException>(() =>
            InvokeMarkQueueItemCompletedAsync(processor, () => Task.FromResult<DavItem?>(null)));

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        var history = await assertionContext.HistoryItems.SingleAsync(x => x.Id == queueItem.Id);
        Assert.Equal("preexisting terminal state", history.FailMessage);
    }

    [Fact]
    public async Task ProcessAsync_PreservesCancellationWhileSchedulingRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var cancelRetrySave = new CancelAndFailNthSaveInterceptor(2, cancellation);
        var processingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(cancelRetrySave)
            .Options;
        await using var processingContext = new DavDatabaseContext(processingOptions);
        var persistedQueueItem = await processingContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                persistedQueueItem,
                new FailingReadStream(new RetryableDownloadException("retry")),
                new DavDatabaseClient(processingContext),
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                cancellation.Token)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.Cancelled, outcome);
        Assert.True(cancellation.IsCancellationRequested);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
    }

    [Fact]
    public async Task MarkQueueItemCompleted_RejectsStaleWorkerLeaseGeneration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var queueItem = CreateQueueItem();
        var workerJob = new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Download,
            TargetId = queueItem.Id,
            Status = WorkerJob.JobStatus.Leased,
            Priority = 0,
            Attempts = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "current-worker",
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 2,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
        };
        dbContext.AddRange(queueItem, workerJob);
        await dbContext.SaveChangesAsync();
        var staleIdentity = new WorkerLeaseIdentity(
            workerJob.Id,
            workerJob.LeaseOwner!,
            workerJob.LeaseToken!.Value,
            workerJob.LeaseGeneration - 1);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            new DavDatabaseClient(dbContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None,
            workerLease: staleIdentity,
            completionContextFactory: () => new DavDatabaseContext(options));

        await Assert.ThrowsAsync<QueueWorkerLeaseLostException>(() =>
            InvokeMarkQueueItemCompletedAsync(processor, () => Task.FromResult<DavItem?>(null)));

        dbContext.ChangeTracker.Clear();
        Assert.NotNull(await dbContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await dbContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        var savedWorker = await dbContext.WorkerJobs.SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(2, savedWorker.LeaseGeneration);
        Assert.Equal(WorkerJob.JobStatus.Leased, savedWorker.Status);
    }

    [Fact]
    public async Task ProcessAsync_InitialLifecycleSaveFailureKeepsQueueItemRetryable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            await setup.SaveChangesAsync();
        }

        var failInitialLifecycleSave = new FailNthSaveInterceptor(1);
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(failInitialLifecycleSave)
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var persistedQueueItem = await failingContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                persistedQueueItem,
                queueNzbStream: null,
                new DavDatabaseClient(failingContext),
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None)
            .ProcessAsync();

        Assert.Equal(1, failInitialLifecycleSave.SaveAttempts);
        Assert.Equal(QueueItemProcessor.ProcessingOutcome.RetryScheduled, outcome);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Empty(await assertionContext.ArrDownloadLifecycleEvents.ToListAsync());
    }

    [Fact]
    public async Task MarkQueueItemCompleted_DoesNotBroadcastHistoryWhileArrVisibilityIsHidden()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        var visibilityNotifier = new RecordingHistoryVisibilityNotifier(configManager, websocketManager);
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            new DavDatabaseClient(dbContext),
            usenetClient,
            configManager,
            websocketManager,
            new ArrDownloadReportService(
                configManager,
                () => [new SonarrClient("http://sonarr.test", "test-key")]),
            new Progress<int>(),
            CancellationToken.None,
            historyVisibilityNotifier: visibilityNotifier);

        await InvokeMarkQueueItemCompletedAsync(processor, () =>
        {
            var mountFolder = CreateDavItem(
                "Hidden Movie", DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, queueItem.Id);
            dbContext.Items.Add(mountFolder);
            return Task.FromResult<DavItem?>(mountFolder);
        });

        Assert.False(WasHistoryAdditionBroadcast(websocketManager));
        Assert.Null((await dbContext.ArrImportCommands.SingleAsync()).VisibleAt);
        Assert.Equal(0, visibilityNotifier.Calls);
    }

    [Fact]
    public async Task MarkQueueItemCompleted_PersistsCorrelationAndLifecycleInSingleSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var queueItem = CreateQueueItem();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.QueueItems.Add(queueItem);
            setup.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                QueueItemId = queueItem.Id,
                ArrApp = "sonarr",
                InstanceKey = "sonarr:test",
                InstanceHost = "http://sonarr.test",
                Source = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var failSecondSave = new FailNthSaveInterceptor(2);
        var completionOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(failSecondSave)
            .Options;
        await using var completionContext = new DavDatabaseContext(completionOptions);
        var persistedQueueItem = await completionContext.QueueItems.SingleAsync(x => x.Id == queueItem.Id);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            persistedQueueItem,
            Stream.Null,
            new DavDatabaseClient(completionContext),
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await InvokeMarkQueueItemCompletedAsync(processor, () =>
        {
            var mountFolder = CreateDavItem(
                "Atomic Movie", DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, queueItem.Id);
            completionContext.Items.Add(mountFolder);
            return Task.FromResult<DavItem?>(mountFolder);
        });

        Assert.Equal(1, failSecondSave.SaveAttempts);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Null(await assertionContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        var correlation = await assertionContext.ArrDownloadCorrelations.SingleAsync();
        Assert.Null(correlation.QueueItemId);
        Assert.Equal(queueItem.Id, correlation.HistoryItemId);
        var lifecycle = await assertionContext.ArrDownloadLifecycleEvents.SingleAsync();
        Assert.Equal(queueItem.Id, lifecycle.HistoryItemId);
        Assert.Equal("Completed", lifecycle.State);
    }

    [Fact]
    public async Task MarkQueueItemCompleted_PostCommitNotifierFailureDoesNotUndoCompletion()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new FakeNntpClient();
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            new DavDatabaseClient(dbContext),
            usenetClient,
            configManager,
            websocketManager,
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None,
            historyVisibilityNotifier: new ThrowingHistoryVisibilityNotifier(
                configManager,
                websocketManager));

        await InvokeMarkQueueItemCompletedAsync(processor, () => Task.FromResult<DavItem?>(null));

        dbContext.ChangeTracker.Clear();
        Assert.Null(await dbContext.QueueItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.NotNull(await dbContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == queueItem.Id));
        Assert.Equal("Completed", (await dbContext.ArrDownloadLifecycleEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task ProcessAsync_FailsWhenQueueNzbStreamReadFails()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                queueItem,
                new FailingReadStream(new IOException("queue blob read failure")),
                dbClient,
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.Completed, outcome);
        Assert.Equal(0, await dbContext.QueueItems.CountAsync());
        var historyItem = await dbContext.HistoryItems.SingleAsync();
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Equal("The NZB file could not be read from the queue store.", historyItem.FailMessage);
        Assert.Null(queueItem.PauseUntil);
    }

    [Theory]
    [InlineData("Movie.mkv", true)]
    [InlineData("Movie.iso", true)]
    [InlineData("Archive.rar", false)]
    [InlineData("Archive.r00", false)]
    [InlineData("Archive.7z.001", false)]
    [InlineData("Movie.mkv.001", false)]
    [InlineData("Subtitle.srt", false)]
    [InlineData("poster.jpg", false)]
    [InlineData("notes.txt", false)]
    public void ShouldEnqueuePostDownloadVerify_OnlyIncludesPlayableMediaOutputs(string fileName, bool expected)
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            IdPrefix = "abcde",
            CreatedAt = DateTime.UtcNow,
            Name = fileName,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{fileName}"
        };

        Assert.Equal(expected, InvokeShouldEnqueuePostDownloadVerify(davItem));
    }

    [Fact]
    public void ShouldEnqueuePostDownloadVerify_ExcludesDirectories()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            IdPrefix = "abcde",
            CreatedAt = DateTime.UtcNow,
            Name = "Movie.mkv",
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = "/content/Movie"
        };

        Assert.False(InvokeShouldEnqueuePostDownloadVerify(davItem));
    }

    [Theory]
    [InlineData(QueueItem.PriorityOption.Low, 50)]
    [InlineData(QueueItem.PriorityOption.Normal, 50)]
    [InlineData(QueueItem.PriorityOption.High, 50)]
    [InlineData(QueueItem.PriorityOption.Force, 100)]
    public void GetPostDownloadVerifyPriority_UsesPositivePriorityForAllPostDownloadVerifyJobs(
        QueueItem.PriorityOption priority,
        int expected)
    {
        Assert.Equal(expected, InvokeGetPostDownloadVerifyPriority(priority));
    }

    [Theory]
    [InlineData(QueueItem.PriorityOption.Low)]
    [InlineData(QueueItem.PriorityOption.Normal)]
    public async Task EnqueuePostDownloadVerifyJob_UsesHighOperationalPriorityIndependentOfDownloadPriority(
        QueueItem.PriorityOption priority)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem(priority);
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var mountFolder = CreateDavItem(
            "Example Movie",
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            queueItem.Id);
        var video = CreateDavItem(
            "Movie.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);

        dbContext.Items.AddRange(mountFolder, video);
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            dbClient,
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await InvokeEnqueuePostDownloadVerifyJobAsync(processor, mountFolder);

        var workerJob = Assert.Single(dbContext.ChangeTracker
            .Entries<WorkerJob>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity));
        Assert.Equal(50, workerJob.Priority);
    }

    [Fact]
    public async Task EnqueuePostDownloadVerifyJob_TargetsCompletedMountFolderOnce()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var mountFolder = CreateDavItem(
            "Example Movie",
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            queueItem.Id);
        var firstVideo = CreateDavItem(
            "First.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);
        var secondVideo = CreateDavItem(
            "Second.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);

        dbContext.Items.AddRange(mountFolder, firstVideo, secondVideo);
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            dbClient,
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await InvokeEnqueuePostDownloadVerifyJobAsync(processor, mountFolder);

        var workerJob = Assert.Single(dbContext.ChangeTracker
            .Entries<WorkerJob>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity));
        Assert.Equal(WorkerJob.JobKind.Verify, workerJob.Kind);
        Assert.Equal(mountFolder.Id, workerJob.TargetId);
        Assert.Contains("\"Kind\":\"post_download_verify\"", workerJob.PayloadJson);
    }

    private static bool InvokeShouldEnqueuePostDownloadVerify(DavItem davItem)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "ShouldEnqueuePostDownloadVerify",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [davItem])!;
    }

    private static int InvokeGetPostDownloadVerifyPriority(QueueItem.PriorityOption priority)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "GetPostDownloadVerifyPriority",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (int)method.Invoke(null, [priority])!;
    }

    private static async Task InvokeEnqueuePostDownloadVerifyJobAsync(
        QueueItemProcessor processor,
        DavItem mountFolder)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "EnqueuePostDownloadVerifyJobAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(processor, [mountFolder])!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeMarkQueueItemCompletedAsync(
        QueueItemProcessor processor,
        Func<Task<DavItem?>> databaseOperations,
        string? error = null)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "MarkQueueItemCompleted",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(processor, [Stopwatch.GetTimestamp(), error, databaseOperations])!;
        await task.ConfigureAwait(false);
    }

    private static QueueItem CreateQueueItem(QueueItem.PriorityOption priority = QueueItem.PriorityOption.Normal)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.Movie.nzb",
            JobName = "Example Movie",
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
    }

    private static DavItem CreateDavItem(
        string name,
        DavItem.ItemType itemType,
        DavItem.ItemSubType subType,
        Guid historyItemId)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = name,
            Type = itemType,
            SubType = subType,
            Path = $"/content/{name}",
            HistoryItemId = historyItemId
        };
    }

    private static TimeProvider FixedLocalTimeProvider() => new FixedTimeProvider(
        new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone(
            "legacy-local-plus-four",
            TimeSpan.FromHours(4),
            "legacy-local-plus-four",
            "legacy-local-plus-four"));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }

    private static bool WasHistoryAdditionBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemAdded);
    }

    private sealed class FailingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw exception;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromException<int>(exception);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(exception);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("forced completion transaction failure"));
    }

    private sealed class FailNthSaveInterceptor(int failureAttempt) : SaveChangesInterceptor
    {
        private int _saveAttempts;

        public int SaveAttempts => Volatile.Read(ref _saveAttempts);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveAttempts) != failureAttempt)
                return ValueTask.FromResult(result);

            return ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException($"forced save failure at attempt {failureAttempt}"));
        }
    }

    private sealed class CancelAndFailNthSaveInterceptor(
        int failureAttempt,
        CancellationTokenSource cancellationTokenSource) : SaveChangesInterceptor
    {
        private int _saveAttempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveAttempts) != failureAttempt)
                return ValueTask.FromResult(result);

            cancellationTokenSource.Cancel();
            return ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateConcurrencyException("forced cancellation race during retry save"));
        }
    }

    private sealed class ThrowAfterNextCommitInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _exceptionsThrown;

        public int ExceptionsThrown => Volatile.Read(ref _exceptionsThrown);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return Task.CompletedTask;

            Interlocked.Increment(ref _exceptionsThrown);
            return Task.FromException(
                new InvalidOperationException("simulated ambiguous transaction commit acknowledgement"));
        }
    }

    private sealed class ThrowingHistoryVisibilityNotifier(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : HistoryVisibilityNotifier(configManager, websocketManager)
    {
        public override Task<bool> PublishIfVisibleAsync(
            DavDatabaseContext dbContext,
            Guid historyItemId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("forced post-commit notifier failure");
    }

    private sealed class RecordingHistoryVisibilityNotifier(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : HistoryVisibilityNotifier(configManager, websocketManager)
    {
        public int Calls { get; private set; }

        public override Task<bool> PublishIfVisibleAsync(
            DavDatabaseContext dbContext,
            Guid historyItemId,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }
}

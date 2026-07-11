using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;

namespace backend.Tests.Api;

public sealed class RemoveFromHistoryTransactionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControllerOwnedVerifiedDuplicateReturnsSuccessWithoutSecondBroadcast(
        bool cleanupIsStillQueued)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Removed,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                RemovedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            if (cleanupIsStillQueued)
            {
                setup.HistoryCleanupItems.Add(new HistoryCleanupItem
                {
                    Id = historyId,
                    DeleteMountedFiles = true
                });
            }
            await setup.SaveChangesAsync();
        }

        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "prior-removal");
        await using var controllerContext = new DavDatabaseContext(options);
        var staleHistory = CreateCompletedHistory(historyId);
        controllerContext.Attach(staleHistory);
        controllerContext.Remove(staleHistory);
        var request = await CreateRequestAsync(historyId, CancellationToken.None);
        var controller = CreateController(controllerContext, websocketManager);

        var response = await controller.RemoveFromHistory(request);

        Assert.True(response.Status);
        Assert.Equal("prior-removal", GetLastHistoryRemovalMessage(websocketManager));
    }

    [Fact]
    public async Task ControllerOwnedUnrelatedConcurrencyFailurePropagatesWithoutBroadcast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyId,
                DeleteMountedFiles = true
            });
            await setup.SaveChangesAsync();
        }

        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "prior-removal");
        await using var controllerContext = new DavDatabaseContext(options);
        var unrelatedMissingConfig = new ConfigItem
        {
            ConfigName = "missing.concurrent.config",
            ConfigValue = "missing"
        };
        controllerContext.Attach(unrelatedMissingConfig);
        controllerContext.Remove(unrelatedMissingConfig);
        var request = await CreateRequestAsync(historyId, CancellationToken.None);
        var controller = CreateController(controllerContext, websocketManager);

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => controller.RemoveFromHistory(request));

        Assert.IsType<ConfigItem>(Assert.Single(exception.Entries).Entity);
        Assert.Equal("prior-removal", GetLastHistoryRemovalMessage(websocketManager));
    }

    [Fact]
    public async Task ControllerOwnedConcurrencyWithoutEntriesPropagatesWithoutBroadcast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyId,
                DeleteMountedFiles = true
            });
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailFirstSaveInterceptor(
                new DbUpdateConcurrencyException("forced entryless concurrency")))
            .Options;
        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "prior-removal");
        await using var controllerContext = new DavDatabaseContext(failingOptions);
        var request = await CreateRequestAsync(historyId, CancellationToken.None);
        var controller = CreateController(controllerContext, websocketManager);

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => controller.RemoveFromHistory(request));

        Assert.Equal("forced entryless concurrency", exception.Message);
        Assert.Empty(exception.Entries);
        Assert.Equal("prior-removal", GetLastHistoryRemovalMessage(websocketManager));
    }

    [Fact]
    public async Task ControllerOwnedRequestedConcurrencyWithoutCompletedCleanupProofPropagatesWithoutBroadcast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyId,
                DeleteMountedFiles = false
            });
            setup.Items.Add(DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "still-mounted",
                null,
                DavItem.ItemType.Directory,
                DavItem.ItemSubType.Directory,
                null,
                null,
                historyId,
                null));
            await setup.SaveChangesAsync();
        }

        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "prior-removal");
        await using var controllerContext = new DavDatabaseContext(options);
        var staleHistory = CreateCompletedHistory(historyId);
        controllerContext.Attach(staleHistory);
        controllerContext.Remove(staleHistory);
        var request = await CreateRequestAsync(historyId, CancellationToken.None);
        var controller = CreateController(controllerContext, websocketManager);

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => controller.RemoveFromHistory(request));

        Assert.IsType<HistoryItem>(Assert.Single(exception.Entries).Entity);
        Assert.Equal("prior-removal", GetLastHistoryRemovalMessage(websocketManager));
    }

    [Fact]
    public async Task ControllerOwnedRequestedConcurrencyWithActiveReceiptPropagatesWithoutBroadcast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            setup.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyId,
                DeleteMountedFiles = true
            });
            await setup.SaveChangesAsync();
        }

        var websocketManager = new WebsocketManager();
        await websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, "prior-removal");
        await using var controllerContext = new DavDatabaseContext(options);
        var staleHistory = CreateCompletedHistory(historyId);
        controllerContext.Attach(staleHistory);
        controllerContext.Remove(staleHistory);
        var request = await CreateRequestAsync(historyId, CancellationToken.None);
        var controller = CreateController(controllerContext, websocketManager);

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => controller.RemoveFromHistory(request));

        Assert.IsType<HistoryItem>(Assert.Single(exception.Entries).Entity);
        Assert.Equal("prior-removal", GetLastHistoryRemovalMessage(websocketManager));
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
    }

    [Fact]
    public async Task CallerOwnedTransactionWithoutSavepointsFailsBeforeMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setup = new DavDatabaseContext(baseOptions))
            await setup.Database.EnsureCreatedAsync();

        var mutationCounter = new MutationCommandCounter();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .ReplaceService<IDbContextTransactionManager, NoSavepointTransactionManager>()
            .AddInterceptors(mutationCounter)
            .Options;
        await using var callerContext = new DavDatabaseContext(options);
        var manager = Assert.IsType<NoSavepointTransactionManager>(
            callerContext.GetService<IDbContextTransactionManager>());
        var request = await CreateRequestAsync(Guid.NewGuid(), CancellationToken.None);
        var controller = CreateController(callerContext, new WebsocketManager());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.RemoveFromHistory(request));

        Assert.Equal(
            "Removing history inside a caller-owned transaction requires savepoint support.",
            exception.Message);
        Assert.Equal(0, manager.Transaction.CreateSavepointCalls);
        Assert.Equal(0, mutationCounter.Count);
    }

    [Fact]
    public async Task CallerOwnedConcurrencyFailurePropagatesAndRollsBackBeforeOuterCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        var unrelatedHistoryId = Guid.NewGuid();
        await SeedAsync(baseOptions, historyId, davItemId, unrelatedHistoryId);

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailFirstSaveInterceptor(
                new DbUpdateConcurrencyException("forced caller-owned concurrency failure")))
            .Options;
        var websocketManager = new WebsocketManager();
        await using (var callerContext = new DavDatabaseContext(failingOptions))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var callerConfig = await callerContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer");
            callerConfig.ConfigValue = "after";
            var callerHistory = await callerContext.HistoryItems.SingleAsync(x => x.Id == unrelatedHistoryId);
            callerHistory.JobName = "unrelated-after";
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, websocketManager);

            var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => controller.RemoveFromHistory(request));

            Assert.Equal("forced caller-owned concurrency failure", exception.Message);
            Assert.Equal(EntityState.Modified, callerContext.Entry(callerConfig).State);
            Assert.Equal(
                [nameof(HistoryItem.JobName)],
                callerContext.Entry(callerHistory).Properties
                    .Where(x => x.IsModified)
                    .Select(x => x.Metadata.Name));
            Assert.False(WasHistoryRemovalBroadcast(websocketManager));
            await callerContext.SaveChangesAsync(CancellationToken.None);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await AssertOnlyCallerWorkCommittedAsync(baseOptions, historyId, davItemId, unrelatedHistoryId);
    }

    [Fact]
    public async Task CallerOwnedDatabaseFailureUsesCleanupTokenAndRollsBackBeforeOuterCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await SeedAsync(baseOptions, historyId, davItemId);

        using var requestCancellation = new CancellationTokenSource();
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailFirstSaveInterceptor(
                new DbUpdateException("forced caller-owned database failure"),
                requestCancellation))
            .Options;
        var websocketManager = new WebsocketManager();
        await using (var callerContext = new DavDatabaseContext(failingOptions))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var callerConfig = await callerContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer");
            callerConfig.ConfigValue = "after";
            var request = await CreateRequestAsync(historyId, requestCancellation.Token);
            var controller = CreateController(callerContext, websocketManager);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => controller.RemoveFromHistory(request));

            Assert.Equal("forced caller-owned database failure", exception.Message);
            Assert.True(requestCancellation.IsCancellationRequested);
            Assert.Equal(EntityState.Modified, callerContext.Entry(callerConfig).State);
            Assert.False(WasHistoryRemovalBroadcast(websocketManager));
            await callerContext.SaveChangesAsync(CancellationToken.None);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await AssertOnlyCallerWorkCommittedAsync(baseOptions, historyId, davItemId);
    }

    [Fact]
    public async Task CallerOwnedRollbackRestoresAddedDeletedAndPretrackedTargetReceipt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await SeedAsync(baseOptions, historyId, davItemId);

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailFirstSaveInterceptor(
                new DbUpdateConcurrencyException("forced caller-owned tracker restoration")))
            .Options;
        var websocketManager = new WebsocketManager();
        await using (var callerContext = new DavDatabaseContext(failingOptions))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var targetReceipt = await callerContext.ImportReceipts
                .SingleAsync(x => x.HistoryItemId == historyId);
            var deletedConfig = await callerContext.ConfigItems
                .SingleAsync(x => x.ConfigName == "test.outer");
            callerContext.Remove(deletedConfig);
            var addedConfig = new ConfigItem
            {
                ConfigName = "test.added",
                ConfigValue = "after"
            };
            callerContext.Add(addedConfig);
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, websocketManager);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => controller.RemoveFromHistory(request));

            var restoredReceiptEntry = Assert.Single(
                callerContext.ChangeTracker.Entries<ImportReceipt>(),
                x => x.Entity.HistoryItemId == historyId);
            Assert.Same(targetReceipt, restoredReceiptEntry.Entity);
            Assert.Equal(EntityState.Unchanged, restoredReceiptEntry.State);
            Assert.Equal(ImportReceiptState.Imported, restoredReceiptEntry.Entity.State);
            Assert.Equal(EntityState.Added, callerContext.Entry(addedConfig).State);
            Assert.Equal(EntityState.Deleted, callerContext.Entry(deletedConfig).State);
            Assert.False(WasHistoryRemovalBroadcast(websocketManager));

            await callerContext.SaveChangesAsync(CancellationToken.None);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
        Assert.Empty(await assertionContext.HistoryCleanupItems.ToListAsync());
        Assert.Null(await assertionContext.ConfigItems.SingleOrDefaultAsync(x => x.ConfigName == "test.outer"));
        Assert.Equal(
            "after",
            (await assertionContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.added")).ConfigValue);
    }

    [Fact]
    public async Task CallerOwnedSuccessPreservesUnrelatedPretrackedReceiptChange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await SeedAsync(options, historyId, davItemId);
        var durableUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var durableRemovedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string durableDetail = "durable removed detail";
        await using (var setup = new DavDatabaseContext(options))
        {
            var receipt = await setup.ImportReceipts.SingleAsync(x => x.HistoryItemId == historyId);
            receipt.State = ImportReceiptState.Removed;
            receipt.UpdatedAt = durableUpdatedAt;
            receipt.RemovedAt = durableRemovedAt;
            receipt.Detail = durableDetail;
            await setup.SaveChangesAsync();
        }

        var importedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        await using (var callerContext = new DavDatabaseContext(options))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var trackedReceipt = await callerContext.ImportReceipts
                .SingleAsync(x => x.HistoryItemId == historyId);
            trackedReceipt.ImportedAt = importedAt;
            trackedReceipt.State = ImportReceiptState.Imported;
            trackedReceipt.UpdatedAt = DateTimeOffset.UtcNow;
            trackedReceipt.RemovedAt = null;
            trackedReceipt.Detail = "stale caller terminal values";
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, new WebsocketManager());

            var response = await controller.RemoveFromHistory(request);

            Assert.True(response.Status);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        var durableReceipt = await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(ImportReceiptState.Removed, durableReceipt.State);
        Assert.Equal(durableUpdatedAt, durableReceipt.UpdatedAt);
        Assert.Equal(durableRemovedAt, durableReceipt.RemovedAt);
        Assert.Equal(durableDetail, durableReceipt.Detail);
        Assert.Equal(importedAt, durableReceipt.ImportedAt);
    }

    [Fact]
    public async Task CallerOwnedSuccessTerminalizesAndPersistsAddedTargetReceipt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryItems.Add(CreateCompletedHistory(historyId));
            await setup.SaveChangesAsync();
        }

        await using (var callerContext = new DavDatabaseContext(options))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            callerContext.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Available,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Detail = "pending caller receipt"
            });
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, new WebsocketManager());

            var response = await controller.RemoveFromHistory(request);

            Assert.True(response.Status);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Null(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        var durableReceipt = await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(ImportReceiptState.Removed, durableReceipt.State);
        Assert.NotNull(durableReceipt.RemovedAt);
        Assert.Null(durableReceipt.Detail);
    }

    [Fact]
    public async Task CallerOwnedDeletedTargetReceiptFailsWithoutRemovingDurableState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await SeedAsync(options, historyId, davItemId);

        await using (var callerContext = new DavDatabaseContext(options))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var trackedReceipt = await callerContext.ImportReceipts
                .SingleAsync(x => x.HistoryItemId == historyId);
            callerContext.Remove(trackedReceipt);
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, new WebsocketManager());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.RemoveFromHistory(request));

            Assert.Equal(
                "Cannot remove history while a matching import receipt is tracked as Deleted.",
                exception.Message);
            await outerTransaction.RollbackAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerOwnedModifiedTargetReceiptIdentityFailsWithoutMutation(bool changeHistoryItemId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await SeedAsync(options, historyId, davItemId);

        await using (var callerContext = new DavDatabaseContext(options))
        await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
        {
            var trackedReceipt = await callerContext.ImportReceipts
                .SingleAsync(x => x.HistoryItemId == historyId);
            if (changeHistoryItemId)
                trackedReceipt.HistoryItemId = Guid.NewGuid();
            else
                trackedReceipt.DavItemId = Guid.NewGuid();
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, new WebsocketManager());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.RemoveFromHistory(request));

            Assert.Equal(
                "Cannot remove history while a matching import receipt identity has pending changes.",
                exception.Message);
            await outerTransaction.RollbackAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        var durableReceipt = await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(historyId, durableReceipt.HistoryItemId);
        Assert.Equal(ImportReceiptState.Imported, durableReceipt.State);
    }

    [Fact]
    public async Task CallerOwnedMissingTrackedReceiptRollsBackBatchAndLeavesOuterTransactionUsable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var missingDavItemId = Guid.NewGuid();
        var survivingDavItemId = Guid.NewGuid();
        await SeedAsync(options, historyId, missingDavItemId);
        await using (var setup = new DavDatabaseContext(options))
        {
            setup.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = survivingDavItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await setup.SaveChangesAsync();
        }

        var websocketManager = new WebsocketManager();
        await using (var callerContext = new DavDatabaseContext(options))
        {
            _ = await callerContext.ImportReceipts
                .SingleAsync(x => x.DavItemId == missingDavItemId);
            await using (var deleteContext = new DavDatabaseContext(options))
            {
                var deletedReceipt = await deleteContext.ImportReceipts
                    .SingleAsync(x => x.DavItemId == missingDavItemId);
                deleteContext.Remove(deletedReceipt);
                await deleteContext.SaveChangesAsync();
            }

            await using var outerTransaction = await callerContext.Database.BeginTransactionAsync();
            var callerConfig = await callerContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer");
            callerConfig.ConfigValue = "after-missing-receipt";
            var request = await CreateRequestAsync(historyId, CancellationToken.None);
            var controller = CreateController(callerContext, websocketManager);

            var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => controller.RemoveFromHistory(request));

            Assert.Contains("disappeared during history removal", exception.Message);
            Assert.Empty(exception.Entries);
            Assert.False(WasHistoryRemovalBroadcast(websocketManager));
            await callerContext.SaveChangesAsync(CancellationToken.None);
            await outerTransaction.CommitAsync(CancellationToken.None);
        }

        await using var assertionContext = new DavDatabaseContext(options);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Null(await assertionContext.ImportReceipts.SingleOrDefaultAsync(x => x.DavItemId == missingDavItemId));
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == survivingDavItemId)).State);
        Assert.Equal(
            "after-missing-receipt",
            (await assertionContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer")).ConfigValue);
    }

    private static RemoveFromHistoryController CreateController(
        DavDatabaseContext dbContext,
        WebsocketManager websocketManager)
    {
        var httpContext = new DefaultHttpContext();
        return new RemoveFromHistoryController(
            httpContext,
            new DavDatabaseClient(dbContext),
            new ConfigManager(),
            websocketManager);
    }

    private static async Task<RemoveFromHistoryRequest> CreateRequestAsync(
        Guid historyId,
        CancellationToken cancellationToken)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?value={historyId}&del_completed_files=1");
        var request = await RemoveFromHistoryRequest.New(httpContext);
        var cancellationProperty = typeof(RemoveFromHistoryRequest).GetProperty(
            nameof(RemoveFromHistoryRequest.CancellationToken),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(cancellationProperty);
        cancellationProperty.GetSetMethod(nonPublic: true)!.Invoke(request, [cancellationToken]);
        return request;
    }

    private static async Task SeedAsync(
        DbContextOptions<DavDatabaseContext> options,
        Guid historyId,
        Guid davItemId,
        Guid? unrelatedHistoryId = null)
    {
        await using var setup = new DavDatabaseContext(options);
        await setup.Database.EnsureCreatedAsync();
        setup.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "caller-owned.nzb",
            JobName = "caller-owned",
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
        setup.ConfigItems.Add(new ConfigItem { ConfigName = "test.outer", ConfigValue = "before" });
        if (unrelatedHistoryId != null)
        {
            setup.HistoryItems.Add(new HistoryItem
            {
                Id = unrelatedHistoryId.Value,
                CreatedAt = DateTime.UtcNow,
                FileName = "unrelated.nzb",
                JobName = "unrelated-before",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 2048,
                DownloadTimeSeconds = 2
            });
        }
        await setup.SaveChangesAsync();
    }

    private static HistoryItem CreateCompletedHistory(Guid historyId) => new()
    {
        Id = historyId,
        CreatedAt = DateTime.UtcNow,
        FileName = "duplicate.nzb",
        JobName = "duplicate",
        Category = "movies",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1024,
        DownloadTimeSeconds = 1
    };

    private static async Task AssertOnlyCallerWorkCommittedAsync(
        DbContextOptions<DavDatabaseContext> options,
        Guid historyId,
        Guid davItemId,
        Guid? unrelatedHistoryId = null)
    {
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
        Assert.Empty(await assertionContext.HistoryCleanupItems.ToListAsync());
        Assert.Equal(
            "after",
            (await assertionContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer")).ConfigValue);
        if (unrelatedHistoryId != null)
        {
            Assert.Equal(
                "unrelated-after",
                (await assertionContext.HistoryItems.SingleAsync(x => x.Id == unrelatedHistoryId.Value)).JobName);
        }
    }

    private static bool WasHistoryRemovalBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemRemoved);
    }

    private static string? GetLastHistoryRemovalMessage(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.GetValueOrDefault(WebsocketTopic.HistoryItemRemoved);
    }

    private sealed class FailFirstSaveInterceptor(
        Exception exception,
        CancellationTokenSource? cancellation = null) : SaveChangesInterceptor
    {
        private int _saveAttempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveAttempts) != 1)
                return ValueTask.FromResult(result);

            cancellation?.Cancel();
            return ValueTask.FromException<InterceptionResult<int>>(exception);
        }
    }

    private sealed class MutationCommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoSavepointTransactionManager : IDbContextTransactionManager
    {
        public NoSavepointTransaction Transaction { get; } = new();
        public IDbContextTransaction? CurrentTransaction => Transaction;

        public IDbContextTransaction BeginTransaction() => throw new NotSupportedException();

        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void CommitTransaction() => throw new NotSupportedException();

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void RollbackTransaction() => throw new NotSupportedException();

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ResetState()
        {
        }

        public Task ResetStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoSavepointTransaction : IDbContextTransaction
    {
        public int CreateSavepointCalls { get; private set; }
        public Guid TransactionId { get; } = Guid.NewGuid();
        public bool SupportsSavepoints => false;

        public void Commit() => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Rollback() => throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void CreateSavepoint(string name)
        {
            CreateSavepointCalls++;
            throw new NotSupportedException();
        }

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
        {
            CreateSavepointCalls++;
            throw new NotSupportedException();
        }

        public void RollbackToSavepoint(string name) => throw new NotSupportedException();

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ReleaseSavepoint(string name) => throw new NotSupportedException();

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

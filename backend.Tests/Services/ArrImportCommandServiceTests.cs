using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ArrImportCommandServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ArrImportCommandServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
        // Most ARR routing tests are topology-neutral. DFS provides that baseline
        // without silently bypassing the startup whole-cache fence required by rclone.
        SelectMountWithoutRcloneRemoteControl("dfs");
    }

    [Fact]
    public async Task RunOnce_DfsMountReleasesCommandWithoutDeletingObsoleteRcloneFence()
    {
        var configManager = SelectMountWithoutRcloneRemoteControl("dfs");
        try
        {
            var client = new RecordingSonarrClient("http://sonarr.test");
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                var command = CreateCommand(history, ["/content/tv/Example"]);
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(command);
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv/Example",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                AddCorrelation(setup, history, client);
                await setup.SaveChangesAsync();
            }

            var service = new ArrImportCommandService(() => [client]);
            Assert.True(await service.RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
            Assert.Equal(1, client.RefreshCount);
            Assert.Single(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task RunOnce_DisabledRcPublishesHistoryWithoutWaitingForInvalidation()
    {
        _ = SelectMountWithoutRcloneRemoteControl("rclone");
        try
        {
            var client = new RecordingSonarrClient("http://sonarr.test");
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                var command = CreateCommand(history, ["/content/tv/Example"]);
                command.VisibleAt = null;
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(command);
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv/Example",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                AddCorrelation(setup, history, client);
                await setup.SaveChangesAsync();
            }

            var service = new ArrImportCommandService(() => [client]);
            Assert.True(await service.RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var published = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Pending, published.Status);
            Assert.NotNull(published.VisibleAt);
            Assert.False(RcloneClient.RequiresVfsVisibilityFence);
            Assert.Equal(0, client.RefreshCount);
            Assert.True(await HistoryVisibilityPolicy
                .VisibleToSab(
                    assertionContext.HistoryItems.AsNoTracking(),
                    assertionContext,
                    hasActiveRepairJobs: false)
                .AnyAsync());

            Assert.True(await service.RunOnceAsync());
            Assert.Equal(1, client.RefreshCount);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task RunOnce_WholeCacheFenceBlocksCommandWithNoRequiredPaths()
    {
        _ = SelectMountWithRcloneRemoteControl("rclone");
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);
        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            ArrImportCommandStatus.WaitingForInvalidation,
            (await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_DurableWholeCacheSentinelBlocksAllCommandsAfterMemoryFenceClears()
    {
        _ = SelectMountWithRcloneRemoteControl("rclone");
        AcknowledgeStartupWholeCacheFenceForPathOnlyTest();
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
            {
                Id = RcloneInvalidationItem.WholeCacheVisibilityFenceId,
                Path = RcloneInvalidationItem.WholeCacheVisibilityFencePath,
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);
        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            ArrImportCommandStatus.WaitingForInvalidation,
            (await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_DfsStagedCompletionThenRcloneFlipUsesWholeCacheSentinelAndBlocks()
    {
        var configManager = SelectMountWithRcloneRemoteControl("dfs");
        try
        {
            var client = new RecordingSonarrClient("http://sonarr.test");
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(CreateCommand(history, ["/content/tv/Example"]));
                setup.EnqueueRcloneVfsForgetPaths(["/content/tv/Example"]);
                AddCorrelation(setup, history, client);
                await setup.SaveChangesAsync();
            }

            configManager.UpdateValues([
                new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" }
            ]);
            var checks = 0;
            var service = new ArrImportCommandService(
                () => [client],
                wakeRecheckInvalidation: (_, _) =>
                {
                    Interlocked.Increment(ref checks);
                    return Task.FromResult(true);
                });

            Assert.True(await service.RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var waiting = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, waiting.Status);
            Assert.Equal(0, checks);
            Assert.Equal(0, client.RefreshCount);
            var fence = Assert.Single(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
            Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFencePath, fence.Path);
            Assert.True(RcloneClient.RequiresVfsVisibilityFence);
            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task RunOnce_RcloneActiveMountKeepsPendingPrerequisiteBlocked()
    {
        _ = SelectMountWithRcloneRemoteControl("rclone");
        AcknowledgeStartupWholeCacheFenceForPathOnlyTest();
        try
        {
            var client = new RecordingSonarrClient("http://sonarr.test");
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(CreateCommand(history, ["/content/tv/Example"]));
                AddCorrelation(setup, history, client);
                await setup.SaveChangesAsync();
            }

            var checks = 0;
            var service = new ArrImportCommandService(
                () => [client],
                wakeRecheckInvalidation: (_, _) =>
                {
                    Interlocked.Increment(ref checks);
                    return Task.FromResult(false);
                },
                evaluateVisibilityAndPublish: (_, _, _, _) =>
                {
                    Interlocked.Increment(ref checks);
                    return Task.FromResult(ArrVisibilityPublicationOutcome.Blocked);
                });

            Assert.True(await service.RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var waiting = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Pending, waiting.Status);
            Assert.Equal(2, checks);
            Assert.Equal(0, client.RefreshCount);
            Assert.True(RcloneClient.RequiresVfsVisibilityFence);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Theory]
    [InlineData("rclone", "dfs")]
    [InlineData("dfs", "rclone")]
    public async Task RunOnce_BlocksLiveTopologyChangeThroughVisibleAtDatabaseCommit(
        string activeMount,
        string changedMount)
    {
        var activeConfig = SelectMountWithRcloneRemoteControl(activeMount);
        if (activeMount == "rclone")
            AcknowledgeStartupWholeCacheFenceForPathOnlyTest();
        try
        {
            var client = new RecordingSonarrClient("http://sonarr.test");
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                var command = CreateCommand(history, []);
                command.VisibleAt = null;
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(command);
                AddCorrelation(setup, history, client);
                await setup.SaveChangesAsync();
            }

            var publisherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowPublisher = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long generationBeforeUpdate = -1;
            long generationAfterUpdate = -1;
            var service = new ArrImportCommandService(
                () => [client],
                evaluateVisibilityAndPublish: async (command, paths, fenceRequired, ct) =>
                {
                    generationBeforeUpdate = RcloneClient.VisibilityFenceGeneration;
                    publisherEntered.TrySetResult();
                    await allowPublisher.Task.WaitAsync(ct);
                    var outcome = await ArrImportCommandService
                        .EvaluateVisibilityAndPublishAsync(
                            command,
                            paths,
                            fenceRequired,
                            ct);
                    generationAfterUpdate = RcloneClient.VisibilityFenceGeneration;
                    return outcome;
                });
            var run = service.RunOnceAsync();
            await publisherEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var topologyChange = Task.Run(() => activeConfig.UpdateValues([
                new ConfigItem { ConfigName = "Mount:Type", ConfigValue = changedMount }
            ]));
            await Task.Delay(100);

            Assert.False(topologyChange.IsCompleted);
            allowPublisher.TrySetResult();
            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(2)));
            await topologyChange.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(generationBeforeUpdate, generationAfterUpdate);
            Assert.Equal(changedMount == "rclone", RcloneClient.RequiresVfsVisibilityFence);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var commandAfterUpdate = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.NotNull(commandAfterUpdate.VisibleAt);
            Assert.Equal(ArrImportCommandStatus.Pending, commandAfterUpdate.Status);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task RunOnce_HoldsTopologyThroughReadyDispatchAndLeaseTokenTerminalUpdate()
    {
        var activeConfig = SelectMountWithRcloneRemoteControl("dfs");
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            DispatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);
        var run = service.RunOnceAsync();
        await client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var topologyChange = Task.Run(() => activeConfig.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://replacement-rclone:5572" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "replacement:" }
        ]));
        await Task.Delay(100);
        Assert.False(topologyChange.IsCompleted);

        client.DispatchGate.TrySetResult();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(2)));
        await topologyChange.WaitAsync(TimeSpan.FromSeconds(2));

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, command.Status);
        Assert.Null(command.LeaseToken);
        Assert.NotNull(command.CompletedAt);
        Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
    }

    [Fact]
    public async Task SqliteVisibilityUnit_SerializesConcurrentInvalidationEnqueueAfterAbsenceRead()
    {
        _ = SelectMountWithRcloneRemoteControl("rclone");
        try
        {
            ArrImportCommand command;
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = CreateHistoryItem();
                command = CreateCommand(history, ["/content/tv/Example"]);
                command.VisibleAt = null;
                command.Status = ArrImportCommandStatus.Executing;
                command.LeaseToken = Guid.NewGuid();
                command.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(command);
                await setup.SaveChangesAsync();
            }

            var absenceRead = new PausingInvalidationReadInterceptor();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DavDatabaseContext.DatabaseFilePath,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 30
            }.ToString();
            DavDatabaseContext CreateVisibilityContext()
            {
                var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                    .UseSqlite(connectionString)
                    .AddInterceptors(absenceRead)
                    .Options;
                return new DavDatabaseContext(options);
            }

            var visibility = ArrImportCommandService.EvaluateVisibilityAndPublishAsync(
                command,
                ["/content/tv/Example"],
                fenceRequired: true,
                CancellationToken.None,
                CreateVisibilityContext);
            await absenceRead.Observed.WaitAsync(TimeSpan.FromSeconds(2));

            var enqueueAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueue = Task.Run(async () =>
            {
                await using var producer = new DavDatabaseContext();
                producer.EnqueueRcloneVfsForgetPaths(["/content/tv/Example"]);
                enqueueAttempted.TrySetResult();
                await producer.SaveChangesAsync();
            });
            await enqueueAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.False(enqueue.IsCompleted);

            absenceRead.Release();
            Assert.Equal(
                ArrVisibilityPublicationOutcome.Published,
                await visibility.WaitAsync(TimeSpan.FromSeconds(2)));
            await enqueue.WaitAsync(TimeSpan.FromSeconds(2));

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var published = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.NotNull(published.VisibleAt);
            Assert.Equal(ArrImportCommandStatus.Pending, published.Status);
            Assert.Single(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task SqliteVisibilityUnit_RecreatesWholeContextConnectionAndTransactionAfterBusy()
    {
        ArrImportCommand command;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            command = CreateCommand(history, ["/content/tv/Example"]);
            command.VisibleAt = null;
            command.Status = ArrImportCommandStatus.Executing;
            command.LeaseToken = Guid.NewGuid();
            command.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            await setup.SaveChangesAsync();
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DavDatabaseContext.DatabaseFilePath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
        var blockerOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var blockerContext = new DavDatabaseContext(blockerOptions);
        await using var blockerTransaction = await blockerContext.Database.BeginTransactionAsync();
        blockerContext.RcloneInvalidationItems.Add(new RcloneInvalidationItem
        {
            Id = Guid.NewGuid(),
            Path = "/content/tv/Example",
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        await blockerContext.SaveChangesAsync();
        var factoryCalls = 0;
        var contextIdentities = new System.Collections.Concurrent.ConcurrentBag<DavDatabaseContext>();
        DavDatabaseContext CreateContext()
        {
            Interlocked.Increment(ref factoryCalls);
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(connectionString)
                .Options;
            var context = new DavDatabaseContext(options);
            contextIdentities.Add(context);
            return context;
        }

        var visibility = ArrImportCommandService.EvaluateVisibilityAndPublishAsync(
            command,
            ["/content/tv/Example"],
            fenceRequired: true,
            CancellationToken.None,
            CreateContext);
        var retryDeadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (Volatile.Read(ref factoryCalls) < 2 && DateTimeOffset.UtcNow < retryDeadline)
            await Task.Delay(10);
        Assert.True(factoryCalls >= 2, $"Expected a fresh retry context; observed {factoryCalls} factory call(s).");

        await blockerTransaction.CommitAsync();
        Assert.Equal(
            ArrVisibilityPublicationOutcome.Blocked,
            await visibility.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(factoryCalls, contextIdentities.Distinct(ReferenceEqualityComparer.Instance).Count());
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Null((await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).VisibleAt);
    }

    [Fact]
    public async Task RunOnce_WaitsForFencedRcloneInvalidationThenDispatchesImmediately()
    {
        _ = SelectMountWithRcloneRemoteControl("rclone");
        AcknowledgeStartupWholeCacheFenceForPathOnlyTest();
        var client = new RecordingSonarrClient("http://sonarr.test");
        RcloneInvalidationItem invalidation;
        ArrImportCommand command;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            invalidation = new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/content/tv/Example",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            };
            command = CreateCommand(history, [invalidation.Path]);
            setup.HistoryItems.Add(history);
            setup.RcloneInvalidationItems.Add(invalidation);
            setup.ArrImportCommands.Add(command);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);
        Assert.True(await service.RunOnceAsync());

        await using (var waitingContext = await _fixture.CreateMigratedContextAsync())
        {
            var waiting = await waitingContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, waiting.Status);
            Assert.Equal(0, waiting.Attempts);
            Assert.Equal(0, client.RefreshCount);
            Assert.Equal(
                1,
                await RcloneInvalidationService.DeleteConfirmedItemsAsync(
                    waitingContext,
                    [invalidation],
                    CancellationToken.None));
        }

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, dispatched.Attempts);
        Assert.NotNull(dispatched.CompletedAt);
        Assert.Null(dispatched.LeaseToken);
        Assert.Equal(1, client.RefreshCount);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal(1, result.CommandId);
    }

    [Fact]
    public async Task RunOnce_RetriesFailedDispatchWithoutRepeatingAcceptedTargets()
    {
        var first = new RecordingSonarrClient("http://sonarr-one.test");
        var second = new RecordingSonarrClient("http://sonarr-two.test") { ShouldFail = true };
        ArrImportCommand command;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            command = CreateCommand(history, []);
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, first);
            AddCorrelation(setup, history, second);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [first, second]);
        Assert.True(await service.RunOnceAsync());

        await using (var retryContext = await _fixture.CreateMigratedContextAsync())
        {
            var retry = await retryContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
            Assert.Equal(1, retry.Attempts);
            Assert.Equal(1, first.RefreshCount);
            Assert.Equal(1, second.RefreshCount);
            Assert.NotNull(retry.LastError);
            await retryContext.ArrImportCommands
                .Where(x => x.Id == retry.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow));
        }

        second.ShouldFail = false;
        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(2, dispatched.Attempts);
        Assert.Equal(1, first.RefreshCount);
        Assert.Equal(2, second.RefreshCount);
        Assert.Equal(2, JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!.Length);
    }

    [Fact]
    public async Task RunOnce_SaturatesAttemptCounterInsteadOfPoisoningTheWorker()
    {
        var failing = new RecordingSonarrClient("http://sonarr.test") { ShouldFail = true };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            command.Attempts = int.MaxValue;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [failing]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Equal(int.MaxValue, retry.Attempts);
        Assert.Null(retry.LeaseToken);
    }

    [Fact]
    public async Task RunOnce_UsesCorrelatedInstanceInsteadOfBroadcasting()
    {
        var selected = new RecordingSonarrClient("http://selected-sonarr.test");
        var unrelated = new RecordingSonarrClient("http://unrelated-sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            setup.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                HistoryItemId = history.Id,
                ArrApp = "sonarr",
                InstanceKey = GetInstanceKey("sonarr", selected.Host),
                InstanceHost = selected.Host,
                Source = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [selected, unrelated]);
        Assert.True(await service.RunOnceAsync());

        Assert.Equal(1, selected.RefreshCount);
        Assert.Equal(0, unrelated.RefreshCount);
        Assert.Equal(0, selected.DownloadClientProbeCount);
        Assert.Equal(0, unrelated.DownloadClientProbeCount);
    }

    [Fact]
    public async Task RunOnce_MissingCorrelatedInstanceRetriesWithoutBroadcasting()
    {
        var configured = new RecordingSonarrClient("http://configured-sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, configured);
            setup.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                HistoryItemId = history.Id,
                ArrApp = "sonarr",
                InstanceKey = GetInstanceKey("sonarr", "http://missing-sonarr.test"),
                InstanceHost = "http://missing-sonarr.test",
                Source = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [configured]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Null(retry.CompletedAt);
        Assert.Null(retry.LeaseToken);
        Assert.Contains("correlated", retry.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, configured.RefreshCount);
        Assert.Equal(0, configured.DownloadClientProbeCount);
    }

    [Fact]
    public async Task RunOnce_WithoutCorrelationDispatchesOnlyUniqueCategoryOwner()
    {
        var owner = new RecordingSonarrClient("http://tv-sonarr.test");
        var unrelated = new RecordingSonarrClient("http://movie-sonarr.test")
        {
            DownloadCategories = ["movies"]
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [owner, unrelated]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, owner.RefreshCount);
        Assert.Equal(0, unrelated.RefreshCount);
        Assert.Equal(1, owner.DownloadClientProbeCount);
        Assert.Equal(1, unrelated.DownloadClientProbeCount);
    }

    [Fact]
    public async Task RunOnce_CategoryOwnershipAndRefreshShareSingleTargetDeadline()
    {
        var requestTimeout = TimeSpan.FromMilliseconds(500);
        var schedulingAllowance = TimeSpan.FromMilliseconds(100);
        var owner = new RecordingSonarrClient("http://tv-sonarr.test")
        {
            DownloadClientDelay = TimeSpan.FromMilliseconds(350),
            BlockDispatchUntilCancellation = true
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            await setup.SaveChangesAsync();
        }

        Assert.True(await new ArrImportCommandService(
            () => [owner],
            requestTimeout).RunOnceAsync());

        Assert.Equal(1, owner.DownloadClientProbeCount);
        Assert.Equal(1, owner.RefreshCount);
        var networkElapsed = Stopwatch.GetElapsedTime(
            Assert.IsType<long>(owner.DownloadClientProbeStartedAt),
            Assert.IsType<long>(owner.RefreshAttemptFinishedAt));
        Assert.True(
            networkElapsed <= requestTimeout + schedulingAllowance,
            $"Ownership and refresh used {networkElapsed}; budget was {requestTimeout}.");
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Contains("refresh-timeout", retry.LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "route-category-owner-missing")]
    [InlineData(true, true, "route-category-owner-ambiguous")]
    public async Task RunOnce_WithoutCorrelationAndWithoutUniqueOwnerRetriesWithoutDispatch(
        bool firstOwnsCategory,
        bool secondOwnsCategory,
        string expectedError)
    {
        var first = new RecordingSonarrClient("http://sonarr-one.test")
        {
            DownloadCategories = [firstOwnsCategory ? "tv" : "movies"]
        };
        var second = new RecordingSonarrClient("http://sonarr-two.test")
        {
            DownloadCategories = [secondOwnsCategory ? "tv" : "movies"]
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [first, second]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Null(retry.CompletedAt);
        Assert.Contains(expectedError, retry.LastError, StringComparison.Ordinal);
        Assert.Equal(0, first.RefreshCount);
        Assert.Equal(0, second.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_NoConfiguredInstancesTerminalizesAsNoRouteWithoutPollingForever()
    {
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => []);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var noRoute = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.NoRoute, noRoute.Status);
        Assert.Equal(1, noRoute.Attempts);
        Assert.NotNull(noRoute.CompletedAt);
        Assert.Equal("route-no-configured-instances", noRoute.LastError);

        Assert.False(await service.RunOnceAsync());
        var unchanged = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(1, unchanged.Attempts);
    }

    [Fact]
    public async Task RunOnce_RequeuesNoRouteWhenArrConfigurationBecomesAvailable()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        IReadOnlyCollection<ArrClient> configuredClients = [];
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            await setup.SaveChangesAsync();
        }
        var service = new ArrImportCommandService(() => configuredClients);

        Assert.True(await service.RunOnceAsync());
        Assert.False(await service.RunOnceAsync());

        configuredClients = [client];
        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_RetriesNoRouteRequeueAfterFirstDatabaseFailure()
    {
        await using var setup = await _fixture.ResetAndCreateMigratedContextAsync();
        var client = new RecordingSonarrClient("http://sonarr.test");
        var requeueAttempts = 0;
        var service = new ArrImportCommandService(
            () => [client],
            requeueNoRoute: _ =>
            {
                if (Interlocked.Increment(ref requeueAttempts) == 1)
                    return Task.FromException(new DbUpdateException("forced requeue failure"));
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => service.RunOnceAsync());

        Assert.False(await service.RunOnceAsync());
        Assert.Equal(2, requeueAttempts);
    }

    [Fact]
    public async Task RunOnce_UsesOneArrSnapshotAcrossClientsEmptyClientsFlapWithoutStrandingNoRoute()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            command.Status = ArrImportCommandStatus.NoRoute;
            command.CompletedAt = DateTimeOffset.UtcNow;
            command.LastError = "No configured ARR instances are available for import refresh.";
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        IReadOnlyList<IReadOnlyCollection<ArrClient>> snapshots =
        [
            [client],
            [],
            [client]
        ];
        var snapshotCalls = 0;
        var service = new ArrImportCommandService(() =>
        {
            var index = Interlocked.Increment(ref snapshotCalls) - 1;
            return snapshots[Math.Min(index, snapshots.Count - 1)];
        });

        Assert.True(await service.RunOnceAsync());
        Assert.False(await service.RunOnceAsync());
        Assert.False(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var commandAfterFlap = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, commandAfterFlap.Status);
        Assert.Equal(1, client.RefreshCount);
        Assert.Equal(3, snapshotCalls);
    }

    [Fact]
    public async Task RunOnce_MalformedSiblingResponseKeepsSuccessfulDispatchForRetry()
    {
        var successful = new RecordingSonarrClient("http://sonarr-one.test");
        var malformed = new RecordingSonarrClient("http://sonarr-two.test")
        {
            DispatchException = new JsonException("malformed ARR response")
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, successful);
            AddCorrelation(setup, history, malformed);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [successful, malformed]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Contains("refresh-malformed", retry.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed ARR response", retry.LastError, StringComparison.Ordinal);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(retry.ResultsJson)!);
        Assert.Equal(GetInstanceKey("sonarr", successful.Host), result.InstanceKey);
    }

    [Fact]
    public async Task RunOnce_MalformedSiblingCommandKeepsSuccessfulDispatchForRetry()
    {
        var successful = new RecordingSonarrClient("http://sonarr-one.test");
        var malformed = new RecordingSonarrClient("http://sonarr-two.test")
        {
            ReturnMalformedCommand = true
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, successful);
            AddCorrelation(setup, history, malformed);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [successful, malformed]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Contains("invalid-command", retry.LastError, StringComparison.Ordinal);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(retry.ResultsJson)!);
        Assert.Equal(GetInstanceKey("sonarr", successful.Host), result.InstanceKey);
    }

    [Fact]
    public async Task RunOnce_BoundsArrHttpDispatchWithinLatencyBudget()
    {
        var blocking = new RecordingSonarrClient("http://blocking-sonarr.test")
        {
            BlockDispatchUntilCancellation = true
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, blocking);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [blocking], TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        Assert.True(await service.RunOnceAsync());

        stopwatch.Stop();
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Null(retry.LeaseToken);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Dispatch took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunOnce_CancellationRequeuesClaimInsteadOfLeavingLongLease()
    {
        var blocking = new RecordingSonarrClient("http://blocking-sonarr.test")
        {
            BlockDispatchUntilCancellation = true
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, blocking);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [blocking], TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(cts.Token));

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var pending = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Pending, pending.Status);
        Assert.Null(pending.LeaseToken);
        Assert.Null(pending.LeaseExpiresAt);
        Assert.Equal(0, pending.Attempts);
    }

    [Fact]
    public async Task RunOnce_DoesNotDispatchWhenVisibleClaimIsQuarantinedBeforeTransactionalAuthentication()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            evaluateVisibilityAndPublish: async (claimed, paths, fenceRequired, ct) =>
            {
                await using (var quarantine = await _fixture.CreateMigratedContextAsync())
                {
                    await quarantine.ArrImportCommands
                        .Where(command => command.Id == claimed.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(command => command.Status, ArrImportCommandStatus.Quarantined)
                            .SetProperty(command => command.LeaseToken, (Guid?)null)
                            .SetProperty(command => command.LeaseExpiresAt, (DateTimeOffset?)null)
                            .SetProperty(command => command.LastError, "verification quarantined"), ct);
                }

                return await ArrImportCommandService.EvaluateVisibilityAndPublishAsync(
                    claimed,
                    paths,
                    fenceRequired,
                    ct);
            });

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var quarantined = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Quarantined, quarantined.Status);
        Assert.Null(quarantined.LeaseToken);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_RechecksFenceAfterWaitingReleaseToAvoidLostWake()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        var fenceChecks = 0;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, ["/content/tv/Example"]));
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            wakeRecheckInvalidation: (_, _) =>
            {
                Interlocked.Increment(ref fenceChecks);
                return Task.FromResult(false);
            },
            evaluateVisibilityAndPublish: (_, _, _, _) => Task.FromResult(
                Interlocked.Increment(ref fenceChecks) == 1
                    ? ArrVisibilityPublicationOutcome.Blocked
                    : ArrVisibilityPublicationOutcome.Ready));

        Assert.True(await service.RunOnceAsync());

        await using (var waitingContext = await _fixture.CreateMigratedContextAsync())
        {
            var ready = await waitingContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Pending, ready.Status);
            Assert.True(ready.NextAttemptAt <= DateTimeOffset.UtcNow);
            Assert.Equal(0, client.RefreshCount);
        }

        Assert.True(await service.RunOnceAsync());
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_PublishesSabVisibilityBeforeArrPostAndPreservesItAcrossRetry()
    {
        var client = new RecordingSonarrClient("http://sonarr.test") { ShouldFail = true };
        var websocketManager = new WebsocketManager();
        var visibilityNotifier = new HistoryVisibilityNotifier(
            _fixture.CreateConfigManager(),
            websocketManager);
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            command.VisibleAt = null;
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            historyVisibilityNotifier: visibilityNotifier);

        Assert.True(await service.RunOnceAsync());

        DateTimeOffset visibleAt;
        await using (var visibleContext = await _fixture.CreateMigratedContextAsync())
        {
            var visible = await visibleContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == commandId);
            visibleAt = Assert.IsType<DateTimeOffset>(visible.VisibleAt);
            Assert.Equal(ArrImportCommandStatus.Pending, visible.Status);
            Assert.Equal(0, visible.Attempts);
            Assert.Null(visible.LeaseToken);
            Assert.Equal(0, client.RefreshCount);
            Assert.True(WasHistoryAdditionBroadcast(websocketManager));
        }

        Assert.True(await service.RunOnceAsync());

        await using var retryContext = await _fixture.CreateMigratedContextAsync();
        var retry = await retryContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == commandId);
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Equal(visibleAt, retry.VisibleAt);
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_RepublishesAlreadyVisibleHistoryBeforeDispatch()
    {
        var websocketManager = new WebsocketManager();
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            BeforeRefresh = () => Assert.True(WasHistoryAdditionBroadcast(websocketManager))
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            historyVisibilityNotifier: new HistoryVisibilityNotifier(
                _fixture.CreateConfigManager(),
                websocketManager));

        Assert.True(await service.RunOnceAsync());

        Assert.True(WasHistoryAdditionBroadcast(websocketManager));
        Assert.Equal(1, client.RefreshCount);
    }

    [Theory]
    [InlineData(WorkerJob.JobStatus.Pending)]
    [InlineData(WorkerJob.JobStatus.Leased)]
    [InlineData(WorkerJob.JobStatus.Retry)]
    public async Task RunOnce_DoesNotWaitForPostDownloadVerification(WorkerJob.JobStatus status)
    {
        const string downloadId = "verify-independent-download";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example")
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            history.DownloadDirId = Guid.NewGuid();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            setup.WorkerJobs.Add(new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = WorkerJob.JobKind.Verify,
                Status = status,
                TargetId = history.DownloadDirId.Value,
                Priority = 50,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                AvailableAt = DateTimeOffset.UtcNow,
                PayloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson()
            });
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, dispatched.Attempts);
        Assert.Null(dispatched.LeaseToken);
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_DoesNotWaitForUnrelatedVerifyPayloadContainingMarker()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            history.DownloadDirId = Guid.NewGuid();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            setup.WorkerJobs.Add(new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = WorkerJob.JobKind.Verify,
                Status = WorkerJob.JobStatus.Pending,
                TargetId = history.DownloadDirId.Value,
                Priority = 50,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                AvailableAt = DateTimeOffset.UtcNow,
                PayloadJson = """{"Kind":"not_post_download_verify"}"""
            });
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_CorrelatedSonarrUsesDirectScanAfterDurableVisibilityAndHistoryPublication()
    {
        const string downloadId = "sonarr-download-id";
        const string outputPath = @"Z:\remote\Example.S01E01";
        var websocketManager = new WebsocketManager();
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, outputPath, seriesId: 17, episodeId: 23),
            BeforeQueue = () => Assert.True(WasHistoryAdditionBroadcast(websocketManager))
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId, seriesId: 17, episodeId: 23);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            historyVisibilityNotifier: new HistoryVisibilityNotifier(
                _fixture.CreateConfigManager(),
                websocketManager));

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.NotNull(dispatched.VisibleAt);
        Assert.Equal(1, client.QueueProbeCount);
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
        Assert.Equal((outputPath, downloadId), client.LastDirectScan);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal("direct-scan", result.DispatchMode);
        Assert.Null(result.FallbackReasonCode);
        Assert.Equal("DownloadedEpisodesScan", result.CommandName);
        Assert.InRange(result.PublicationToAcceptMilliseconds ?? -1, 0, int.MaxValue);
    }

    [Fact]
    public async Task RunOnce_CorrelatedRadarrUsesTypedQueueAndDirectMovieScan()
    {
        const string downloadId = "radarr-download-id";
        const string outputPath = "/remote/movies/Example (2026)";
        var client = new RecordingRadarrClient("http://radarr.test")
        {
            RadarrQueue = CreateRadarrQueue(downloadId, outputPath, movieId: 41)
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            history.Category = "movies";
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId, movieId: 41);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(() => [client]);

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, client.QueueProbeCount);
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
        Assert.Equal((outputPath, downloadId), client.LastDirectScan);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal("direct-scan", result.DispatchMode);
        Assert.Equal("DownloadedMoviesScan", result.CommandName);
    }

    [Fact]
    public async Task RunOnce_PolicyRefusalSkipsDirectScanAndUsesOneRefresh()
    {
        const string downloadId = "not-present-in-queue";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue("different-id", "/remote/tv/Example")
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, client.QueueProbeCount);
        Assert.Equal(0, client.DirectScanCount);
        Assert.Equal(1, client.RefreshCount);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal("refresh-fallback", result.DispatchMode);
        Assert.Equal("queue-match-missing", result.FallbackReasonCode);
        Assert.Equal("RefreshMonitoredDownloads", result.CommandName);
    }

    [Fact]
    public async Task RunOnce_MalformedTypedQueueUsesRefreshFallback()
    {
        const string downloadId = "download-id";
        var malformedQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example");
        malformedQueue.Page = 0;
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = malformedQueue
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal("refresh-fallback", result.DispatchMode);
        Assert.Equal("queue-malformed", result.FallbackReasonCode);
        Assert.Equal(0, client.DirectScanCount);
        Assert.Equal(1, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_QueueExceptionIsSanitizedAndUsesRefreshFallback()
    {
        const string secret = "Z:/secret/title api-key=top-secret http://user:pass@arr.test/download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            QueueException = new HttpRequestException(secret)
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, "download-id");
            await setup.SaveChangesAsync();
        }

        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        try
        {
            Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
            Assert.DoesNotContain(secret, dispatched.ResultsJson, StringComparison.Ordinal);
            Assert.Null(dispatched.LastError);
            var result = Assert.Single(
                JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
            Assert.Equal("refresh-fallback", result.DispatchMode);
            Assert.Equal("queue-http", result.FallbackReasonCode);
            Assert.Equal(1, client.RefreshCount);
            Assert.DoesNotContain(secret, sink.Rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_FailedDirectScanUsesRefreshFallback()
    {
        const string downloadId = "download-id";
        const string secret = "private-path api-key=direct-secret http://user:pass@arr.test";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example"),
            DirectScanException = new HttpRequestException(secret)
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }

        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        try
        {
            Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
            Assert.Null(dispatched.LastError);
            Assert.Equal(1, client.DirectScanCount);
            Assert.Equal(1, client.RefreshCount);
            var result = Assert.Single(
                JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
            Assert.Equal("refresh-fallback", result.DispatchMode);
            Assert.Equal("direct-http", result.FallbackReasonCode);
            Assert.DoesNotContain(secret, dispatched.ResultsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, sink.Rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_DirectAndFallbackFailureRetriesWithOnlyStableCodes()
    {
        const string directSecret = "download-id /remote/direct api-key=direct-secret";
        const string fallbackSecret = "http://user:pass@arr.test api-key=fallback-secret";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue("download-id", "/remote/tv/Example"),
            DirectScanException = new HttpRequestException(directSecret),
            DispatchException = new HttpRequestException(fallbackSecret)
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, "download-id");
            await setup.SaveChangesAsync();
        }

        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        try
        {
            Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
            Assert.Empty(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(retry.ResultsJson)!);
            Assert.Contains("refresh-http", retry.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain(directSecret, retry.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain(directSecret, retry.ResultsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(fallbackSecret, retry.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain(fallbackSecret, retry.ResultsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(directSecret, sink.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(fallbackSecret, sink.Rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_VisibilityInfrastructureFailurePersistsOnlyStableWorkerCode()
    {
        const string secret =
            "/private/media/Example download-id=deadbeef api-key=top-secret http://user:pass@arr.internal";
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            evaluateVisibilityAndPublish: (_, _, _, _) =>
                Task.FromException<ArrVisibilityPublicationOutcome>(new IOException(secret)));

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Equal("worker-error", retry.LastError);
        Assert.InRange(retry.LastError!.Length, 1, 64);
        Assert.DoesNotContain(secret, retry.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, retry.ResultsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundWorker_GetArrClientsFailureLogsStableCodeAndStopsCleanly()
    {
        const string secret =
            "/private/media/Example download-id=deadbeef api-key=top-secret http://user:pass@arr.internal";
        using var service = new ArrImportCommandService(
            () => throw new InvalidOperationException(secret));
        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        try
        {
            await service.StartAsync(CancellationToken.None);
            await sink.FirstEmission.WaitAsync(TimeSpan.FromSeconds(2));
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StopAsync(stopCts.Token);

            var workerEvents = sink.Events.Where(logEvent =>
                    logEvent.MessageTemplate.Text.Contains(
                        "ARR import command worker failed",
                        StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(workerEvents);
            Assert.All(workerEvents, workerEvent =>
            {
                Assert.Equal(LogEventLevel.Error, workerEvent.Level);
                Assert.Null(workerEvent.Exception);
                Assert.Equal(
                    "worker-error",
                    Assert.IsType<ScalarValue>(workerEvent.Properties["ErrorCode"]).Value);
            });
            Assert.All(sink.Events, logEvent =>
            {
                Assert.DoesNotContain(secret, RenderLogEvent(logEvent), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "InvalidOperationException",
                    RenderLogEvent(logEvent),
                    StringComparison.Ordinal);
            });
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_HistoryVisibilityFailureLogsStableCodeWithHistoryId()
    {
        const string secret =
            "/private/media/Example download-id=deadbeef api-key=top-secret http://user:pass@arr.internal";
        var client = new RecordingSonarrClient("http://sonarr.test");
        Guid historyItemId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            historyItemId = history.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        try
        {
            var service = new ArrImportCommandService(
                () => [client],
                historyVisibilityNotifier: new ThrowingHistoryVisibilityNotifier(
                    _fixture.CreateConfigManager(),
                    new WebsocketManager(),
                    new InvalidOperationException(secret)));

            Assert.True(await service.RunOnceAsync());

            var visibilityEvent = Assert.Single(sink.Events, logEvent =>
                logEvent.MessageTemplate.Text.Contains(
                    "Could not publish history visibility for ARR import",
                    StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Warning, visibilityEvent.Level);
            Assert.Null(visibilityEvent.Exception);
            Assert.Equal(
                historyItemId,
                Assert.IsType<ScalarValue>(visibilityEvent.Properties["HistoryItemId"]).Value);
            Assert.Equal(
                "visibility-error",
                Assert.IsType<ScalarValue>(visibilityEvent.Properties["ErrorCode"]).Value);
            Assert.DoesNotContain(secret, RenderLogEvent(visibilityEvent), StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", RenderLogEvent(visibilityEvent), StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_CancelledCommandRequeueFailureLogsStableCodeWithCommandId()
    {
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            BlockDispatchUntilCancellation = true
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var sink = new CollectingLogSink();
        var previousLogger = Log.Logger;
        using var testLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = testLogger;
        using var cts = new CancellationTokenSource();
        try
        {
            var service = new ArrImportCommandService(() => [client], TimeSpan.FromSeconds(5));
            var run = service.RunOnceAsync(cts.Token);
            await client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", "unsupported-for-test");
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            var requeueEvent = Assert.Single(sink.Events, logEvent =>
                logEvent.MessageTemplate.Text.Contains(
                    "Could not requeue cancelled ARR import command",
                    StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Warning, requeueEvent.Level);
            Assert.Null(requeueEvent.Exception);
            Assert.Equal(
                commandId,
                Assert.IsType<ScalarValue>(requeueEvent.Properties["CommandId"]).Value);
            Assert.Equal(
                "cancel-requeue-error",
                Assert.IsType<ScalarValue>(requeueEvent.Properties["ErrorCode"]).Value);
            Assert.DoesNotContain(
                "Unsupported database provider",
                RenderLogEvent(requeueEvent),
                StringComparison.Ordinal);
        }
        finally
        {
            _fixture.RestoreEnvironment();
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task RunOnce_OldFourPropertyResultSuppressesReplay()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            command.ResultsJson = $$"""
                [{"App":"sonarr","InstanceKey":"{{GetInstanceKey("sonarr", client.Host)}}","CommandId":7,"AcceptedAt":"2026-07-13T00:00:00+00:00"}]
                """;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client, "download-id");
            await setup.SaveChangesAsync();
        }

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        Assert.Equal(0, client.QueueProbeCount);
        Assert.Equal(0, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal(7, result.CommandId);
        Assert.Null(result.DispatchMode);
    }

    [Fact]
    public async Task RunOnce_QuarantineBetweenQueueAndDirectSuppressesAllFurtherCalls()
    {
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example")
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }
        client.AfterQueue = async _ => await QuarantineAsync(commandId);

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var quarantined = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Quarantined, quarantined.Status);
        Assert.Equal(1, client.QueueProbeCount);
        Assert.Equal(0, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_FinalAuthorizationRejectsQuarantineEvenWhenVisibilityReportsReady()
    {
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example")
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }
        var service = new ArrImportCommandService(
            () => [client],
            evaluateVisibilityAndPublish: async (claimed, _, _, _) =>
            {
                await QuarantineAsync(claimed.Id);
                return ArrVisibilityPublicationOutcome.Ready;
            });

        Assert.True(await service.RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            ArrImportCommandStatus.Quarantined,
            (await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, client.QueueProbeCount);
        Assert.Equal(0, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_QuarantineAfterFailedDirectSuppressesFallback()
    {
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example"),
            DirectScanException = new HttpRequestException("direct failure")
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }
        client.AfterDirectScan = async _ => await QuarantineAsync(commandId);

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var quarantined = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Quarantined, quarantined.Status);
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_ExpiredLeaseBeforeRefreshSuppressesNetworkCall()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client);
            await setup.SaveChangesAsync();
        }

        var service = new ArrImportCommandService(
            () => [client],
            evaluateVisibilityAndPublish: async (claimed, _, _, ct) =>
            {
                await using var context = await _fixture.CreateMigratedContextAsync();
                await context.ArrImportCommands
                    .Where(command => command.Id == claimed.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            command => command.LeaseExpiresAt,
                            DateTimeOffset.UtcNow.AddMilliseconds(-1)),
                        ct);
                return ArrVisibilityPublicationOutcome.Ready;
            });

        Assert.True(await service.RunOnceAsync());

        Assert.Equal(0, client.RefreshCount);
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var retry = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Retry, retry.Status);
        Assert.Null(retry.LeaseToken);
    }

    [Fact]
    public async Task RunOnce_DoesNotHoldDatabaseLockAcrossDirectHttp()
    {
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example"),
            DirectScanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }

        var run = new ArrImportCommandService(() => [client], TimeSpan.FromSeconds(2)).RunOnceAsync();
        await client.DirectScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await using (var concurrent = await _fixture.CreateMigratedContextAsync())
        {
            var changed = await concurrent.ArrImportCommands
                .Where(command => command.Id == commandId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(command => command.LastError, "concurrent-write"))
                .WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(1, changed);
        }
        client.DirectScanGate.TrySetResult();

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RunOnce_NearHalfDeadlineDirectFailureStillLeavesSecondSliceForRefresh()
    {
        var requestTimeout = TimeSpan.FromMilliseconds(600);
        var schedulingAllowance = TimeSpan.FromMilliseconds(100);
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example"),
            QueueDelay = TimeSpan.FromMilliseconds(240),
            RefreshDelay = TimeSpan.FromMilliseconds(220),
            DirectScanException = new HttpRequestException("direct failure")
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }
        var totalStopwatch = Stopwatch.StartNew();

        Assert.True(await new ArrImportCommandService(
            () => [client],
            requestTimeout).RunOnceAsync());

        totalStopwatch.Stop();
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(1, client.RefreshCount);
        var networkElapsed = Stopwatch.GetElapsedTime(
            Assert.IsType<long>(client.QueueProbeStartedAt),
            Assert.IsType<long>(client.RefreshAttemptFinishedAt));
        Assert.True(
            networkElapsed <= requestTimeout + schedulingAllowance,
            $"Direct attempt and reserved refresh used {networkElapsed}; budget was {requestTimeout}.");
        Assert.True(
            totalStopwatch.Elapsed <= requestTimeout + schedulingAllowance,
            $"RunOnce used {totalStopwatch.Elapsed}; request budget was {requestTimeout}.");
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var dispatched = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Dispatched, dispatched.Status);
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(dispatched.ResultsJson)!);
        Assert.Equal("refresh-fallback", result.DispatchMode);
        Assert.Equal("direct-http", result.FallbackReasonCode);
    }

    [Fact]
    public async Task RunOnce_LidarrCorrelationRemainsRefreshOnly()
    {
        var client = new RecordingLidarrClient("http://lidarr.test");
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            history.Category = "music";
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(CreateCommand(history, []));
            setup.ArrDownloadCorrelations.Add(CreateCorrelation(
                history,
                "lidarr",
                client.Host,
                "download-id"));
            await setup.SaveChangesAsync();
        }

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        Assert.Equal(1, client.RefreshCount);
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(
            (await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).ResultsJson)!);
        Assert.Equal("refresh-only", result.DispatchMode);
        Assert.Equal("unsupported-target", result.FallbackReasonCode);
    }

    [Fact]
    public async Task RunOnce_ExternalCancellationPreservesAcceptedSiblingAndSuppressesReplay()
    {
        var accepted = new RecordingSonarrClient("http://accepted-sonarr.test");
        var cancelled = new RecordingSonarrClient("http://cancelled-sonarr.test")
        {
            BlockDispatchUntilCancellation = true
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, accepted);
            AddCorrelation(setup, history, cancelled);
            await setup.SaveChangesAsync();
        }
        var service = new ArrImportCommandService(() => [accepted, cancelled], TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var firstRun = service.RunOnceAsync(cts.Token);
        await cancelled.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);

        await using (var pendingContext = await _fixture.CreateMigratedContextAsync())
        {
            var pending = await pendingContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(ArrImportCommandStatus.Pending, pending.Status);
            var result = Assert.Single(JsonSerializer.Deserialize<ArrImportDispatchResult[]>(pending.ResultsJson)!);
            Assert.Equal(GetInstanceKey("sonarr", accepted.Host), result.InstanceKey);
            Assert.Equal(1, accepted.RefreshCount);
            Assert.Equal(1, cancelled.RefreshCount);
            await pendingContext.ArrImportCommands
                .Where(command => command.Id == commandId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    command => command.NextAttemptAt,
                    DateTimeOffset.UtcNow));
        }

        cancelled.BlockDispatchUntilCancellation = false;
        Assert.True(await service.RunOnceAsync());

        Assert.Equal(1, accepted.RefreshCount);
        Assert.Equal(2, cancelled.RefreshCount);
    }

    [Fact]
    public async Task RunOnce_QuarantineDuringSuccessfulDirectFencesStaleFinalize()
    {
        const string downloadId = "download-id";
        var client = new RecordingSonarrClient("http://sonarr.test")
        {
            SonarrQueue = CreateSonarrQueue(downloadId, "/remote/tv/Example")
        };
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var history = CreateHistoryItem();
            var command = CreateCommand(history, []);
            commandId = command.Id;
            setup.HistoryItems.Add(history);
            setup.ArrImportCommands.Add(command);
            AddCorrelation(setup, history, client, downloadId);
            await setup.SaveChangesAsync();
        }
        client.AfterDirectScan = async _ => await QuarantineAsync(commandId);

        Assert.True(await new ArrImportCommandService(() => [client]).RunOnceAsync());

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var quarantined = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Quarantined, quarantined.Status);
        Assert.Equal("[]", quarantined.ResultsJson);
        Assert.Equal(1, client.DirectScanCount);
        Assert.Equal(0, client.RefreshCount);
    }

    [Fact]
    public async Task StageCompletionRefresh_IsDurableAndIdempotentBeforeSave()
    {
        var client = new RecordingSonarrClient("http://sonarr.test");
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var history = CreateHistoryItem();
        dbContext.HistoryItems.Add(history);
        var dbClient = new DavDatabaseClient(dbContext);
        var reports = new ArrDownloadReportService(
            _fixture.CreateConfigManager(),
            () => [client]);

        Assert.True(await reports.StageCompletionRefreshAsync(
            dbClient,
            history,
            ["/content/tv/Example", "/content/tv/Example"]));
        Assert.False(await reports.StageCompletionRefreshAsync(
            dbClient,
            history,
            ["/content/tv/Example"]));
        await using (var beforeSave = await _fixture.CreateMigratedContextAsync())
            Assert.Empty(await beforeSave.ArrImportCommands.ToListAsync());

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var command = await dbContext.ArrImportCommands.SingleAsync();
        Assert.Null(command.VisibleAt);
        var requiredPaths = JsonSerializer.Deserialize<string[]>(command.RequiredInvalidationPathsJson);
        Assert.NotNull(requiredPaths);
        Assert.Equal(["/content/tv/Example"], requiredPaths);
    }

    [Fact]
    public async Task StageCompletionRefresh_StagesVisibilityFenceWithoutConfiguredArrInstances()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var history = CreateHistoryItem();
        dbContext.HistoryItems.Add(history);
        var reports = new ArrDownloadReportService(
            _fixture.CreateConfigManager(),
            () => []);

        var staged = await reports.StageCompletionRefreshAsync(
            new DavDatabaseClient(dbContext),
            history,
            ["/content/tv/Example"]);
        await dbContext.SaveChangesAsync();

        Assert.True(staged);
        var command = await dbContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.Equal(ArrImportCommandStatus.Pending, command.Status);
        Assert.Null(command.VisibleAt);
        Assert.Null(command.CompletedAt);
        var requiredPaths = JsonSerializer.Deserialize<string[]>(command.RequiredInvalidationPathsJson);
        Assert.NotNull(requiredPaths);
        Assert.Equal(["/content/tv/Example"], requiredPaths);
    }

    [Fact]
    public async Task WakeSignalCoalescesConcurrentPulsesWithoutThrowing()
    {
        while (await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
        }

        using var start = new ManualResetEventSlim();
        var pulses = Enumerable.Range(0, 256)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                ArrImportCommandWakeSignal.Pulse();
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(pulses);

        Assert.True(await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    private static HistoryItem CreateHistoryItem() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.Now,
        FileName = "Example.nzb",
        JobName = "Example",
        Category = "tv",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1024,
        DownloadTimeSeconds = 1
    };

    private static bool WasHistoryAdditionBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemAdded);
    }

    private static string RenderLogEvent(LogEvent logEvent) => string.Join(
        " ",
        logEvent.RenderMessage(),
        logEvent.Exception?.ToString(),
        string.Join(" ", logEvent.Properties.Select(property => property.Value.ToString())));

    private static ConfigManager SelectMountWithoutRcloneRemoteControl(string mountType)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = mountType },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "false" }
        ]);
        RcloneClient.Initialize(configManager);
        return configManager;
    }

    private static ConfigManager SelectMountWithRcloneRemoteControl(string mountType)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = mountType },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(configManager);
        return configManager;
    }

    private static void AcknowledgeStartupWholeCacheFenceForPathOnlyTest()
    {
        Assert.True(RcloneClient.TryClearWholeCacheVisibilityFence(
            RcloneClient.VisibilityFenceGeneration));
    }

    private async Task QuarantineAsync(Guid commandId)
    {
        await using var quarantine = await _fixture.CreateMigratedContextAsync();
        await quarantine.ArrImportCommands
            .Where(command => command.Id == commandId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(command => command.Status, ArrImportCommandStatus.Quarantined)
                .SetProperty(command => command.LeaseToken, (Guid?)null)
                .SetProperty(command => command.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(command => command.LastError, "quarantined"));
    }

    private static ArrImportCommand CreateCommand(HistoryItem history, IReadOnlyCollection<string> paths)
    {
        var now = DateTimeOffset.UtcNow;
        return new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = history.Id,
            Category = history.Category,
            RequiredInvalidationPathsJson = JsonSerializer.Serialize(paths),
            Status = ArrImportCommandStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            VisibleAt = now,
        };
    }

    private static string GetInstanceKey(string app, string host)
    {
        host = host.Trim().TrimEnd('/').ToLowerInvariant();
        var raw = $"{app}:{host}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        return $"{app}:{hash}";
    }

    private static void AddCorrelation(
        DavDatabaseContext dbContext,
        HistoryItem history,
        RecordingSonarrClient client,
        string? downloadId = null,
        int? seriesId = null,
        int? episodeId = null)
    {
        var correlation = CreateCorrelation(history, "sonarr", client.Host, downloadId);
        correlation.SeriesId = seriesId;
        correlation.EpisodeId = episodeId;
        dbContext.ArrDownloadCorrelations.Add(correlation);
    }

    private static void AddCorrelation(
        DavDatabaseContext dbContext,
        HistoryItem history,
        RecordingRadarrClient client,
        string? downloadId = null,
        int? movieId = null)
    {
        var correlation = CreateCorrelation(history, "radarr", client.Host, downloadId);
        correlation.MovieId = movieId;
        dbContext.ArrDownloadCorrelations.Add(correlation);
    }

    private static ArrDownloadCorrelation CreateCorrelation(
        HistoryItem history,
        string app,
        string host,
        string? downloadId = null) => new()
    {
        Id = Guid.NewGuid(),
        HistoryItemId = history.Id,
        ArrApp = app,
        InstanceKey = GetInstanceKey(app, host),
        InstanceHost = host,
        DownloadId = downloadId,
        Source = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow
    };

    private static SonarrQueue CreateSonarrQueue(
        string downloadId,
        string outputPath,
        int? seriesId = null,
        int? episodeId = null) => new()
    {
        Page = 1,
        PageSize = 5000,
        TotalRecords = 1,
        Records =
        [
            new SonarrQueueRecord
            {
                Protocol = "usenet",
                DownloadId = downloadId,
                OutputPath = outputPath,
                SeriesId = seriesId,
                EpisodeId = episodeId
            }
        ]
    };

    private static RadarrQueue CreateRadarrQueue(
        string downloadId,
        string outputPath,
        int? movieId = null) => new()
    {
        Page = 1,
        PageSize = 5000,
        TotalRecords = 1,
        Records =
        [
            new RadarrQueueRecord
            {
                Protocol = "usenet",
                DownloadId = downloadId,
                OutputPath = outputPath,
                MovieId = movieId
            }
        ]
    };

    private sealed class RecordingSonarrClient(string host) : SonarrClient(host, "test-key")
    {
        public bool ShouldFail { get; set; }
        public Exception? DispatchException { get; set; }
        public bool ReturnMalformedCommand { get; set; }
        public bool BlockDispatchUntilCancellation { get; set; }
        public TaskCompletionSource DispatchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DispatchGate { get; set; }
        public Action? BeforeRefresh { get; set; }
        public Action? BeforeQueue { get; set; }
        public Func<CancellationToken, Task>? AfterQueue { get; set; }
        public Func<CancellationToken, Task>? AfterDirectScan { get; set; }
        public Exception? QueueException { get; set; }
        public Exception? DirectScanException { get; set; }
        public TimeSpan DownloadClientDelay { get; set; }
        public TimeSpan QueueDelay { get; set; }
        public TimeSpan RefreshDelay { get; set; }
        public SonarrQueue SonarrQueue { get; set; } = new()
        {
            Page = 1,
            PageSize = 5000,
            TotalRecords = 0,
            Records = []
        };
        public TaskCompletionSource DirectScanEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? DirectScanGate { get; set; }
        public IReadOnlyCollection<string> DownloadCategories { get; set; } = ["tv"];
        public int DownloadClientProbeCount { get; private set; }
        public int QueueProbeCount { get; private set; }
        public int DirectScanCount { get; private set; }
        public int RefreshCount { get; private set; }
        public (string Path, string DownloadId)? LastDirectScan { get; private set; }
        public long? DownloadClientProbeStartedAt { get; private set; }
        public long? QueueProbeStartedAt { get; private set; }
        public long? RefreshAttemptFinishedAt { get; private set; }

        public override async Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default)
        {
            DownloadClientProbeCount++;
            DownloadClientProbeStartedAt ??= Stopwatch.GetTimestamp();
            if (DownloadClientDelay > TimeSpan.Zero)
                await Task.Delay(DownloadClientDelay, ct);
            var clients = DownloadCategories.Select(category => JsonSerializer.Deserialize<ArrDownloadClient>(
                $$"""
                {
                  "enable": true,
                  "protocol": "usenet",
                  "fields": [{ "name": "tvCategory", "value": "{{category}}" }]
                }
                """)!).ToList();
            return clients;
        }

        public override async Task<SonarrQueue> GetSonarrQueueAsync(CancellationToken ct = default)
        {
            QueueProbeCount++;
            QueueProbeStartedAt ??= Stopwatch.GetTimestamp();
            BeforeQueue?.Invoke();
            if (QueueDelay > TimeSpan.Zero)
                await Task.Delay(QueueDelay, ct);
            if (QueueException is not null)
                throw QueueException;
            if (AfterQueue is not null)
                await AfterQueue(ct);
            return SonarrQueue;
        }

        public override async Task<ArrCommand> DownloadedEpisodesScanAsync(
            string path,
            string downloadClientId,
            CancellationToken ct = default)
        {
            DirectScanCount++;
            LastDirectScan = (path, downloadClientId);
            DirectScanEntered.TrySetResult();
            if (DirectScanGate is not null)
                await DirectScanGate.Task.WaitAsync(ct);
            if (AfterDirectScan is not null)
                await AfterDirectScan(ct);
            if (DirectScanException is not null)
                throw DirectScanException;
            return new ArrCommand
            {
                Id = 100 + DirectScanCount,
                Name = "DownloadedEpisodesScan"
            };
        }

        public override async Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default)
        {
            try
            {
                BeforeRefresh?.Invoke();
                RefreshCount++;
                DispatchEntered.TrySetResult();
                if (DispatchGate is not null)
                    await DispatchGate.Task.WaitAsync(ct);
                if (RefreshDelay > TimeSpan.Zero)
                    await Task.Delay(RefreshDelay, ct);
                if (BlockDispatchUntilCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (ShouldFail)
                    throw new HttpRequestException("simulated ARR failure");
                if (DispatchException is not null)
                    throw DispatchException;
                if (ReturnMalformedCommand)
                    return new ArrCommand();
                return new ArrCommand
                {
                    Id = RefreshCount,
                    Name = "RefreshMonitoredDownloads"
                };
            }
            finally
            {
                RefreshAttemptFinishedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    private sealed class RecordingRadarrClient(string host) : RadarrClient(host, "test-key")
    {
        public RadarrQueue RadarrQueue { get; set; } = new()
        {
            Page = 1,
            PageSize = 5000,
            TotalRecords = 0,
            Records = []
        };
        public int QueueProbeCount { get; private set; }
        public int DirectScanCount { get; private set; }
        public int RefreshCount { get; private set; }
        public (string Path, string DownloadId)? LastDirectScan { get; private set; }

        public override Task<RadarrQueue> GetRadarrQueueAsync(CancellationToken ct = default)
        {
            QueueProbeCount++;
            return Task.FromResult(RadarrQueue);
        }

        public override Task<ArrCommand> DownloadedMoviesScanAsync(
            string path,
            string downloadClientId,
            CancellationToken ct = default)
        {
            DirectScanCount++;
            LastDirectScan = (path, downloadClientId);
            return Task.FromResult(new ArrCommand
            {
                Id = 200 + DirectScanCount,
                Name = "DownloadedMoviesScan"
            });
        }

        public override Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default)
        {
            RefreshCount++;
            return Task.FromResult(new ArrCommand
            {
                Id = 300 + RefreshCount,
                Name = "RefreshMonitoredDownloads"
            });
        }
    }

    private sealed class RecordingLidarrClient(string host) : LidarrClient(host, "test-key")
    {
        public int RefreshCount { get; private set; }

        public override Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default)
        {
            RefreshCount++;
            return Task.FromResult(new ArrCommand
            {
                Id = 400 + RefreshCount,
                Name = "RefreshMonitoredDownloads"
            });
        }
    }

    private sealed class ThrowingHistoryVisibilityNotifier(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        Exception exception)
        : HistoryVisibilityNotifier(configManager, websocketManager)
    {
        public override Task<bool> PublishIfVisibleAsync(
            Guid historyItemId,
            CancellationToken ct = default) => Task.FromException<bool>(exception);
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();
        private readonly TaskCompletionSource _firstEmission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();
        public Task FirstEmission => _firstEmission.Task;
        public string Rendered => string.Join("\n", _events.Select(RenderLogEvent));

        public void Emit(LogEvent logEvent)
        {
            _events.Enqueue(logEvent);
            _firstEmission.TrySetResult();
        }
    }

    private sealed class PausingInvalidationReadInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _observed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;

        public Task Observed => _observed.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RcloneInvalidationItems", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _observed.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}

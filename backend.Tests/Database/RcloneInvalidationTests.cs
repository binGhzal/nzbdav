using System.Data.Common;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using backend.Tests.Security;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RcloneInvalidationTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public RcloneInvalidationTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveChangesAsync_EnqueuesInvalidationsForDavItemChanges()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var fileId = Guid.NewGuid();
            dbContext.Items.Add(new DavItem
            {
                Id = fileId,
                IdPrefix = fileId.GetFiveLengthPrefix(),
                CreatedAt = DateTime.UtcNow,
                ParentId = DavItem.ContentFolder.Id,
                Name = "movie.mkv",
                FileSize = 1024,
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.RarFile,
                Path = "/content/movies/Example/movie.mkv"
            });

            await dbContext.SaveChangesAsync();

            var paths = await dbContext.RcloneInvalidationItems
                .Select(x => x.Path)
                .ToListAsync();

            Assert.Contains("/content/movies/Example", paths);
            Assert.Contains("/completed-symlinks/movies/Example", paths);
            Assert.Contains(Path.GetDirectoryName(DatabaseStoreSymlinkFile.GetTargetPath(fileId))!, paths);
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_RetainsWholeCacheProofWhileRemoteControlIsDisabled()
    {
        DisableRcloneRemoteControl();
        await using var disabledContext = await _fixture.ResetAndCreateMigratedContextAsync();
        disabledContext.EnqueueRcloneVfsForgetPaths(["/nzbs", "/nzbs", " "]);
        await disabledContext.SaveChangesAsync();

        var item = Assert.Single(await disabledContext.RcloneInvalidationItems.ToListAsync());
        Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFencePath, item.Path);
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_UsesOneWholeCacheSentinelWhenDfsIsSelected()
    {
        SelectMountWithoutRcloneRemoteControl("dfs");

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            dbContext.EnqueueRcloneVfsForgetPaths(["/nzbs", "/content/tv", "/content/movies"]);
            await dbContext.SaveChangesAsync();

            var item = Assert.Single(await dbContext.RcloneInvalidationItems.ToListAsync());
            Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFencePath, item.Path);
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task DfsWholeCacheSentinel_IsSingletonAcrossConcurrentContexts()
    {
        SelectMountWithoutRcloneRemoteControl("dfs");
        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            await using var first = await _fixture.CreateMigratedContextAsync();
            await using var second = await _fixture.CreateMigratedContextAsync();
            first.EnqueueRcloneVfsForgetPaths(["/content/tv/One"]);
            second.EnqueueRcloneVfsForgetPaths(["/content/tv/Two"]);

            await Task.WhenAll(first.SaveChangesAsync(), second.SaveChangesAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var sentinel = Assert.Single(await assertionContext.RcloneInvalidationItems
                .Where(x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath)
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFenceId, sentinel.Id);
            Assert.True(sentinel.Revision >= 2);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheSentinel_IsReportedSeparatelyFromPathBacklogCounters()
    {
        SelectMountWithoutRcloneRemoteControl("dfs");
        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            dbContext.EnqueueRcloneVfsForgetPaths(["/content/tv/Example"]);
            await dbContext.SaveChangesAsync();

            var stats = await new DavDatabaseClient(dbContext).GetRcloneInvalidationStatsAsync();

            Assert.True(stats.WholeCacheVisibilityFencePending);
            Assert.Equal(0, stats.Pending);
            Assert.Equal(0, stats.Ready);
            Assert.Equal(0, stats.Failed);
            Assert.Null(stats.OldestPendingAt);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task EnablingRemoteControlWakesWorkerForDurableInvalidationsQueuedWhileDisabled()
    {
        var configManager = SelectMountWithoutRcloneRemoteControl("rclone");
        while (await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
        }

        try
        {
            configManager.UpdateValues([
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://127.0.0.1:5572" },
                new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
            ]);

            Assert.True(await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task DisabledRcloneFenceMakesWaitingArrCommandsDueWithoutDeletingInvalidations()
    {
        _ = SelectMountWithoutRcloneRemoteControl("dfs");
        try
        {
            var future = DateTimeOffset.UtcNow.AddMinutes(5);
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = new HistoryItem
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
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(new ArrImportCommand
                {
                    Id = Guid.NewGuid(),
                    HistoryItemId = history.Id,
                    Category = history.Category,
                    RequiredInvalidationPathsJson = "[\"/content/tv/Example\"]",
                    Status = ArrImportCommandStatus.WaitingForInvalidation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = future,
                    VisibleAt = DateTimeOffset.UtcNow
                });
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv/Example",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            while (await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            {
            }

            Assert.Equal(
                1,
                await RcloneInvalidationService.MakeWaitingArrCommandsDueWhenFenceNotRequiredAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.True(command.NextAttemptAt <= DateTimeOffset.UtcNow);
            Assert.Single(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
            Assert.True(await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task EnabledRemoteControlDoesNotReleaseWaitingCommandsWhileRcloneFenceIsRequired()
    {
        EnableRcloneRemoteControl();
        try
        {
            var future = DateTimeOffset.UtcNow.AddMinutes(5);
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = new HistoryItem
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
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(new ArrImportCommand
                {
                    Id = Guid.NewGuid(),
                    HistoryItemId = history.Id,
                    Category = history.Category,
                    RequiredInvalidationPathsJson = "[\"/content/tv/Example\"]",
                    Status = ArrImportCommandStatus.WaitingForInvalidation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = future,
                    VisibleAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            Assert.Equal(
                0,
                await RcloneInvalidationService.MakeWaitingArrCommandsDueWhenFenceNotRequiredAsync());

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
            Assert.Equal(future.UtcTicks, command.NextAttemptAt.UtcTicks);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_DoesNotCreateDuplicatePendingRows()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var now = DateTimeOffset.UtcNow;
            dbContext.RcloneInvalidationItems.Add(new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/nzbs",
                CreatedAt = now.AddMinutes(-10),
                NextAttemptAt = now.AddMinutes(5),
                Attempts = 2,
                LastError = "previous failure"
            });
            await dbContext.SaveChangesAsync();

            dbContext.EnqueueRcloneVfsForgetPaths(["/nzbs", "/nzbs"]);
            await dbContext.SaveChangesAsync();

            var item = Assert.Single(await dbContext.RcloneInvalidationItems.ToListAsync());
            Assert.Equal("/nzbs", item.Path);
            Assert.Equal(2, item.Attempts);
            Assert.Equal("rclone_invalidation_legacy_failure", item.LastError);
            Assert.DoesNotContain("previous failure", item.LastError, StringComparison.Ordinal);
            PublicFailureCanary.AssertSafe(item.LastError);
            Assert.True(item.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_AdvancesRevisionEvenWhenItemIsAlreadyReady()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var now = DateTimeOffset.UtcNow;
            dbContext.RcloneInvalidationItems.Add(new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/content/movies",
                Revision = 7,
                CreatedAt = now.AddMinutes(-1),
                NextAttemptAt = now.AddSeconds(-1)
            });
            await dbContext.SaveChangesAsync();

            dbContext.EnqueueRcloneVfsForgetPaths(["/content/movies"]);
            await dbContext.SaveChangesAsync();

            var item = await dbContext.RcloneInvalidationItems.SingleAsync();
            Assert.Equal(8, item.Revision);
            Assert.True(item.NextAttemptAt <= DateTimeOffset.UtcNow);
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task PublishOverridesBackoffWrittenByTheOldRevisionAfterThePublishRead()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
                await setup.SaveChangesAsync();
            }

            await using var producer = await _fixture.CreateMigratedContextAsync();
            producer.EnqueueRcloneVfsForgetPaths(["/content/tv"]);

            var staleBackoff = DateTimeOffset.UtcNow.AddMinutes(5);
            await using (var worker = await _fixture.CreateMigratedContextAsync())
            {
                await worker.RcloneInvalidationItems
                    .Where(x => x.Path == "/content/tv" && x.Revision == 1)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.NextAttemptAt, staleBackoff));
            }

            await producer.SaveChangesAsync();

            producer.ChangeTracker.Clear();
            var published = await producer.RcloneInvalidationItems.SingleAsync();
            Assert.Equal(2, published.Revision);
            Assert.True(published.NextAttemptAt < staleBackoff);
            Assert.True(published.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task ConfirmedForgetCannotDeleteARevisionPublishedAfterTheRcloneCallStarted()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/movies",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            RcloneInvalidationItem stale;
            await using (var producer = await _fixture.CreateMigratedContextAsync())
            {
                stale = await producer.RcloneInvalidationItems.AsNoTracking().SingleAsync();
                producer.EnqueueRcloneVfsForgetPaths([stale.Path]);
                await producer.SaveChangesAsync();
            }

            await using var worker = await _fixture.CreateMigratedContextAsync();
            var staleDelete = await RcloneInvalidationService.DeleteConfirmedItemsAsync(
                worker,
                [stale],
                CancellationToken.None);

            Assert.Equal(0, staleDelete);
            var current = await worker.RcloneInvalidationItems.AsNoTracking().SingleAsync();
            Assert.Equal(2, current.Revision);
            var currentDelete = await RcloneInvalidationService.DeleteConfirmedItemsAsync(
                worker,
                [current],
                CancellationToken.None);
            Assert.Equal(1, currentDelete);
            Assert.Empty(await worker.RcloneInvalidationItems.ToListAsync());
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task ConcurrentConfirmedDeleteIsRecoveredInsideThePublishingSave()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            await using var producer = await _fixture.CreateMigratedContextAsync();
            var stale = await producer.RcloneInvalidationItems.AsNoTracking().SingleAsync();
            producer.EnqueueRcloneVfsForgetPaths([stale.Path]);

            await using (var worker = await _fixture.CreateMigratedContextAsync())
            {
                Assert.Equal(
                    1,
                    await RcloneInvalidationService.DeleteConfirmedItemsAsync(
                        worker,
                        [stale],
                        CancellationToken.None));
            }

            await producer.SaveChangesAsync();

            producer.ChangeTracker.Clear();
            var republished = await producer.RcloneInvalidationItems.SingleAsync();
            Assert.Equal(stale.Id, republished.Id);
            Assert.Equal(stale.Path, republished.Path);
            Assert.True(republished.Revision > stale.Revision);
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_ToleratesExistingDuplicatePendingRows()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var now = DateTimeOffset.UtcNow;
            dbContext.RcloneInvalidationItems.AddRange(
                new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/movies",
                    CreatedAt = now.AddMinutes(-20),
                    NextAttemptAt = now.AddMinutes(5)
                },
                new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/movies",
                    CreatedAt = now.AddMinutes(-10),
                    NextAttemptAt = now.AddMinutes(10),
                    Attempts = 1
                });
            await dbContext.SaveChangesAsync();

            dbContext.EnqueueRcloneVfsForgetPaths(["/content/movies"]);
            await dbContext.SaveChangesAsync();

            var items = await dbContext.RcloneInvalidationItems.ToListAsync();
            Assert.Equal(2, items.Count);
            Assert.All(items, item =>
            {
                Assert.Equal("/content/movies", item.Path);
                Assert.True(item.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(1));
            });
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task EnqueueRcloneVfsForgetPaths_DeduplicatesRepeatedCallsBeforeSave()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();

            dbContext.EnqueueRcloneVfsForgetPaths(["/content/tv"]);
            dbContext.EnqueueRcloneVfsForgetPaths(["/content/tv"]);
            await dbContext.SaveChangesAsync();

            var item = Assert.Single(await dbContext.RcloneInvalidationItems.ToListAsync());
            Assert.Equal("/content/tv", item.Path);
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task SaveChangesAsync_SkipsItemsWithoutParentDirectoryWhenEnqueuingInvalidations()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var id = Guid.NewGuid();
            dbContext.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.GetFiveLengthPrefix(),
                CreatedAt = DateTime.UtcNow,
                ParentId = null,
                Name = "synthetic-root",
                Type = DavItem.ItemType.Directory,
                SubType = DavItem.ItemSubType.Directory,
                Path = "/"
            });

            await dbContext.SaveChangesAsync();

            Assert.DoesNotContain(await dbContext.RcloneInvalidationItems.ToListAsync(), x => string.IsNullOrWhiteSpace(x.Path));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task GetRcloneInvalidationStatsAsync_SummarizesReadyAndFailedInvalidations()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.RcloneInvalidationItems.AddRange(
            new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/ready",
                CreatedAt = now.AddMinutes(-3),
                NextAttemptAt = now.AddMinutes(-1),
                Attempts = 1,
                LastError = "temporary failure"
            },
            new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/future",
                CreatedAt = now.AddMinutes(-2),
                NextAttemptAt = now.AddMinutes(5),
                Attempts = 0
            },
            new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/failed",
                CreatedAt = now.AddMinutes(-1),
                NextAttemptAt = now.AddMinutes(1),
                Attempts = 4,
                LastError = "latest failure"
            }
        );
        await dbContext.SaveChangesAsync();

        var dbClient = new DavDatabaseClient(dbContext);
        var stats = await dbClient.GetRcloneInvalidationStatsAsync(now);

        Assert.Equal(3, stats.Pending);
        Assert.Equal(1, stats.Ready);
        Assert.Equal(2, stats.Failed);
        Assert.Equal(4, stats.MaxAttempts);
        Assert.Equal("rclone_invalidation_legacy_failure", stats.LastError);
        Assert.DoesNotContain("latest failure", stats.LastError, StringComparison.Ordinal);
        PublicFailureCanary.AssertSafe(stats.LastError);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-3).ToUnixTimeSeconds()),
            stats.OldestPendingAt);
    }

    [Fact]
    public async Task GetRcloneInvalidationStatsAsync_UsesTwoQueries()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("RcloneInvalidationItems", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        dbContext.RcloneInvalidationItems.AddRange(
            new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/ready",
                CreatedAt = now.AddMinutes(-3),
                NextAttemptAt = now.AddMinutes(-1),
                Attempts = 1,
                LastError = "temporary failure"
            },
            new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/failed",
                CreatedAt = now.AddMinutes(-1),
                NextAttemptAt = now.AddMinutes(1),
                Attempts = 4,
                LastError = "latest failure"
            }
        );
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var stats = await new DavDatabaseClient(dbContext).GetRcloneInvalidationStatsAsync(now);

        Assert.Equal(2, interceptor.Count);
        Assert.Equal(2, stats.Pending);
        Assert.Equal(1, stats.Ready);
        Assert.Equal(2, stats.Failed);
        Assert.Equal(4, stats.MaxAttempts);
        Assert.Equal("rclone_invalidation_legacy_failure", stats.LastError);
        Assert.DoesNotContain("latest failure", stats.LastError, StringComparison.Ordinal);
        PublicFailureCanary.AssertSafe(stats.LastError);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-3).ToUnixTimeSeconds()),
            stats.OldestPendingAt);
    }

    [Fact]
    public void GetSuccessfullyForgottenItems_OnlyReturnsPathsConfirmedByRclone()
    {
        var items = new List<RcloneInvalidationItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Path = "/content/a",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            },
            new()
            {
                Id = Guid.NewGuid(),
                Path = "/content/b",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            }
        };
        var response = new VfsForgetResponse
        {
            Success = true,
            Forgotten = ["content/a"]
        };

        var forgotten = RcloneInvalidationService.GetSuccessfullyForgottenItems(items, response);

        Assert.Equal(["/content/a"], forgotten.Select(x => x.Path));
    }

    [Fact]
    public void GetSuccessfullyForgottenItems_NormalizesLeadingAndTrailingSlashes()
    {
        var items = new List<RcloneInvalidationItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Path = "/nzbs",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            },
            new()
            {
                Id = Guid.NewGuid(),
                Path = "content/sonarr/",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            }
        };
        var response = new VfsForgetResponse
        {
            Success = true,
            Forgotten = ["nzbs", "/content/sonarr"]
        };

        var forgotten = RcloneInvalidationService.GetSuccessfullyForgottenItems(items, response);

        Assert.Equal(["/nzbs", "content/sonarr/"], forgotten.Select(x => x.Path));
    }

    [Fact]
    public void GetSuccessfullyForgottenItems_TreatsMissingForgottenListAsUnverified()
    {
        var item = new RcloneInvalidationItem
        {
            Id = Guid.NewGuid(),
            Path = "/content/a",
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        };
        var response = new VfsForgetResponse
        {
            Success = true,
            Forgotten = null
        };

        var forgotten = RcloneInvalidationService.GetSuccessfullyForgottenItems([item], response);

        Assert.Empty(forgotten);
    }

    [Fact]
    public async Task WakeSignalReleasesIdleInvalidationWorkerImmediately()
    {
        while (await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
        }

        var wait = RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        RcloneInvalidationWakeSignal.Pulse();

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ExplicitTransactionPublishesWakeOnlyAfterCommit()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            while (await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            {
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            dbContext.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.GetFiveLengthPrefix(),
                CreatedAt = DateTime.UtcNow,
                ParentId = DavItem.ContentFolder.Id,
                Name = "committed.mkv",
                FileSize = 1024,
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.NzbFile,
                Path = "/content/tv/Committed/committed.mkv"
            });
            await dbContext.SaveChangesAsync();

            Assert.False(await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
            await transaction.CommitAsync();
            Assert.True(await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task ExplicitTransactionRollbackDiscardsWake()
    {
        EnableRcloneRemoteControl();

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            while (await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            {
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            var id = Guid.NewGuid();
            dbContext.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.GetFiveLengthPrefix(),
                CreatedAt = DateTime.UtcNow,
                ParentId = DavItem.ContentFolder.Id,
                Name = "rolled-back.mkv",
                FileSize = 1024,
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.NzbFile,
                Path = "/content/tv/RolledBack/rolled-back.mkv"
            });
            await dbContext.SaveChangesAsync();
            await transaction.RollbackAsync();

            Assert.False(await RcloneInvalidationWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        }
        finally
        {
            DisableRcloneRemoteControl();
        }
    }

    [Fact]
    public async Task ProcessBatch_HoldsTargetGateThroughAcknowledgementAndRevisionDelete()
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "old-fs:" }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var item = new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/content/tv",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            };
            dbContext.RcloneInvalidationItems.Add(item);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var processing = RcloneInvalidationService.ProcessBatchAsync(
                dbContext,
                [item],
                CancellationToken.None);
            var requestBody = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("\"fs\":\"old-fs:\"", requestBody, StringComparison.Ordinal);

            var configChange = Task.Run(() => configManager.UpdateValues([
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://replacement-rclone:5572" },
                new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "new-fs:" }
            ]));
            await Task.Delay(100);
            Assert.False(configChange.IsCompleted);

            server.Release("""{"forgotten":["content/tv"]}""");
            await processing;
            await configChange.WaitAsync(TimeSpan.FromSeconds(2));

            dbContext.ChangeTracker.Clear();
            Assert.DoesNotContain(
                await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Id == item.Id);
            Assert.Equal("http://replacement-rclone:5572", RcloneClient.Host);
            Assert.Equal("new-fs:", RcloneClient.Fs);
            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("nzbdav:", "{\"fs\":\"nzbdav:\"}")]
    public async Task WholeCacheFence_UsesNoPathForgetAndClearsOnlyAfterExactAcknowledgement(
        string? fs,
        string expectedBody)
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        var values = new List<ConfigItem>
        {
            new() { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new() { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new() { ConfigName = "rclone.host", ConfigValue = server.Url }
        };
        if (fs is not null)
            values.Add(new ConfigItem { ConfigName = "rclone.fs", ConfigValue = fs });
        configManager.UpdateValues(values);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            var processing = Task.Run(
                () => RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync());
            Assert.Equal(expectedBody, await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);

            server.Release("""{"forgotten":[]}""");
            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Completed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.DoesNotContain(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"forgotten\":[\"\"]}")]
    public async Task WholeCacheFence_RejectsNonExactAcknowledgement(string responseBody)
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            var processing = RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync();
            _ = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            server.Release(responseBody);

            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Failed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
            Assert.Equal("invalid response", RcloneClient.GetRuntimeSnapshot().LastError);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.Contains(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheFence_DisabledRemoteControlDoesNotRequireActiveProof()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new() { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new() { ConfigName = "rclone.rc-enabled", ConfigValue = "false" }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.NotRequired,
                await RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync());

            Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheFence_EnabledRemoteControlWithoutHostCannotClearProof()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new() { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new() { ConfigName = "rclone.rc-enabled", ConfigValue = "true" }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Failed,
                await RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync());

            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.Contains(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheFence_RevisionCasRetainsConcurrentPublication()
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            var processing = RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync();
            _ = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            await using (var publisher = await _fixture.CreateMigratedContextAsync())
            {
                publisher.EnqueueWholeCacheVisibilityFence();
                await publisher.SaveChangesAsync();
            }

            server.Release("""{"forgotten":[]}""");
            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Completed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            var sentinel = await assertionContext.RcloneInvalidationItems.AsNoTracking().SingleAsync(
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
            Assert.True(sentinel.Revision >= 2);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheFence_HoldsTargetGateThroughAcknowledgementAndSentinelDelete()
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "old-fs:" }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var reset = await _fixture.ResetAndCreateMigratedContextAsync())
            {
            }

            var processing = RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync();
            var requestBody = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("{\"fs\":\"old-fs:\"}", requestBody);
            var configChange = Task.Run(() => configManager.UpdateValues([
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://replacement-rclone:5572" },
                new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "new-fs:" }
            ]));
            await Task.Delay(100);
            Assert.False(configChange.IsCompleted);

            server.Release("""{"forgotten":[]}""");
            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Completed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            await configChange.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("http://replacement-rclone:5572", RcloneClient.Host);
            Assert.Equal("new-fs:", RcloneClient.Fs);
            Assert.True(RcloneClient.WholeCacheVisibilityFencePending);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.DoesNotContain(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WholeCacheFenceClearMakesWaitingArrCommandsDueAndWakesWorker()
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            var future = DateTimeOffset.UtcNow.AddMinutes(5);
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var history = new HistoryItem
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
                setup.HistoryItems.Add(history);
                setup.ArrImportCommands.Add(new ArrImportCommand
                {
                    Id = Guid.NewGuid(),
                    HistoryItemId = history.Id,
                    Category = history.Category,
                    RequiredInvalidationPathsJson = "[]",
                    Status = ArrImportCommandStatus.WaitingForInvalidation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = future,
                    VisibleAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }
            while (await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            {
            }

            var processing = RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync();
            _ = await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            server.Release("""{"forgotten":[]}""");
            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Completed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.True((await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).NextAttemptAt
                        <= DateTimeOffset.UtcNow);
            Assert.True(await ArrImportCommandWakeSignal.WaitAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None));
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task WorkerProcessesWholeCacheFenceBeforePathRows()
    {
        var handler = new RecordingForgetHandler();
        using var clientOverride = RcloneClient.OverrideHttpClientForTests(new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2)
        });
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone.test:5572" }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                setup.RcloneInvalidationItems.Add(new RcloneInvalidationItem
                {
                    Id = Guid.NewGuid(),
                    Path = "/content/tv/Example",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var service = new RcloneInvalidationService();
            await service.StartAsync(cts.Token);
            var bodies = await handler.FirstTwoBodies.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("{}", bodies[0]);
            Assert.Contains("\"dir\":\"/content/tv/Example\"", bodies[1], StringComparison.Ordinal);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await using var probe = await _fixture.CreateMigratedContextAsync();
                if (!await probe.RcloneInvalidationItems.AsNoTracking().AnyAsync())
                    break;
                await Task.Delay(20, cts.Token);
            }
            await service.StopAsync(CancellationToken.None);

            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.Empty(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task DfsCommitCannotLandUnfencedAfterRcloneTransitionFlush()
    {
        await using var server = await GatedResponseHttpServer.StartAsync();
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "dfs" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url }
        ]);
        RcloneClient.Initialize(configManager);

        try
        {
            await using var producer = await _fixture.ResetAndCreateMigratedContextAsync();
            await using var transaction = await producer.Database.BeginTransactionAsync();
            producer.EnqueueRcloneVfsForgetPaths(["/content/tv/CommittedAfterTransition"]);
            await producer.SaveChangesAsync();

            configManager.UpdateValues([
                new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" }
            ]);
            var processing = Task.Run(
                () => RcloneInvalidationService.ProcessWholeCacheVisibilityFenceAsync());
            await Task.Delay(100);
            Assert.False(server.RequestBody.IsCompleted);

            await transaction.CommitAsync();
            Assert.Equal("{}", await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2)));
            server.Release("""{"forgotten":[]}""");
            Assert.Equal(
                RcloneInvalidationService.WholeCacheFenceProcessOutcome.Completed,
                await processing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.False(RcloneClient.WholeCacheVisibilityFencePending);
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.DoesNotContain(
                await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(),
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public async Task ProcessBatch_PersistsAndExposesOnlySecretSafeFailureCategories()
    {
        await AssertSecretSafeFailureAsync(
            HttpStatusCode.InternalServerError,
            "{\"error\":\"server echoed super-secret-rc-password and raw-token-value\"}",
            "rclone_rc_http_500");
        await AssertSecretSafeFailureAsync(
            HttpStatusCode.OK,
            "malformed super-secret-rc-password raw-token-value response",
            "rclone_rc_malformed_response");
    }

    private async Task AssertSecretSafeFailureAsync(
        HttpStatusCode statusCode,
        string responseBody,
        string expectedCategory)
    {
        const string password = "super-secret-rc-password";
        const string token = "raw-token-value";
        await using var server = await GatedResponseHttpServer.StartAsync(statusCode);
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" },
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = server.Url },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "benchmark-user" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = password }
        ]);
        RcloneClient.Initialize(configManager);
        var sink = new RenderedLogSink();
        var testLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var previousLogger = Log.Logger;
        Log.Logger = testLogger;

        try
        {
            await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
            var item = new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/content/tv",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            };
            dbContext.RcloneInvalidationItems.Add(item);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var processing = RcloneInvalidationService.ProcessBatchAsync(
                dbContext,
                [item],
                CancellationToken.None);
            await server.RequestBody.WaitAsync(TimeSpan.FromSeconds(2));
            server.Release(responseBody);
            await processing;

            dbContext.ChangeTracker.Clear();
            var retained = await dbContext.RcloneInvalidationItems.AsNoTracking().SingleAsync();
            Assert.Equal(expectedCategory, retained.LastError);
            var now = DateTimeOffset.UtcNow;
            var stats = await new DavDatabaseClient(dbContext).GetRcloneInvalidationStatsAsync(now);
            var status = RcloneInvalidationStatus.FromSnapshots(
                stats,
                RcloneClient.GetRuntimeSnapshot(),
                now);
            var serializedStatus = JsonSerializer.Serialize(status);
            var durableAndExposedText = string.Join('\n', new[] {
                retained.LastError ?? "",
                serializedStatus,
                sink.Rendered
            });

            Assert.Contains(expectedCategory, serializedStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(password, durableAndExposedText, StringComparison.Ordinal);
            Assert.DoesNotContain(token, durableAndExposedText, StringComparison.Ordinal);
            Assert.DoesNotContain(responseBody, durableAndExposedText, StringComparison.Ordinal);

            var legacyUnsafeStatus = RcloneInvalidationStatus.FromSnapshots(
                stats with { LastError = responseBody },
                RcloneClient.GetRuntimeSnapshot(),
                now);
            var serializedLegacyStatus = JsonSerializer.Serialize(legacyUnsafeStatus);
            Assert.Contains("rclone_invalidation_legacy_failure", serializedLegacyStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(password, serializedLegacyStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(token, serializedLegacyStatus, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = previousLogger;
            testLogger.Dispose();
            RcloneClient.Initialize(new ConfigManager());
        }
    }

    [Fact]
    public void InitializeIgnoresChangesFromPreviousConfigManagers()
    {
        var oldConfigManager = new ConfigManager();
        oldConfigManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://old-rclone:5572" }
        ]);
        RcloneClient.Initialize(oldConfigManager);

        var currentConfigManager = new ConfigManager();
        currentConfigManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "false" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://current-rclone:5572" }
        ]);
        RcloneClient.Initialize(currentConfigManager);

        oldConfigManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://stale-rclone:5572" }
        ]);

        Assert.False(RcloneClient.IsRemoteControlEnabled);
        Assert.Equal("http://current-rclone:5572", RcloneClient.Host);
    }

    private static void EnableRcloneRemoteControl()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://127.0.0.1:5572" }
        ]);
        RcloneClient.Initialize(configManager);
    }

    private static void DisableRcloneRemoteControl()
    {
        RcloneClient.Initialize(new ConfigManager());
    }

    private static ConfigManager SelectMountWithoutRcloneRemoteControl(string mountType)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = mountType }
        ]);
        RcloneClient.Initialize(configManager);
        return configManager;
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

        private void CountIfMatched(DbCommand command)
        {
            if (predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }

    private sealed class GatedResponseHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<string> _requestBody =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _responseBody =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private Task _serverTask;

        private readonly HttpStatusCode _statusCode;

        private GatedResponseHttpServer(TcpListener listener, string url, HttpStatusCode statusCode)
        {
            _listener = listener;
            Url = url;
            _statusCode = statusCode;
            _serverTask = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public Task<string> RequestBody => _requestBody.Task;

        public static Task<GatedResponseHttpServer> StartAsync(
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new GatedResponseHttpServer(
                listener,
                $"http://127.0.0.1:{port}",
                statusCode));
        }

        public void Release(string responseBody)
        {
            _responseBody.TrySetResult(responseBody);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var body = await ReadRequestBodyAsync(stream, _cts.Token).ConfigureAwait(false);
                _requestBody.TrySetResult(body);
                var responseBody = await _responseBody.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
                var bodyBytes = Encoding.UTF8.GetBytes(responseBody);
                var header = string.Join("\r\n", [
                    $"HTTP/1.1 {(int)_statusCode} {_statusCode}",
                    "Content-Type: application/json",
                    $"Content-Length: {bodyBytes.Length}",
                    "Connection: close",
                    "",
                    ""
                ]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
        }

        private static async Task<string> ReadRequestBodyAsync(NetworkStream stream, CancellationToken ct)
        {
            using var request = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                request.Write(buffer, 0, read);
                var bytes = request.ToArray();
                if (headerEnd < 0)
                {
                    var text = Encoding.ASCII.GetString(bytes);
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        var headers = text[..headerEnd];
                        var lengthHeader = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        if (lengthHeader is not null)
                            int.TryParse(lengthHeader[(lengthHeader.IndexOf(':') + 1)..].Trim(), out contentLength);
                    }
                }

                if (headerEnd >= 0 && bytes.Length >= headerEnd + 4 + contentLength)
                    return Encoding.UTF8.GetString(bytes, headerEnd + 4, contentLength);
            }

            return "";
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
            }
            _cts.Dispose();
        }
    }

    private sealed class RecordingForgetHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _bodies = [];

        public TaskCompletionSource<IReadOnlyList<string>> FirstTwoBodies { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock)
            {
                _bodies.Add(body);
                if (_bodies.Count >= 2)
                    FirstTwoBodies.TrySetResult(_bodies.Take(2).ToArray());
            }

            var responseBody = body.Contains("\"dir\"", StringComparison.Ordinal)
                ? """{"forgotten":["content/tv/Example"]}"""
                : """{"forgotten":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RenderedLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();

        public string Rendered => string.Join('\n', _events);

        public void Emit(LogEvent logEvent)
        {
            _events.Enqueue(logEvent.RenderMessage());
            if (logEvent.Exception is not null)
                _events.Enqueue(logEvent.Exception.ToString());
        }
    }
}

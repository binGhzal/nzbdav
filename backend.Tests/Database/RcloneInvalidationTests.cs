using System.Data.Common;
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
using NzbWebDAV.Services;
using NzbWebDAV.WebDav;

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
    public async Task EnqueueRcloneVfsForgetPaths_DeduplicatesPathsAndSkipsWhenDisabled()
    {
        DisableRcloneRemoteControl();
        await using var disabledContext = await _fixture.ResetAndCreateMigratedContextAsync();
        disabledContext.EnqueueRcloneVfsForgetPaths(["/nzbs"]);
        await disabledContext.SaveChangesAsync();
        Assert.Empty(await disabledContext.RcloneInvalidationItems.ToListAsync());

        EnableRcloneRemoteControl();

        try
        {
            await using var enabledContext = await _fixture.ResetAndCreateMigratedContextAsync();
            enabledContext.EnqueueRcloneVfsForgetPaths(["/nzbs", "/nzbs", " "]);
            await enabledContext.SaveChangesAsync();

            var item = Assert.Single(await enabledContext.RcloneInvalidationItems.ToListAsync());
            Assert.Equal("/nzbs", item.Path);
        }
        finally
        {
            DisableRcloneRemoteControl();
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
            Assert.Equal("previous failure", item.LastError);
            Assert.True(item.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(1));
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
        Assert.Equal("latest failure", stats.LastError);
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
        Assert.Equal("latest failure", stats.LastError);
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
}

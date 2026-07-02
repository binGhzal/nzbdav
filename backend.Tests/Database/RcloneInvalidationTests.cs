using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
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
            Forgotten = ["/content/a"]
        };

        var forgotten = RcloneInvalidationService.GetSuccessfullyForgottenItems(items, response);

        Assert.Equal(["/content/a"], forgotten.Select(x => x.Path));
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
}

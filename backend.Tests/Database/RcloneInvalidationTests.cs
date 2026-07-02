using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
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

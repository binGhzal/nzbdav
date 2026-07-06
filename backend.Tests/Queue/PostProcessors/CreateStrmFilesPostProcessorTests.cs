using backend.Tests.Services;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;

namespace backend.Tests.Queue.PostProcessors;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class CreateStrmFilesPostProcessorTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public CreateStrmFilesPostProcessorTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateStrmFilesAsync_SkipsPerFileWriteErrorsAndContinues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var completedDir = _fixture.CreateLibraryDirectory();
        var configManager = CreateConfigManager(completedDir);
        var mountFolder = CreateDirectory("movies", DavItem.ContentFolder);
        var blocked = CreateFile(mountFolder, "Blocked.mkv");
        var valid = CreateFile(mountFolder, "Valid.mkv");
        dbContext.Items.AddRange(mountFolder, blocked, valid);
        Directory.CreateDirectory(Path.Join(completedDir, "movies", "Blocked.mkv.strm"));

        var processor = new CreateStrmFilesPostProcessor(configManager, new DavDatabaseClient(dbContext));

        await processor.CreateStrmFilesAsync(mountFolder);

        Assert.True(Directory.Exists(Path.Join(completedDir, "movies", "Blocked.mkv.strm")));
        Assert.True(File.Exists(Path.Join(completedDir, "movies", "Valid.mkv.strm")));
    }

    [Fact]
    public async Task CreateStrmFilesAsync_SkipsInvalidPathsAndContinues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var completedDir = _fixture.CreateLibraryDirectory();
        var configManager = CreateConfigManager(completedDir);
        var mountFolder = CreateDirectory("movies", DavItem.ContentFolder);
        var invalid = CreateFile(mountFolder, "Invalid.mkv");
        invalid.Path = $"{mountFolder.Path}/Invalid\0.mkv";
        var valid = CreateFile(mountFolder, "Valid.mkv");
        dbContext.Items.AddRange(mountFolder, invalid, valid);

        var processor = new CreateStrmFilesPostProcessor(configManager, new DavDatabaseClient(dbContext));

        await processor.CreateStrmFilesAsync(mountFolder);

        Assert.True(File.Exists(Path.Join(completedDir, "movies", "Valid.mkv.strm")));
    }

    private static ConfigManager CreateConfigManager(string completedDir)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.completed-downloads-dir", ConfigValue = completedDir },
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" },
            new ConfigItem { ConfigName = "general.base-url", ConfigValue = "http://localhost:3000" }
        ]);
        return configManager;
    }

    private static DavItem CreateDirectory(string name, DavItem parent)
    {
        return DavItem.New(
            Guid.NewGuid(),
            parent,
            name,
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null);
    }

    private static DavItem CreateFile(DavItem parent, string name)
    {
        return DavItem.New(
            Guid.NewGuid(),
            parent,
            name,
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);
    }
}

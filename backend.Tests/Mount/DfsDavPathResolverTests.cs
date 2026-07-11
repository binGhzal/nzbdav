using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.WebDav;

namespace backend.Tests.Mount;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DfsDavPathResolverTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DfsDavPathResolverTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveAsync_ExposesContentAndIdsPath()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        var movie = CreateNzbFile(category, "Movie.mkv");
        dbContext.Items.AddRange(category, movie);
        await dbContext.SaveChangesAsync();

        var resolver = CreateResolver(dbContext);
        var contentNode = await resolver.ResolveAsync("/content/movies/Movie.mkv");
        var idPath = "/.ids/" + string.Join('/', movie.IdPrefix.Select(x => x.ToString())) + "/" + movie.Id;
        var idNode = await resolver.ResolveAsync(idPath);

        Assert.NotNull(contentNode);
        Assert.Equal(DfsDavNodeKind.File, contentNode.Kind);
        Assert.Equal(movie.Id, contentNode.Item?.Id);
        Assert.NotNull(idNode);
        Assert.Equal(DfsDavNodeKind.File, idNode.Kind);
        Assert.Equal(movie.Id, idNode.Item?.Id);
    }

    [Fact]
    public async Task ResolveAsync_ExposesVirtualContentAndNzbsRootsWithoutDatabaseRows()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var resolver = CreateResolver(dbContext);

        var contentNode = await resolver.ResolveAsync("/content");
        var nzbsNode = await resolver.ResolveAsync("/nzbs");

        Assert.NotNull(contentNode);
        Assert.Equal(DfsDavNodeKind.Directory, contentNode.Kind);
        Assert.Equal(DavItem.ContentFolder.Id, contentNode.Item?.Id);
        Assert.NotNull(nzbsNode);
        Assert.Equal(DfsDavNodeKind.Directory, nzbsNode.Kind);
        Assert.Equal(DavItem.NzbFolder.Id, nzbsNode.Item?.Id);
    }

    [Theory]
    [InlineData("/.ids/6/a/8/9/6/.gitignore")]
    [InlineData("/.ids/6/a/8/9/6/.ignore")]
    public async Task ResolveAsync_InvalidIdsLeaf_ReturnsNull(string path)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var resolver = CreateResolver(dbContext);

        var node = await resolver.ResolveAsync(path);

        Assert.Null(node);
    }

    [Fact]
    public async Task CompletedSymlinkPath_ResolvesAsSymlinkAndCanBeSuppressedByUnlink()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        var movie = CreateNzbFile(category, "Movie.mkv");
        dbContext.Items.AddRange(category, movie);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Movie.nzb",
            JobName = "Movie",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = category.Id
        });
        await dbContext.SaveChangesAsync();

        var resolver = CreateResolver(dbContext);
        var symlinkPath = "/completed-symlinks/movies/Movie.mkv";
        var symlinkNode = await resolver.ResolveAsync(symlinkPath);
        var deleted = await resolver.MarkCompletedSymlinkDeletedAsync(symlinkPath);
        var suppressedNode = await resolver.ResolveAsync(symlinkPath);

        Assert.NotNull(symlinkNode);
        Assert.Equal(DfsDavNodeKind.Symlink, symlinkNode.Kind);
        Assert.Equal(
            DatabaseStoreSymlinkFile.GetTargetPath(movie.Id, "/mnt/nzbdav"),
            symlinkNode.SymlinkTarget);
        Assert.True(deleted);
        Assert.Null(suppressedNode);
    }

    [Fact]
    public async Task CompletedSymlinkPath_UsesRelativeTargetWhenConfigured()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        var movieFolder = CreateDirectory("Example", category);
        var movie = CreateNzbFile(movieFolder, "Movie.mkv");
        dbContext.Items.AddRange(category, movieFolder, movie);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Movie.nzb",
            JobName = "Movie",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = category.Id
        });
        await dbContext.SaveChangesAsync();

        var configManager = _fixture.CreateConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.symlink-target-mode", ConfigValue = "relative" }
        ]);
        var resolver = CreateResolver(dbContext, configManager);
        var symlinkNode = await resolver.ResolveAsync("/completed-symlinks/movies/Example/Movie.mkv");

        Assert.NotNull(symlinkNode);
        Assert.Equal(
            "../../../" + DatabaseStoreSymlinkFile.GetTargetPath(movie.Id, '/'),
            symlinkNode.SymlinkTarget);
    }

    private DfsDavPathResolver CreateResolver(DavDatabaseContext dbContext, ConfigManager? configManager = null)
    {
        return new DfsDavPathResolver(
            new DavDatabaseClient(dbContext),
            configManager ?? _fixture.CreateConfigManager());
    }

    private static DavItem CreateDirectory(string name, DavItem parent)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parent.Id,
            Name = name,
            FileSize = null,
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = Path.Join(parent.Path, name)
        };
    }

    private static DavItem CreateNzbFile(DavItem parent, string name)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parent.Id,
            Name = name,
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = Path.Join(parent.Path, name)
        };
    }
}

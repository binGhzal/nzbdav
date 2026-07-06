using System.Reflection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Tasks;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class StrmToSymlinksTaskTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public StrmToSymlinksTaskTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Execute_LeavesStrmUntouchedWhenDavItemNoLongerExists()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var libraryPath = _fixture.CreateLibraryDirectory();
        var staleId = Guid.NewGuid();
        var strmPath = Path.Join(libraryPath, "Stale Movie.strm");
        var symlinkPath = Path.Join(libraryPath, "Stale Movie.mkv");
        await File.WriteAllTextAsync(
            strmPath,
            $"http://localhost:3000/view/.ids/{staleId}.mkv?downloadKey=test&extension=mkv");
        var task = new StrmToSymlinksTask(
            _fixture.CreateConfigManager(libraryPath),
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.Execute();

        Assert.True(File.Exists(strmPath));
        Assert.False(File.Exists(symlinkPath));
    }

    [Fact]
    public async Task ConvertBatch_FallsBackToDavItemExtensionWhenStrmExtensionIsUnsafe()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var libraryPath = _fixture.CreateLibraryDirectory();
        var unsafeDavItem = CreateFile("Unsafe Movie.mkv");
        var validDavItem = CreateFile("Valid Movie.mkv");
        dbContext.Items.AddRange(unsafeDavItem, validDavItem);
        await dbContext.SaveChangesAsync();

        var unsafeStrmPath = Path.Join(libraryPath, "Unsafe Movie.strm");
        var validStrmPath = Path.Join(libraryPath, "Valid Movie.strm");
        await File.WriteAllTextAsync(unsafeStrmPath, "placeholder");
        await File.WriteAllTextAsync(validStrmPath, "placeholder");
        var task = new StrmToSymlinksTask(
            _fixture.CreateConfigManager(libraryPath),
            new DavDatabaseClient(dbContext),
            new WebsocketManager());
        var batch = new List<OrganizedLinksUtil.DavItemLink>
        {
            CreateStrmLink(
                unsafeStrmPath,
                unsafeDavItem.Id,
                $"http://localhost:3000/view/.ids/{unsafeDavItem.Id}.mkv?downloadKey=test&extension=mkv%2Fevil"),
            CreateStrmLink(
                validStrmPath,
                validDavItem.Id,
                $"http://localhost:3000/view/.ids/{validDavItem.Id}.mkv?downloadKey=test&extension=mkv")
        };

        await InvokeConvertBatch(task, batch);

        Assert.False(File.Exists(unsafeStrmPath));
        Assert.True(File.Exists(Path.Join(libraryPath, "Unsafe Movie.mkv")));
        Assert.False(File.Exists(validStrmPath));
        Assert.True(File.Exists(Path.Join(libraryPath, "Valid Movie.mkv")));
    }

    [Fact]
    public async Task Execute_RemovesStrmWhenExpectedSymlinkAlreadyExists()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var libraryPath = _fixture.CreateLibraryDirectory();
        var davItem = CreateFile("Existing Movie.mkv");
        dbContext.Items.Add(davItem);
        await dbContext.SaveChangesAsync();

        var strmPath = Path.Join(libraryPath, "Existing Movie.strm");
        var symlinkPath = Path.Join(libraryPath, "Existing Movie.mkv");
        var expectedTarget = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, "/mnt/nzbdav");
        await File.WriteAllTextAsync(
            strmPath,
            $"http://localhost:3000/view/.ids/{davItem.Id}.mkv?downloadKey=test&extension=mkv");
        File.CreateSymbolicLink(symlinkPath, expectedTarget);
        var task = new StrmToSymlinksTask(
            _fixture.CreateConfigManager(libraryPath),
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.Execute();

        Assert.False(File.Exists(strmPath));
        Assert.True(File.Exists(symlinkPath));
        Assert.Equal(expectedTarget, new FileInfo(symlinkPath).LinkTarget);
    }

    [Fact]
    public async Task ConvertBatch_SkipsPerFileFilesystemErrorsAndContinues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var libraryPath = _fixture.CreateLibraryDirectory();
        var missingParentItem = CreateFile("Missing Parent Movie.mkv");
        var validDavItem = CreateFile("Valid Movie.mkv");
        dbContext.Items.AddRange(missingParentItem, validDavItem);
        await dbContext.SaveChangesAsync();

        var removedDirectory = Path.Join(libraryPath, "removed");
        Directory.CreateDirectory(removedDirectory);
        var missingParentStrmPath = Path.Join(removedDirectory, "Missing Parent Movie.strm");
        await File.WriteAllTextAsync(missingParentStrmPath, "placeholder");
        Directory.Delete(removedDirectory, recursive: true);

        var validStrmPath = Path.Join(libraryPath, "Valid Movie.strm");
        await File.WriteAllTextAsync(validStrmPath, "placeholder");
        var task = new StrmToSymlinksTask(
            _fixture.CreateConfigManager(libraryPath),
            new DavDatabaseClient(dbContext),
            new WebsocketManager());
        var batch = new List<OrganizedLinksUtil.DavItemLink>
        {
            CreateStrmLink(
                missingParentStrmPath,
                missingParentItem.Id,
                $"http://localhost:3000/view/.ids/{missingParentItem.Id}.mkv?downloadKey=test&extension=mkv"),
            CreateStrmLink(
                validStrmPath,
                validDavItem.Id,
                $"http://localhost:3000/view/.ids/{validDavItem.Id}.mkv?downloadKey=test&extension=mkv")
        };

        await InvokeConvertBatch(task, batch);

        Assert.False(File.Exists(validStrmPath));
        Assert.True(File.Exists(Path.Join(libraryPath, "Valid Movie.mkv")));
    }

    private static DavItem CreateFile(string name)
    {
        return DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            name,
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);
    }

    private static OrganizedLinksUtil.DavItemLink CreateStrmLink(string path, Guid davItemId, string targetUrl)
    {
        return new OrganizedLinksUtil.DavItemLink
        {
            LinkPath = path,
            DavItemId = davItemId,
            SymlinkOrStrmInfo = new SymlinkAndStrmUtil.StrmInfo
            {
                StrmPath = path,
                TargetUrl = targetUrl
            }
        };
    }

    private static async Task InvokeConvertBatch(
        StrmToSymlinksTask task,
        List<OrganizedLinksUtil.DavItemLink> batch)
    {
        var method = typeof(StrmToSymlinksTask).GetMethod(
            "ConvertBatchOfStrmFilesToSymlinks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var conversion = (Task)method.Invoke(task, [batch, () => { }, CancellationToken.None])!;
        await conversion;
    }
}

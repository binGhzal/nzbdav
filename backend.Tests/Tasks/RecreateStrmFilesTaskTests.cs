using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Tasks;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RecreateStrmFilesTaskTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public RecreateStrmFilesTaskTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecreateStrmFiles_ReadsDavItemsOnlyOnce()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("DavItems", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var completedDir = Path.Join(Path.GetTempPath(), "nzbdav-recreate-strm", Guid.NewGuid().ToString("N"));
        var configManager = CreateConfigManager(completedDir);
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        dbContext.Items.AddRange(
            category,
            CreateFile(category, "Movie.mkv"),
            CreateFile(category, "Movie.nfo"));
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var task = new RecreateStrmFilesTask(
            configManager,
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.RecreateStrmFiles();

        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    public async Task RecreateStrmFiles_FiltersVideoDavItemsInDatabase()
    {
        var interceptor = new CommandTextCollector(commandText =>
            commandText.Contains("DavItems", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var completedDir = Path.Join(Path.GetTempPath(), "nzbdav-recreate-strm", Guid.NewGuid().ToString("N"));
        var configManager = CreateConfigManager(completedDir);
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        dbContext.Items.AddRange(
            category,
            CreateFile(category, "Movie.mkv"),
            CreateFile(category, "Poster.jpg"),
            CreateFile(category, "Movie.nfo"));
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        var task = new RecreateStrmFilesTask(
            configManager,
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.RecreateStrmFiles();

        var davItemQuery = Assert.Single(interceptor.CommandTexts);
        Assert.Contains(".mkv", davItemQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Poster.jpg", File.ReadAllText(Path.Join(completedDir, "movies", "Movie.mkv.strm")));
    }

    [Fact]
    public async Task RecreateStrmFiles_CreatesFilesOnlyForVideoDavItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var completedDir = _fixture.CreateLibraryDirectory();
        var configManager = CreateConfigManager(completedDir);
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        var video = CreateFile(category, "Movie.mkv");
        var nfo = CreateFile(category, "Movie.nfo");
        dbContext.Items.AddRange(category, video, nfo);
        await dbContext.SaveChangesAsync();

        var task = new RecreateStrmFilesTask(
            configManager,
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.RecreateStrmFiles();

        var strmFiles = Directory
            .EnumerateFiles(completedDir, "*.strm", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["Movie.mkv.strm"], strmFiles);
    }

    [Fact]
    public async Task RecreateStrmFiles_SkipsPerFileWriteErrorsAndContinues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var completedDir = _fixture.CreateLibraryDirectory();
        var configManager = CreateConfigManager(completedDir);
        var category = CreateDirectory("movies", DavItem.ContentFolder);
        var blocked = CreateFile(category, "Blocked.mkv");
        var valid = CreateFile(category, "Valid.mkv");
        dbContext.Items.AddRange(category, blocked, valid);
        await dbContext.SaveChangesAsync();
        Directory.CreateDirectory(Path.Join(completedDir, "movies", "Blocked.mkv.strm"));

        var task = new RecreateStrmFilesTask(
            configManager,
            new DavDatabaseClient(dbContext),
            new WebsocketManager());

        await task.RecreateStrmFiles();

        Assert.True(Directory.Exists(Path.Join(completedDir, "movies", "Blocked.mkv.strm")));
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

    private sealed class CommandTextCollector(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<string> CommandTexts
        {
            get
            {
                lock (_lock)
                {
                    return _commandTexts.ToList();
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _commandTexts.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CollectIfMatched(command);
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
            CollectIfMatched(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CollectIfMatched(DbCommand command)
        {
            if (!predicate(command.CommandText)) return;
            lock (_lock)
            {
                _commandTexts.Add(command.CommandText);
            }
        }
    }
}

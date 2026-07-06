using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ContentIndexRecoveryServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ContentIndexRecoveryServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StartupRecovery_RestoresAllContent_WhenDatabaseComesUpEmpty()
    {
        var expectedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(expectedItemId, movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1", "segment-2"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            2,
            await restoredContext.Items.CountAsync(x =>
                x.Path.StartsWith(ContentPathUtil.ForwardSlashPrefix) || x.Path.StartsWith(ContentPathUtil.BackslashPrefix))
        );
        Assert.Equal(["segment-1", "segment-2"], (await restoredContext.NzbFiles.SingleAsync()).SegmentIds);
    }

    [Fact]
    public async Task StartupRecovery_RestoresMissingMetadata_ForExistingContentItem()
    {
        var expectedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(expectedItemId, movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1", "segment-2"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM DavNzbFiles WHERE Id = {0}", expectedItemId);
        }

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        Assert.Single(await restoredContext.NzbFiles.Where(x => x.Id == expectedItemId).ToListAsync());
    }

    [Fact]
    public async Task StartupRecovery_RestoresBlobBackedItemsWithoutDatabaseMetadataRows()
    {
        var itemId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var nzbBlobId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(itemId, movieDirectory, "BlobBacked.mkv", blobId, nzbBlobId);

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.BlobNzbFiles.Add(new DavNzbFile
            {
                Id = blobId,
                SegmentIds = ["segment-1", "segment-2"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var snapshot = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        Assert.Contains("BlobBacked.mkv", snapshot);
        Assert.DoesNotContain("segment-1", snapshot);
        Assert.DoesNotContain("segment-2", snapshot);

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        var restoredItem = await restoredContext.Items.SingleAsync(x => x.Id == itemId);
        var restoredMetadata = await new DavDatabaseClient(restoredContext).GetDavNzbFileAsync(restoredItem);

        Assert.Equal(DavItem.ItemSubType.NzbFile, restoredItem.SubType);
        Assert.Equal(blobId, restoredItem.FileBlobId);
        Assert.Equal(nzbBlobId, restoredItem.NzbBlobId);
        Assert.NotNull(restoredMetadata);
        Assert.Equal(blobId, restoredMetadata.Id);
        Assert.Equal(["segment-1", "segment-2"], restoredMetadata.SegmentIds);
        Assert.Empty(await restoredContext.NzbFiles.Where(x => x.Id == itemId).ToListAsync());
    }

    [Fact]
    public async Task StartupRecovery_StartAsyncDoesNotWaitForLibraryLinkScan()
    {
        if (OperatingSystem.IsWindows()) return;

        var libraryPath = _fixture.CreateLibraryDirectory();
        var blockingStrmPath = Path.Join(libraryPath, "blocking.strm");
        await RunProcessAsync("mkfifo", blockingStrmPath);

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(Guid.NewGuid(), movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var recoveryService = new ContentIndexRecoveryService();
        var startTask = recoveryService.StartAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(500));
        var completedTask = await Task.WhenAny(startTask, timeoutTask);

        if (completedTask != startTask && !startTask.IsCompleted)
            await UnblockNamedPipeAsync(blockingStrmPath);

        Assert.Same(startTask, completedTask);
        await startTask;
        await recoveryService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupRecovery_DoesNotRestoreLinkedMissingItems_WhenDatabaseIsPartiallyMissing()
    {
        var linkedItemId = Guid.NewGuid();
        var missingDirectoryId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var tvDirectory = CreateDirectory("tv", DavItem.ContentFolder);
            var tvFile = CreateNzbFile(Guid.NewGuid(), tvDirectory, "Existing.mkv");
            var movieDirectory = CreateDirectory(missingDirectoryId, "movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(linkedItemId, movieDirectory, "Missing.mkv");

            dbContext.Items.AddRange(tvDirectory, tvFile, movieDirectory, movieFile);
            dbContext.NzbFiles.AddRange(
                new DavNzbFile { Id = tvFile.Id, SegmentIds = ["tv-segment"] },
                new DavNzbFile { Id = movieFile.Id, SegmentIds = ["movie-segment"] }
            );

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var libraryPath = _fixture.CreateLibraryDirectory();
        await File.WriteAllTextAsync(
            Path.Join(libraryPath, "Missing.strm"),
            $"http://localhost:3000/view/.ids/{linkedItemId}.mkv?downloadKey=test&extension=mkv"
        );

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM DavItems WHERE Id = {0}", linkedItemId);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM DavItems WHERE Id = {0}", missingDirectoryId);
        }

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        var restoredPaths = await restoredContext.Items
            .Where(x => x.Path.StartsWith(ContentPathUtil.ForwardSlashPrefix) || x.Path.StartsWith(ContentPathUtil.BackslashPrefix))
            .Select(x => ContentPathUtil.NormalizeSeparators(x.Path))
            .ToListAsync();

        Assert.DoesNotContain("/content/movies", restoredPaths);
        Assert.DoesNotContain("/content/movies/Missing.mkv", restoredPaths);
        Assert.Empty(await restoredContext.NzbFiles.Where(x => x.Id == linkedItemId).ToListAsync());
    }

    [Fact]
    public async Task StartupRecovery_IgnoresUnsupportedSnapshotVersion()
    {
        await _fixture.ResetAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(ContentIndexSnapshotStore.SnapshotFilePath)!);
        await File.WriteAllTextAsync(
            ContentIndexSnapshotStore.SnapshotFilePath,
            """{"Version":999,"GeneratedAtUtc":"2026-03-08T00:00:00+00:00","Items":[],"NzbFiles":[],"RarFiles":[],"MultipartFiles":[]}"""
        );

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            0,
            await restoredContext.Items.CountAsync(x =>
                x.Path.StartsWith(ContentPathUtil.ForwardSlashPrefix) || x.Path.StartsWith(ContentPathUtil.BackslashPrefix))
        );
    }

    [Fact]
    public async Task StartupRecovery_PropagatesRecoveryCancellation()
    {
        await _fixture.ResetAsync();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var recoveryService = new ContentIndexRecoveryService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recoveryService.RecoverAsync(cts.Token));
    }

    [Fact]
    public async Task SnapshotRead_PropagatesDeserializeCancellation()
    {
        await _fixture.ResetAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(ContentIndexSnapshotStore.SnapshotFilePath)!);
        await File.WriteAllTextAsync(
            ContentIndexSnapshotStore.SnapshotFilePath,
            """{"Version":1,"GeneratedAtUtc":"2026-03-08T00:00:00+00:00","Items":[],"NzbFiles":[],"RarFiles":[],"MultipartFiles":[]}"""
        );
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var method = typeof(ContentIndexSnapshotStore).GetMethod(
            "TryReadPathAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var readTask = (Task<ContentIndexSnapshotStore.SnapshotReadResult>)method.Invoke(
            null,
            [ContentIndexSnapshotStore.SnapshotFilePath, cts.Token])!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task SnapshotWriter_PropagatesFlushCancellation()
    {
        await _fixture.ResetAsync();
        ContentIndexSnapshotWriterService.RequestSnapshot();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContentIndexSnapshotWriterService.FlushNowAsync(cts.Token));
    }

    [Fact]
    public async Task SnapshotWriter_RetainsPendingRequestAfterCanceledFlush()
    {
        await _fixture.ResetAsync();
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        DeleteIfExists(ContentIndexSnapshotStore.SnapshotFilePath);
        DeleteIfExists(ContentIndexSnapshotStore.BackupSnapshotFilePath);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(Guid.NewGuid(), movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContentIndexSnapshotWriterService.FlushNowAsync(cts.Token));
        Assert.False(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));

        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        Assert.True(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));
    }

    [Fact]
    public async Task SnapshotWriter_RetainsPendingRequestAfterTransientWriteFailure()
    {
        await _fixture.ResetAsync();
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        DeleteIfExists(ContentIndexSnapshotStore.SnapshotFilePath);
        DeleteIfExists(ContentIndexSnapshotStore.BackupSnapshotFilePath);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(Guid.NewGuid(), movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
        }

        var field = typeof(ContentIndexSnapshotWriterService).GetField(
            "WriteSnapshotCore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var originalWriter = (Func<long, CancellationToken, Task>)field.GetValue(null)!;
        var attempts = 0;

        field.SetValue(null, new Func<long, CancellationToken, Task>((requestCount, cancellationToken) =>
        {
            attempts++;
            if (attempts == 1)
                throw new IOException("temporary filesystem failure");

            return originalWriter(requestCount, cancellationToken);
        }));

        try
        {
            ContentIndexSnapshotWriterService.RequestSnapshot();

            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

            Assert.Equal(1, attempts);
            Assert.False(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));

            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

            Assert.Equal(2, attempts);
            Assert.True(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));
        }
        finally
        {
            field.SetValue(null, originalWriter);
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SnapshotWriter_CoalescesManyRequestsIntoSingleWakeSignal()
    {
        await _fixture.ResetAsync();
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        for (var i = 0; i < 100; i++)
            ContentIndexSnapshotWriterService.RequestSnapshot();

        Assert.Equal(1, DrainSnapshotRequestSignals());

        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        Assert.Equal(0, DrainSnapshotRequestSignals());
    }

    [Fact]
    public async Task SnapshotWriter_ReplacesDirectoryAtBackupSnapshotPath()
    {
        await _fixture.ResetAsync();
        DeleteIfExists(ContentIndexSnapshotStore.SnapshotFilePath);
        DeleteDirectoryIfExists(ContentIndexSnapshotStore.BackupSnapshotFilePath);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(Guid.NewGuid(), movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
        }

        Directory.CreateDirectory(ContentIndexSnapshotStore.BackupSnapshotFilePath);
        ContentIndexSnapshotWriterService.RequestSnapshot();

        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        Assert.True(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));
        Assert.False(Directory.Exists(ContentIndexSnapshotStore.BackupSnapshotFilePath));
        Assert.True(File.Exists(ContentIndexSnapshotStore.BackupSnapshotFilePath));
    }

    [Fact]
    public async Task SnapshotWriter_ReplacesStaleDirectoryAtTempSnapshotPath()
    {
        await _fixture.ResetAsync();
        var tempSnapshotPath = ContentIndexSnapshotStore.SnapshotFilePath + ".tmp";
        DeleteDirectoryIfExists(tempSnapshotPath);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(Guid.NewGuid(), movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
        }

        Directory.CreateDirectory(tempSnapshotPath);
        ContentIndexSnapshotWriterService.RequestSnapshot();

        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        Assert.True(File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));
        Assert.False(Directory.Exists(tempSnapshotPath));
        Assert.False(File.Exists(tempSnapshotPath));
    }

    [Fact]
    public async Task SnapshotWriter_DoesNotInlineBlobBackedArchiveMetadata()
    {
        await _fixture.ResetAsync();
        var blobId = Guid.NewGuid();

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = DavItem.New(
                Guid.NewGuid(),
                movieDirectory,
                "BrokenArchive.mkv",
                fileSize: 1024,
                DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.RarFile,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: null,
                fileBlobId: blobId,
                nzbBlobId: null);

            dbContext.Items.AddRange(movieDirectory, movieFile);
            await dbContext.SaveChangesAsync();
            await BlobStore.WriteBlob(blobId, new DavRarFile
            {
                Id = movieFile.Id,
                RarParts = null!
            });

            ContentIndexSnapshotWriterService.RequestSnapshot();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var snapshot = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        Assert.Contains("BrokenArchive.mkv", snapshot);
        Assert.DoesNotContain("RarParts", snapshot);
    }

    [Fact]
    public async Task StartupRecovery_FallsBackToBackupSnapshot_WhenPrimarySnapshotIsCorrupt()
    {
        var expectedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(expectedItemId, movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        await File.WriteAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath, "not-json");
        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        Assert.Single(await restoredContext.NzbFiles.Where(x => x.Id == expectedItemId).ToListAsync());
    }

    [Fact]
    public async Task StartupRecovery_DoesNotBringBackDeletedItems_WhenSnapshotWasUpdatedAfterDeletion()
    {
        var deletedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(deletedItemId, movieDirectory, "Deleted.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
            dbContext.Items.Remove(movieFile);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        Assert.DoesNotContain(await restoredContext.Items.Select(x => x.Id).ToListAsync(), x => x == deletedItemId);
    }

    [Fact]
    public async Task SnapshotWriter_PrunesItemsWithMissingMetadata_WhenMetadataRowsDisappear()
    {
        var expectedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(expectedItemId, movieDirectory, "Example.mkv");

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = movieFile.Id,
                SegmentIds = ["segment-1", "segment-2"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var originalSnapshot = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            dbContext.NzbFiles.Remove(await dbContext.NzbFiles.SingleAsync(x => x.Id == expectedItemId));
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var snapshotAfterCorruption = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        Assert.NotEqual(originalSnapshot, snapshotAfterCorruption);
        Assert.DoesNotContain(expectedItemId.ToString(), snapshotAfterCorruption);
    }

    [Fact]
    public async Task SnapshotWriter_KeepsBlobBackedItemsCompactWhenBlobIsMissing()
    {
        var itemId = Guid.NewGuid();
        var blobId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var movieDirectory = CreateDirectory("movies", DavItem.ContentFolder);
            var movieFile = CreateNzbFile(itemId, movieDirectory, "BlobBacked.mkv", blobId, nzbBlobId: null);

            dbContext.Items.AddRange(movieDirectory, movieFile);
            dbContext.BlobNzbFiles.Add(new DavNzbFile
            {
                Id = blobId,
                SegmentIds = ["segment-1"],
            });

            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        }

        var originalSnapshot = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        BlobStore.Delete(blobId);
        ContentIndexSnapshotWriterService.RequestSnapshot();

        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

        var snapshotAfterBlobReadFailure = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        Assert.NotEqual(originalSnapshot, snapshotAfterBlobReadFailure);
        Assert.Contains(itemId.ToString(), snapshotAfterBlobReadFailure);
        Assert.DoesNotContain("segment-1", snapshotAfterBlobReadFailure);
    }

    private static DavItem CreateDirectory(string name, DavItem parent)
    {
        return CreateDirectory(Guid.NewGuid(), name, parent);
    }

    private static int DrainSnapshotRequestSignals()
    {
        var field = typeof(ContentIndexSnapshotWriterService).GetField(
            "Requests",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var channel = Assert.IsAssignableFrom<Channel<byte>>(field.GetValue(null));
        var count = 0;
        while (channel.Reader.TryRead(out _))
            count++;
        return count;
    }

    private static DavItem CreateDirectory(Guid id, string name, DavItem parent)
    {
        return DavItem.New(
            id,
            parent,
            name,
            null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            null,
            null,
            null,
            null
        );
    }

    private static DavItem CreateNzbFile(Guid id, DavItem parent, string name)
    {
        return CreateNzbFile(id, parent, name, fileBlobId: null, nzbBlobId: null);
    }

    private static DavItem CreateNzbFile(Guid id, DavItem parent, string name, Guid? fileBlobId, Guid? nzbBlobId)
    {
        return DavItem.New(
            id,
            parent,
            name,
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            fileBlobId,
            nzbBlobId
        );
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static async Task UnblockNamedPipeAsync(string path)
    {
        await RunShellAsync("printf '\\n' > \"$1\"", path);
    }

    private static async Task RunProcessAsync(string fileName, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} failed with exit code {process.ExitCode}: {stderr}");
    }

    private static async Task RunShellAsync(string script, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("sh")
            {
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add("sh");
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"shell command failed with exit code {process.ExitCode}: {stderr}");
    }
}

public sealed class ContentIndexDatabaseFixture : IAsyncLifetime
{
    private readonly string _configPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "content-index-recovery");

    public ContentIndexDatabaseFixture()
    {
        RestoreEnvironment();
    }

    public Task InitializeAsync()
    {
        RestoreEnvironment();
        Directory.CreateDirectory(_configPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return ResetAsync();
    }

    public async Task<DavDatabaseContext> ResetAndCreateMigratedContextAsync()
    {
        await ResetAsync();
        return await CreateMigratedContextAsync();
    }

    public async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        RestoreEnvironment();
        Directory.CreateDirectory(_configPath);
        var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }

    public async Task RecreateDatabaseAsync()
    {
        await EnsureCleanDatabaseAsync(deleteSnapshots: false);
    }

    public async Task ResetAsync()
    {
        await EnsureCleanDatabaseAsync(deleteSnapshots: true);
        DeleteDirectoryIfExists(Path.Join(_configPath, "library"));
    }

    public string CreateLibraryDirectory()
    {
        var libraryPath = Path.Join(_configPath, "library", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        return libraryPath;
    }

    public ConfigManager CreateConfigManager(string? libraryPath = null)
    {
        var configManager = new ConfigManager();
        var items = new List<ConfigItem>
        {
            new() { ConfigName = "rclone.mount-dir", ConfigValue = "/mnt/nzbdav" }
        };

        if (libraryPath != null)
        {
            items.Add(new ConfigItem
            {
                ConfigName = "media.library-dir",
                ConfigValue = libraryPath
            });
        }

        configManager.UpdateValues(items);
        return configManager;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private async Task EnsureCleanDatabaseAsync(bool deleteSnapshots)
    {
        RestoreEnvironment();
        Directory.CreateDirectory(_configPath);

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
        await dbContext.ArrDownloadLifecycleEvents.ExecuteDeleteAsync();
        await dbContext.ArrSearchNudgeCommands.ExecuteDeleteAsync();
        await dbContext.ArrDownloadCorrelations.ExecuteDeleteAsync();
        await dbContext.QueuePriorityHints.ExecuteDeleteAsync();
        await dbContext.RepairBrokenFiles.ExecuteDeleteAsync();
        await dbContext.RepairEntryHealth.ExecuteDeleteAsync();
        await dbContext.RepairRuns.ExecuteDeleteAsync();
        await dbContext.HealthCheckResults.ExecuteDeleteAsync();
        await dbContext.HealthCheckStats.ExecuteDeleteAsync();
        await dbContext.WorkerJobs.ExecuteDeleteAsync();
        await dbContext.QueueNzbContents.ExecuteDeleteAsync();
        await dbContext.QueueItems.ExecuteDeleteAsync();
        await dbContext.RcloneInvalidationItems.ExecuteDeleteAsync();
        await dbContext.BlobCleanupItems.ExecuteDeleteAsync();
        await dbContext.HistoryCleanupItems.ExecuteDeleteAsync();
        await dbContext.HistoryItems.ExecuteDeleteAsync();
        await dbContext.Items
            .Where(x => x.Path.StartsWith(ContentPathUtil.ForwardSlashPrefix) || x.Path.StartsWith(ContentPathUtil.BackslashPrefix))
            .ExecuteDeleteAsync();

        if (!deleteSnapshots) return;

        DeleteIfExists(ContentIndexSnapshotStore.SnapshotFilePath);
        DeleteIfExists(ContentIndexSnapshotStore.BackupSnapshotFilePath);
        DeleteDirectoryIfExists(Path.Join(_configPath, "blobs"));
    }

    public void RestoreEnvironment()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", "sqlite");
        Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", null);
    }
}

[CollectionDefinition(nameof(ContentIndexDatabaseCollection), DisableParallelization = true)]
public sealed class ContentIndexDatabaseCollection : ICollectionFixture<ContentIndexDatabaseFixture>
{
}

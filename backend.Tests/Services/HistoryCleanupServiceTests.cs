using System.Reflection;
using Microsoft.EntityFrameworkCore;
using backend.Tests.Services;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class HistoryCleanupServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public HistoryCleanupServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeleteMountedFilesPersistsContentSnapshotBeforeQueueDrains()
    {
        var historyItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var historyItem = new HistoryItem
            {
                Id = historyItemId,
                JobName = "Removed",
                FileName = "Removed.nzb",
                Category = "tv",
                CreatedAt = DateTime.UtcNow,
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed
            };
            var directory = CreateDirectory(Guid.NewGuid(), DavItem.ContentFolder.Id, "/content/Removed");
            var file = CreateFile(Guid.NewGuid(), directory.Id, "/content/Removed/Episode.mkv", historyItemId);

            dbContext.HistoryItems.Add(historyItem);
            dbContext.Items.AddRange(directory, file);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = file.Id,
                SegmentIds = ["segment-1"]
            });
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

            dbContext.HistoryCleanupItems.Add(new HistoryCleanupItem
            {
                Id = historyItemId,
                DeleteMountedFiles = true
            });
            await dbContext.SaveChangesAsync();
        }

        using var service = new HistoryCleanupService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitForCleanupQueueToDrainAsync(timeout.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var recoveredPaths = await assertionContext.Items
            .AsNoTracking()
            .Select(x => x.Path)
            .ToListAsync();

        Assert.DoesNotContain("/content/Removed/Episode.mkv", recoveredPaths);
    }

    [Fact]
    public async Task DeleteMountedFilesRetainsCleanupUntilRetrySnapshotPersists()
    {
        EnableRcloneRemoteControl();
        try
        {
            var historyItemId = Guid.NewGuid();

            await using (var dbContext = await _fixture.ResetAndCreateMigratedContextAsync())
            {
                var historyItem = new HistoryItem
                {
                    Id = historyItemId,
                    JobName = "Removed",
                    FileName = "Removed.nzb",
                    Category = "tv",
                    CreatedAt = DateTime.UtcNow,
                    DownloadStatus = HistoryItem.DownloadStatusOption.Completed
                };
                var directory = CreateDirectory(Guid.NewGuid(), DavItem.ContentFolder.Id, "/content/Removed");
                var file = CreateFile(Guid.NewGuid(), directory.Id, "/content/Removed/Episode.mkv", historyItemId);

                dbContext.HistoryItems.Add(historyItem);
                dbContext.Items.AddRange(directory, file);
                dbContext.NzbFiles.Add(new DavNzbFile
                {
                    Id = file.Id,
                    SegmentIds = ["segment-1"]
                });
                await dbContext.SaveChangesAsync();
                ContentIndexSnapshotWriterService.RequestSnapshot();
                await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);

                dbContext.HistoryCleanupItems.Add(new HistoryCleanupItem
                {
                    Id = historyItemId,
                    DeleteMountedFiles = true
                });
                await dbContext.SaveChangesAsync();
            }

            var field = typeof(ContentIndexSnapshotWriterService).GetField(
                "WriteSnapshotCore",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            var originalWriter = (Func<long, CancellationToken, Task>)field.GetValue(null)!;
            var secondFlushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowSecondFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var delayCalls = 0;

            field.SetValue(null, new Func<long, CancellationToken, Task>(async (requestCount, cancellationToken) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new IOException("temporary filesystem failure");

                secondFlushStarted.TrySetResult();
                await allowSecondFlush.Task;
                await originalWriter(requestCount, cancellationToken);
            }));

            using var service = new HistoryCleanupService((delay, cancellationToken) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(10), delay);
                Interlocked.Increment(ref delayCalls);
                return Task.CompletedTask;
            });
            try
            {
                await service.StartAsync(CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await secondFlushStarted.Task.WaitAsync(timeout.Token);

                await using (var assertionContext = await _fixture.CreateMigratedContextAsync())
                {
                    Assert.True(await assertionContext.HistoryCleanupItems.AnyAsync(x => x.Id == historyItemId, timeout.Token));
                    Assert.False(await assertionContext.Items.AnyAsync(x => x.HistoryItemId == historyItemId, timeout.Token));
                    Assert.NotEmpty(await assertionContext.RcloneInvalidationItems.ToListAsync(timeout.Token));
                }

                Assert.Equal(1, Volatile.Read(ref delayCalls));
                allowSecondFlush.TrySetResult();
                await WaitForCleanupQueueToDrainAsync(timeout.Token);
                Assert.Equal(2, attempts);
            }
            finally
            {
                allowSecondFlush.TrySetResult();
                await service.StopAsync(CancellationToken.None);
                field.SetValue(null, originalWriter);
                await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
            }
        }
        finally
        {
            RcloneClient.Initialize(new ConfigManager());
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

    private async Task WaitForCleanupQueueToDrainAsync(CancellationToken ct)
    {
        while (true)
        {
            await using var dbContext = await _fixture.CreateMigratedContextAsync();
            if (!await dbContext.HistoryCleanupItems.AnyAsync(ct))
                return;

            await Task.Delay(25, ct);
        }
    }

    private static DavItem CreateDirectory(Guid id, Guid parentId, string path)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = path
        };
    }

    private static DavItem CreateFile(Guid id, Guid parentId, string path, Guid historyItemId)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = path,
            HistoryItemId = historyItemId
        };
    }
}

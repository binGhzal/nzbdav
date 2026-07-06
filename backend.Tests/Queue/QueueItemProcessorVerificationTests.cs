using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestDoubles;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Queue;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class QueueItemProcessorVerificationTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public QueueItemProcessorVerificationTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessAsync_FailsWhenQueueNzbStreamIsMissing()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                queueItem,
                queueNzbStream: null,
                dbClient,
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.Completed, outcome);
        Assert.Equal(0, await dbContext.QueueItems.CountAsync());
        var historyItem = await dbContext.HistoryItems.SingleAsync();
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Equal("The NZB file is missing from the queue store.", historyItem.FailMessage);
        Assert.Null(queueItem.PauseUntil);
    }

    [Fact]
    public async Task ProcessAsync_FailsWhenQueueNzbStreamReadFails()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();

        var outcome = await new QueueItemProcessor(
                queueItem,
                new FailingReadStream(new IOException("queue blob read failure")),
                dbClient,
                usenetClient,
                configManager,
                new WebsocketManager(),
                new ArrDownloadReportService(configManager),
                new Progress<int>(),
                CancellationToken.None)
            .ProcessAsync();

        Assert.Equal(QueueItemProcessor.ProcessingOutcome.Completed, outcome);
        Assert.Equal(0, await dbContext.QueueItems.CountAsync());
        var historyItem = await dbContext.HistoryItems.SingleAsync();
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, historyItem.DownloadStatus);
        Assert.Equal("The NZB file could not be read from the queue store.", historyItem.FailMessage);
        Assert.Null(queueItem.PauseUntil);
    }

    [Theory]
    [InlineData("Movie.mkv", true)]
    [InlineData("Movie.iso", true)]
    [InlineData("Archive.rar", false)]
    [InlineData("Archive.r00", false)]
    [InlineData("Archive.7z.001", false)]
    [InlineData("Movie.mkv.001", false)]
    [InlineData("Subtitle.srt", false)]
    [InlineData("poster.jpg", false)]
    [InlineData("notes.txt", false)]
    public void ShouldEnqueuePostDownloadVerify_OnlyIncludesPlayableMediaOutputs(string fileName, bool expected)
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            IdPrefix = "abcde",
            CreatedAt = DateTime.UtcNow,
            Name = fileName,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{fileName}"
        };

        Assert.Equal(expected, InvokeShouldEnqueuePostDownloadVerify(davItem));
    }

    [Fact]
    public void ShouldEnqueuePostDownloadVerify_ExcludesDirectories()
    {
        var davItem = new DavItem
        {
            Id = Guid.NewGuid(),
            IdPrefix = "abcde",
            CreatedAt = DateTime.UtcNow,
            Name = "Movie.mkv",
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = "/content/Movie"
        };

        Assert.False(InvokeShouldEnqueuePostDownloadVerify(davItem));
    }

    [Theory]
    [InlineData(QueueItem.PriorityOption.Low, 50)]
    [InlineData(QueueItem.PriorityOption.Normal, 50)]
    [InlineData(QueueItem.PriorityOption.High, 50)]
    [InlineData(QueueItem.PriorityOption.Force, 100)]
    public void GetPostDownloadVerifyPriority_UsesPositivePriorityForAllPostDownloadVerifyJobs(
        QueueItem.PriorityOption priority,
        int expected)
    {
        Assert.Equal(expected, InvokeGetPostDownloadVerifyPriority(priority));
    }

    [Theory]
    [InlineData(QueueItem.PriorityOption.Low)]
    [InlineData(QueueItem.PriorityOption.Normal)]
    public async Task EnqueuePostDownloadVerifyJob_UsesHighOperationalPriorityIndependentOfDownloadPriority(
        QueueItem.PriorityOption priority)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem(priority);
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var mountFolder = CreateDavItem(
            "Example Movie",
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            queueItem.Id);
        var video = CreateDavItem(
            "Movie.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);

        dbContext.Items.AddRange(mountFolder, video);
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            dbClient,
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await InvokeEnqueuePostDownloadVerifyJobAsync(processor, mountFolder);

        var workerJob = Assert.Single(dbContext.ChangeTracker
            .Entries<WorkerJob>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity));
        Assert.Equal(50, workerJob.Priority);
    }

    [Fact]
    public async Task EnqueuePostDownloadVerifyJob_TargetsCompletedMountFolderOnce()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem();
        var dbClient = new DavDatabaseClient(dbContext);
        var configManager = _fixture.CreateConfigManager();
        using var usenetClient = new FakeNntpClient();
        var mountFolder = CreateDavItem(
            "Example Movie",
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            queueItem.Id);
        var firstVideo = CreateDavItem(
            "First.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);
        var secondVideo = CreateDavItem(
            "Second.mkv",
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            queueItem.Id);

        dbContext.Items.AddRange(mountFolder, firstVideo, secondVideo);
        var processor = new QueueItemProcessor(
            queueItem,
            Stream.Null,
            dbClient,
            usenetClient,
            configManager,
            new WebsocketManager(),
            new ArrDownloadReportService(configManager),
            new Progress<int>(),
            CancellationToken.None);

        await InvokeEnqueuePostDownloadVerifyJobAsync(processor, mountFolder);

        var workerJob = Assert.Single(dbContext.ChangeTracker
            .Entries<WorkerJob>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity));
        Assert.Equal(WorkerJob.JobKind.Verify, workerJob.Kind);
        Assert.Equal(mountFolder.Id, workerJob.TargetId);
        Assert.Contains("\"Kind\":\"post_download_verify\"", workerJob.PayloadJson);
    }

    private static bool InvokeShouldEnqueuePostDownloadVerify(DavItem davItem)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "ShouldEnqueuePostDownloadVerify",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [davItem])!;
    }

    private static int InvokeGetPostDownloadVerifyPriority(QueueItem.PriorityOption priority)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "GetPostDownloadVerifyPriority",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (int)method.Invoke(null, [priority])!;
    }

    private static async Task InvokeEnqueuePostDownloadVerifyJobAsync(
        QueueItemProcessor processor,
        DavItem mountFolder)
    {
        var method = typeof(QueueItemProcessor).GetMethod(
            "EnqueuePostDownloadVerifyJobAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(processor, [mountFolder])!;
        await task.ConfigureAwait(false);
    }

    private static QueueItem CreateQueueItem(QueueItem.PriorityOption priority = QueueItem.PriorityOption.Normal)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.Movie.nzb",
            JobName = "Example Movie",
            NzbFileSize = 100,
            TotalSegmentBytes = 1024,
            Category = "movies",
            Priority = priority,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
    }

    private static DavItem CreateDavItem(
        string name,
        DavItem.ItemType itemType,
        DavItem.ItemSubType subType,
        Guid historyItemId)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = name,
            Type = itemType,
            SubType = subType,
            Path = $"/content/{name}",
            HistoryItemId = historyItemId
        };
    }

    private sealed class FailingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw exception;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromException<int>(exception);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromException<int>(exception);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

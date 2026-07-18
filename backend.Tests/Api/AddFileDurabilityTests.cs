using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class AddFileDurabilityTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public AddFileDurabilityTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MalformedNzbLeavesDurableCleanupIntentForWrittenBlob()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await ClearNzbCleanupStateAsync(dbContext);
        var configManager = _fixture.CreateConfigManager();
        var controller = new AddFileController(
            new DefaultHttpContext(),
            new DavDatabaseClient(dbContext),
            queueManager: null!,
            configManager,
            websocketManager: null!,
            arrDownloadReportService: null!,
            new ArrOperationsService(configManager),
            new NzbBlobIngestCoordinator());

        await Assert.ThrowsAsync<XmlException>(() => controller.AddFileAsync(CreateRequest("<nzb><broken>")));

        dbContext.ChangeTracker.Clear();
        var intent = await dbContext.NzbBlobCleanupItems.AsNoTracking().SingleAsync();
        Assert.NotNull(BlobStore.ReadBlob(intent.Id));
    }

    [Fact]
    public async Task CleanupIntentIsCommittedBeforeFirstBlobRead()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await ClearNzbCleanupStateAsync(dbContext);
        var configManager = _fixture.CreateConfigManager();
        var coordinator = new NzbBlobIngestCoordinator();
        var websocketManager = new WebsocketManager();
        using var queueManager = CreateQueueManager(configManager, websocketManager);
        var controller = CreateController(
            dbContext,
            configManager,
            queueManager,
            websocketManager,
            coordinator);
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new BlockingFirstReadStream(
            Encoding.UTF8.GetBytes(ValidNzb),
            firstRead,
            releaseRead);
        var addTask = controller.AddFileAsync(CreateRequest(stream));

        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await using var assertionContext = new DavDatabaseContext();
            Assert.Equal(1, await assertionContext.NzbBlobCleanupItems.AsNoTracking().CountAsync());
            Assert.Equal(0, await assertionContext.QueueItems.AsNoTracking().CountAsync());
        }
        finally
        {
            releaseRead.TrySetResult();
        }
        var response = await addTask;

        Assert.True(response.Status);
    }

    [Fact]
    public async Task AcceptanceSaveFailureLeavesIntentAndNoQueueReference()
    {
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            await ClearNzbCleanupStateAsync(setup);
        }

        await using var dbContext = CreateContext(new FailSecondSaveInterceptor());
        var configManager = _fixture.CreateConfigManager();
        var coordinator = new NzbBlobIngestCoordinator();
        var websocketManager = new WebsocketManager();
        using var queueManager = CreateQueueManager(configManager, websocketManager);
        var controller = CreateController(
            dbContext,
            configManager,
            queueManager,
            websocketManager,
            coordinator);

        await Assert.ThrowsAsync<InjectedAcceptanceException>(() =>
            controller.AddFileAsync(CreateRequest(ValidNzb)));

        await using var assertionContext = new DavDatabaseContext();
        var intent = await assertionContext.NzbBlobCleanupItems.AsNoTracking().SingleAsync();
        Assert.Empty(await assertionContext.QueueItems.AsNoTracking().ToListAsync());
        Assert.Empty(await assertionContext.NzbNames.AsNoTracking().ToListAsync());
        Assert.Empty(await assertionContext.WorkerJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await assertionContext.ArrDownloadLifecycleEvents.AsNoTracking().ToListAsync());
        Assert.NotNull(BlobStore.ReadBlob(intent.Id));
    }

    [Fact]
    public async Task PostCommitAcknowledgementFailureKeepsCommittedQueueBlob()
    {
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            await ClearNzbCleanupStateAsync(setup);
        }

        await using var dbContext = CreateContext(new ThrowAfterSecondCommittedSaveInterceptor());
        var configManager = _fixture.CreateConfigManager();
        var coordinator = new NzbBlobIngestCoordinator();
        var websocketManager = new WebsocketManager();
        using var queueManager = CreateQueueManager(configManager, websocketManager);
        var controller = CreateController(
            dbContext,
            configManager,
            queueManager,
            websocketManager,
            coordinator);

        await Assert.ThrowsAsync<InjectedAcknowledgementException>(() =>
            controller.AddFileAsync(CreateRequest(ValidNzb)));

        await using var assertionContext = new DavDatabaseContext();
        var queueItem = await assertionContext.QueueItems.AsNoTracking().SingleAsync();
        Assert.Empty(await assertionContext.NzbBlobCleanupItems.AsNoTracking().ToListAsync());
        Assert.Equal(queueItem.Id, (await assertionContext.NzbNames.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(queueItem.Id, (await assertionContext.WorkerJobs.AsNoTracking().SingleAsync()).TargetId);
        Assert.Equal(queueItem.Id, (await assertionContext.ArrDownloadLifecycleEvents.AsNoTracking().SingleAsync()).QueueItemId);
        Assert.NotNull(BlobStore.ReadBlob(queueItem.Id));
    }

    private static AddFileController CreateController(
        DavDatabaseContext dbContext,
        ConfigManager configManager,
        QueueManager queueManager,
        WebsocketManager websocketManager,
        NzbBlobIngestCoordinator coordinator)
    {
        return new AddFileController(
            new DefaultHttpContext(),
            new DavDatabaseClient(dbContext),
            queueManager,
            configManager,
            websocketManager,
            new ArrDownloadReportService(configManager),
            new ArrOperationsService(configManager),
            coordinator);
    }

    private static QueueManager CreateQueueManager(
        ConfigManager configManager,
        WebsocketManager websocketManager)
    {
        return new QueueManager(
            new UsenetStreamingClient(configManager, websocketManager),
            configManager,
            new QueueWorkLaneCoordinator(),
            websocketManager,
            new ArrDownloadReportService(configManager));
    }

    private static DavDatabaseContext CreateContext(SaveChangesInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>(
                DavDatabaseContext.CreateSqliteOptions(enforceProviderSelection: true))
            .AddInterceptors(interceptor)
            .Options;
        return new DavDatabaseContext(options);
    }

    private static async Task ClearNzbCleanupStateAsync(DavDatabaseContext dbContext)
    {
        await dbContext.NzbBlobCleanupItems.ExecuteDeleteAsync();
        await dbContext.NzbNames.ExecuteDeleteAsync();
    }

    private static AddFileRequest CreateRequest(string content)
    {
        return CreateRequest(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private static AddFileRequest CreateRequest(Stream stream)
    {
        return new AddFileRequest
        {
            FileName = "Example.nzb",
            Category = "movies",
            NzbFileStream = stream,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };
    }

    private const string ValidNzb =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file poster="poster" date="1" subject="Example">
            <segments>
              <segment bytes="10" number="1">segment-1</segment>
            </segments>
          </file>
        </nzb>
        """;

    private sealed class FailSecondSaveInterceptor : SaveChangesInterceptor
    {
        private int _attempt;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempt) == 2)
                throw new InjectedAcceptanceException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowAfterSecondCommittedSaveInterceptor : SaveChangesInterceptor
    {
        private int _attempt;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempt) == 2)
                throw new InjectedAcknowledgementException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingFirstReadStream(
        byte[] content,
        TaskCompletionSource firstRead,
        TaskCompletionSource releaseRead) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        private int _blocked;

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                firstRead.TrySetResult();
                await releaseRead.Task.WaitAsync(cancellationToken);
            }

            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InjectedAcceptanceException : Exception;
    private sealed class InjectedAcknowledgementException : Exception;
}

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueManager : IDisposable
{
    private static readonly TimeSpan DownloadWorkerLeaseDuration = TimeSpan.FromMinutes(15);
    private const int DownloadWorkerRetryMaxAttempts = int.MaxValue;

    private readonly Dictionary<Guid, InProgressQueueItem> _inProgressQueueItems = new();

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly Lock _inProgressQueueItemsLock = new();

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _websocketManager = websocketManager;
        _cancellationTokenSource = CancellationTokenSource
            .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }

    public (QueueItem? queueItem, int? progress) GetInProgressQueueItem()
    {
        var inProgressQueueItem = GetInProgressQueueItems().FirstOrDefault();
        return (inProgressQueueItem.queueItem, inProgressQueueItem.progress);
    }

    public List<(QueueItem queueItem, int progress)> GetInProgressQueueItems()
    {
        lock (_inProgressQueueItemsLock)
        {
            return _inProgressQueueItems
                .Values
                .OrderByDescending(x => x.QueueItem.Priority)
                .ThenBy(x => x.QueueItem.CreatedAt)
                .Select(x => (x.QueueItem, x.ProgressPercentage))
                .ToList();
        }
    }

    public void AwakenQueue(DateTime? dateTime = null)
    {
        TimeSpan? cancelAfter = dateTime.HasValue ? (dateTime.Value - DateTime.Now) : null;
        lock (_sleepingQueueLock)
        {
            if (cancelAfter.HasValue && cancelAfter.Value > TimeSpan.Zero)
                _sleepingQueueToken.CancelAfter(cancelAfter.Value);
            else
                _sleepingQueueToken.Cancel();
        }
    }

    public async Task RemoveQueueItemsAsync
    (
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        List<InProgressQueueItem> inProgressQueueItems;
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var queueItemIdsSet = queueItemIds.ToHashSet();
            lock (_inProgressQueueItemsLock)
            {
                inProgressQueueItems = _inProgressQueueItems
                    .Where(x => queueItemIdsSet.Contains(x.Key))
                    .Select(x => x.Value)
                    .ToList();
            }

            foreach (var inProgressQueueItem in inProgressQueueItems)
                inProgressQueueItem.CancellationTokenSource.Cancel();

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _queueLock.Release();
        }

        await Task.WhenAll(inProgressQueueItems.Select(x => x.ProcessingTask)).ConfigureAwait(false);
        AwakenQueue();
    }

    public void UpdateInProgressQueueItemsPriority
    (
        List<Guid> queueItemIds,
        QueueItem.PriorityOption priority
    )
    {
        var queueItemIdsSet = queueItemIds.ToHashSet();
        lock (_inProgressQueueItemsLock)
        {
            foreach (var inProgressQueueItem in _inProgressQueueItems.Values)
            {
                if (queueItemIdsSet.Contains(inProgressQueueItem.QueueItem.Id))
                    inProgressQueueItem.QueueItem.Priority = priority;
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_configManager.IsQueuePaused())
                {
                    await WaitForQueueWorkAsync().ConfigureAwait(false);
                    continue;
                }

                var startedItem = await TryStartNextQueueItemAsync(ct).ConfigureAwait(false);
                if (startedItem) continue;

                await WaitForQueueWorkAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error($"An unexpected error occured while processing the queue: {e.Message}");
            }
        }
    }

    private async Task WaitForQueueWorkAsync()
    {
        try
        {
            // If every worker slot is busy, paused, or the queue is empty, wait briefly.
            // New NZBs, cancellations, config changes, and completed workers awaken the queue early.
            await Task.Delay(TimeSpan.FromMinutes(1), _sleepingQueueToken.Token).ConfigureAwait(false);
        }
        catch when (_sleepingQueueToken.IsCancellationRequested)
        {
            lock (_sleepingQueueLock)
            {
                if (!_sleepingQueueToken.TryReset())
                {
                    _sleepingQueueToken.Dispose();
                    _sleepingQueueToken = new CancellationTokenSource();
                }
            }
        }
    }

    private async Task<bool> TryStartNextQueueItemAsync(CancellationToken ct)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var activeIds = GetInProgressQueueItemIds();
            if (activeIds.Count >= _configManager.GetAdaptiveMaxConcurrentQueueDownloads())
                return false;

            var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var topItem = await dbClient.LeaseTopQueueItemAsync(
                    activeIds,
                    owner: $"{Environment.MachineName}:{Environment.ProcessId}:download",
                    leaseDuration: DownloadWorkerLeaseDuration,
                    ct: ct)
                .ConfigureAwait(false);
            if (topItem.queueItem is null)
            {
                await dbContext.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            var cachingUsenetClient = new ArticleCachingNntpClient(
                _usenetClient,
                maxCacheBytes: _configManager.GetArticleCacheMaxBytesPerQueueWorker(),
                sharedMaxCacheBytes: _configManager.GetArticleCacheMaxBytes());
            var queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var inProgressQueueItem = BeginProcessingQueueItem(
                dbContext,
                dbClient,
                cachingUsenetClient,
                topItem.queueItem,
                topItem.queueNzbStream,
                topItem.workerJob!,
                queueItemCancellationTokenSource);

            lock (_inProgressQueueItemsLock)
            {
                _inProgressQueueItems[topItem.queueItem.Id] = inProgressQueueItem;
            }

            return true;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private List<Guid> GetInProgressQueueItemIds()
    {
        lock (_inProgressQueueItemsLock)
        {
            return _inProgressQueueItems.Keys.ToList();
        }
    }

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseContext dbContext,
        DavDatabaseClient dbClient,
        ArticleCachingNntpClient usenetClient,
        QueueItem queueItem,
        Stream? queueNzbStream,
        WorkerJob workerJob,
        CancellationTokenSource cts
    )
    {
        var progressHook = new Progress<int>();
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProgressPercentage = 0,
            CancellationTokenSource = cts,
            DbContext = dbContext,
            NzbStream = queueNzbStream,
            UsenetClient = usenetClient,
            WorkerJob = workerJob
        };
        inProgressQueueItem.ProcessingTask = RunQueueItemAsync(inProgressQueueItem, dbClient, progressHook);
        var debounce = DebounceUtil.CreateDebounce();
        progressHook.ProgressChanged += (_, progress) =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        };
        return inProgressQueueItem;
    }

    private async Task RunQueueItemAsync
    (
        InProgressQueueItem inProgressQueueItem,
        DavDatabaseClient dbClient,
        IProgress<int> progressHook
    )
    {
        try
        {
            var outcome = await new QueueItemProcessor(
                inProgressQueueItem.QueueItem,
                inProgressQueueItem.NzbStream,
                dbClient,
                inProgressQueueItem.UsenetClient,
                _configManager,
                _websocketManager,
                progressHook,
                inProgressQueueItem.CancellationTokenSource.Token
            ).ProcessAsync().ConfigureAwait(false);
            await UpdateDownloadWorkerJobAsync(inProgressQueueItem, dbClient, outcome)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_inProgressQueueItemsLock)
            {
                _inProgressQueueItems.Remove(inProgressQueueItem.QueueItem.Id);
            }

            await inProgressQueueItem.DisposeAsync().ConfigureAwait(false);
            AwakenQueue();
        }
    }

    private static async Task UpdateDownloadWorkerJobAsync
    (
        InProgressQueueItem inProgressQueueItem,
        DavDatabaseClient dbClient,
        QueueItemProcessor.ProcessingOutcome outcome
    )
    {
        if (outcome == QueueItemProcessor.ProcessingOutcome.Completed)
        {
            await dbClient.CompleteWorkerJobAsync(inProgressQueueItem.WorkerJob).ConfigureAwait(false);
            return;
        }

        if (outcome == QueueItemProcessor.ProcessingOutcome.RetryScheduled)
        {
            var nextAttemptAt = inProgressQueueItem.QueueItem.PauseUntil.HasValue
                ? new DateTimeOffset(inProgressQueueItem.QueueItem.PauseUntil.Value)
                : DateTimeOffset.UtcNow.AddMinutes(1);
            await dbClient.FailWorkerJobAsync(
                    inProgressQueueItem.WorkerJob,
                    error: "Download retry scheduled.",
                    nextAttemptAt: nextAttemptAt,
                    maxAttempts: DownloadWorkerRetryMaxAttempts)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        lock (_inProgressQueueItemsLock)
        {
            foreach (var inProgressQueueItem in _inProgressQueueItems.Values)
                inProgressQueueItem.CancellationTokenSource.Cancel();
        }

        _queueLock.Dispose();
        _sleepingQueueToken.Dispose();
    }

    private class InProgressQueueItem : IAsyncDisposable
    {
        public required QueueItem QueueItem { get; init; }
        public int ProgressPercentage { get; set; }
        public Task ProcessingTask { get; set; } = Task.CompletedTask;
        public required CancellationTokenSource CancellationTokenSource { get; init; }
        public required DavDatabaseContext DbContext { get; init; }
        public Stream? NzbStream { get; init; }
        public required ArticleCachingNntpClient UsenetClient { get; init; }
        public required WorkerJob WorkerJob { get; init; }

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource.Dispose();
            if (NzbStream is not null)
                await NzbStream.DisposeAsync().ConfigureAwait(false);
            await UsenetClient.DisposeAsync().ConfigureAwait(false);
            await DbContext.DisposeAsync().ConfigureAwait(false);
        }
    }
}

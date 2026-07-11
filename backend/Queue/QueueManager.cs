using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;
using System.Runtime;
using Microsoft.Extensions.Options;

namespace NzbWebDAV.Queue;

public class QueueManager : IDisposable
{
    private const int DownloadWorkerRetryMaxAttempts = int.MaxValue;

    private readonly Dictionary<Guid, InProgressQueueItem> _inProgressQueueItems = new();

    private readonly UsenetStreamingClient _usenetClient;
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly QueueWorkLaneCoordinator _queueWorkLaneCoordinator;
    private readonly WebsocketManager _websocketManager;
    private readonly ArrDownloadReportService _arrDownloadReportService;
    private readonly IWorkerJobCoordinator _workerJobCoordinator;
    private readonly WorkerLeaseOptions _workerLeaseOptions;
    private readonly Lock _inProgressQueueItemsLock = new();
    private long _lastMemoryCompactionTicks;

    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        QueueWorkLaneCoordinator queueWorkLaneCoordinator,
        WebsocketManager websocketManager,
        ArrDownloadReportService arrDownloadReportService,
        IWorkerJobCoordinator? workerJobCoordinator = null,
        IOptions<WorkerLeaseOptions>? workerLeaseOptions = null
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _queueWorkLaneCoordinator = queueWorkLaneCoordinator;
        _websocketManager = websocketManager;
        _arrDownloadReportService = arrDownloadReportService;
        _workerLeaseOptions = WorkerLeaseOptions.Validate(
            workerLeaseOptions?.Value ?? new WorkerLeaseOptions());
        _workerJobCoordinator = workerJobCoordinator ?? new DatabaseWorkerJobCoordinator(
            new ConfigWorkerLaneCapacityPolicy(configManager),
            Options.Create(_workerLeaseOptions));
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

    public QueueLaneSnapshot GetLaneSnapshot()
    {
        lock (_inProgressQueueItemsLock)
        {
            var values = _inProgressQueueItems.Values.ToList();
            return new QueueLaneSnapshot(
                TotalActive: values.Count,
                DownloadActive: values.Count(x => x.Stage == QueueProcessingStage.Downloading),
                WaitingForVerify: values.Count(x => x.Stage == QueueProcessingStage.WaitingForVerify),
                Verifying: values.Count(x => x.Stage == QueueProcessingStage.Verifying),
                Moving: values.Count(x => x.Stage == QueueProcessingStage.Moving));
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
        var loop = new QueueProcessingLoop(
            _configManager.IsQueuePaused,
            TryStartNextQueueItemAsync,
            WaitForQueueWorkAsync,
            e =>
            {
                Log.Error(e, "An unexpected error occurred while processing the queue: {Message}", e.Message);
                return Task.CompletedTask;
            });
        await loop.RunAsync(ct).ConfigureAwait(false);
    }

    private async Task WaitForQueueWorkAsync(CancellationToken ct)
    {
        try
        {
            // If every worker slot is busy, paused, or the queue is empty, wait briefly.
            // New NZBs, cancellations, config changes, and completed workers awaken the queue early.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sleepingQueueToken.Token);
            await Task.Delay(TimeSpan.FromMinutes(1), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_sleepingQueueToken.IsCancellationRequested)
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
            var laneSnapshot = GetLaneSnapshot();
            var maxDownloadWorkers = _configManager.GetAdaptiveMaxConcurrentQueueDownloads();
            var maxVerifyWorkers = _configManager.GetAdaptiveMaxConcurrentVerifyJobs();
            if (laneSnapshot.DownloadActive >= maxDownloadWorkers)
                return false;

            if (laneSnapshot.TotalActive >= maxDownloadWorkers + maxVerifyWorkers)
                return false;

            var lease = await _workerJobCoordinator.LeaseAsync(
                    WorkerJob.JobKind.Download,
                    owner: $"{Environment.MachineName}:{Environment.ProcessId}:download",
                    capacity: 1,
                    now: DateTimeOffset.UtcNow,
                    ct)
                .ConfigureAwait(false);
            if (lease.Count == 0)
                return false;

            var workerLease = lease[0];
            var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var topItem = await dbClient.GetQueueItemForWorkerAsync(workerLease.TargetId, ct)
                .ConfigureAwait(false);
            if (topItem.queueItem is null)
            {
                var completed = await _workerJobCoordinator.CompleteAsync(
                    workerLease.Identity, null, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (!completed)
                    Log.Warning("Lost download worker lease {WorkerJobId} before orphan cleanup.",
                        workerLease.Identity.JobId);
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
                workerLease.Identity,
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

    private InProgressQueueItem BeginProcessingQueueItem
    (
        DavDatabaseContext dbContext,
        DavDatabaseClient dbClient,
        ArticleCachingNntpClient usenetClient,
        QueueItem queueItem,
        Stream? queueNzbStream,
        WorkerLeaseIdentity workerLease,
        CancellationTokenSource cts
    )
    {
        var inProgressQueueItem = new InProgressQueueItem()
        {
            QueueItem = queueItem,
            ProgressPercentage = 0,
            CancellationTokenSource = cts,
            DbContext = dbContext,
            NzbStream = queueNzbStream,
            UsenetClient = usenetClient,
            WorkerLease = workerLease
        };
        var debounce = DebounceUtil.CreateDebounce();
        var progressHook = ProgressExtensions.FromAction(progress =>
        {
            inProgressQueueItem.ProgressPercentage = progress;
            var message = $"{queueItem.Id}|{progress}";
            if (progress is 100 or 200) _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message);
            else debounce(() => _websocketManager.SendMessage(WebsocketTopic.QueueItemProgress, message));
        });
        inProgressQueueItem.ProcessingTask = RunQueueItemAsync(inProgressQueueItem, dbClient, progressHook);
        return inProgressQueueItem;
    }

    private async Task RunQueueItemAsync
    (
        InProgressQueueItem inProgressQueueItem,
        DavDatabaseClient dbClient,
        IProgress<int> progressHook
    )
    {
        using var renewalStop = new CancellationTokenSource();
        var renewalTask = RenewLeaseAsync(inProgressQueueItem, renewalStop.Token);
        try
        {
            var outcome = await new QueueItemProcessor(
                inProgressQueueItem.QueueItem,
                inProgressQueueItem.NzbStream,
                dbClient,
                inProgressQueueItem.UsenetClient,
                _configManager,
                _websocketManager,
                _arrDownloadReportService,
                progressHook,
                inProgressQueueItem.CancellationTokenSource.Token,
                stage => inProgressQueueItem.Stage = stage
            ).ProcessAsync().ConfigureAwait(false);
            if (!await UpdateDownloadWorkerJobAsync(_workerJobCoordinator, inProgressQueueItem, outcome)
                    .ConfigureAwait(false))
                Log.Warning("Lost download worker lease {WorkerJobId} before terminal update.",
                    inProgressQueueItem.WorkerLease.JobId);
        }
        finally
        {
            await renewalStop.CancelAsync().ConfigureAwait(false);
            await renewalTask.ConfigureAwait(false);
            lock (_inProgressQueueItemsLock)
            {
                _inProgressQueueItems.Remove(inProgressQueueItem.QueueItem.Id);
            }

            await inProgressQueueItem.DisposeAsync().ConfigureAwait(false);
            TryCompactManagedHeapAfterLargeQueueItem();
            AwakenQueue();
        }
    }

    private void TryCompactManagedHeapAfterLargeQueueItem()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        if (memoryInfo.HighMemoryLoadThresholdBytes <= 0) return;
        var pressure = memoryInfo.MemoryLoadBytes / (double)memoryInfo.HighMemoryLoadThresholdBytes;
        if (pressure < 0.85) return;

        var nowTicks = DateTimeOffset.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastMemoryCompactionTicks);
        if (lastTicks != 0 && nowTicks - lastTicks < TimeSpan.FromMinutes(2).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastMemoryCompactionTicks, nowTicks, lastTicks) != lastTicks) return;

        Log.Information(
            "Compacting managed heap after queue item completion under high memory pressure ({Pressure:P0}).",
            pressure);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private async Task RenewLeaseAsync(InProgressQueueItem item, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_workerLeaseOptions.RenewalInterval, ct).ConfigureAwait(false);
                var renewed = await _workerJobCoordinator.RenewAsync(
                    item.WorkerLease, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (renewed) continue;

                Volatile.Write(ref item.LeaseRenewalRejected, 1);
                await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log.Warning(e, "Download worker lease renewal failed: {Message}", e.Message);
            await item.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> UpdateDownloadWorkerJobAsync
    (
        IWorkerJobCoordinator workerJobCoordinator,
        InProgressQueueItem inProgressQueueItem,
        QueueItemProcessor.ProcessingOutcome outcome
    )
    {
        var now = DateTimeOffset.UtcNow;
        if (outcome == QueueItemProcessor.ProcessingOutcome.Completed)
        {
            return await workerJobCoordinator.CompleteAsync(
                inProgressQueueItem.WorkerLease, null, now, CancellationToken.None).ConfigureAwait(false);
        }

        if (outcome == QueueItemProcessor.ProcessingOutcome.RetryScheduled)
        {
            var nextAttemptAt = inProgressQueueItem.QueueItem.PauseUntil.HasValue
                ? new DateTimeOffset(inProgressQueueItem.QueueItem.PauseUntil.Value)
                : DateTimeOffset.UtcNow.AddMinutes(1);
            return await workerJobCoordinator.FailAsync(
                    inProgressQueueItem.WorkerLease,
                    WorkerJob.FailureClass.Retryable,
                    "Download retry scheduled.",
                    nextAttemptAt,
                    DownloadWorkerRetryMaxAttempts,
                    now,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (outcome == QueueItemProcessor.ProcessingOutcome.Cancelled)
        {
            if (Volatile.Read(ref inProgressQueueItem.LeaseRenewalRejected) != 0)
            {
                var acknowledged = await workerJobCoordinator.FailAsync(
                    inProgressQueueItem.WorkerLease,
                    WorkerJob.FailureClass.Cancelled,
                    "Cancelled by request.",
                    now,
                    DownloadWorkerRetryMaxAttempts,
                    now,
                    CancellationToken.None).ConfigureAwait(false);
                return acknowledged;
            }

            return await workerJobCoordinator.ReleaseAsync(
                inProgressQueueItem.WorkerLease, now, CancellationToken.None).ConfigureAwait(false);
        }

        return false;
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
        public required WorkerLeaseIdentity WorkerLease { get; init; }
        public int LeaseRenewalRejected;
        private int _stage;

        public QueueProcessingStage Stage
        {
            get => (QueueProcessingStage)Volatile.Read(ref _stage);
            set => Volatile.Write(ref _stage, (int)value);
        }

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

public sealed record QueueLaneSnapshot(
    int TotalActive,
    int DownloadActive,
    int WaitingForVerify,
    int Verifying,
    int Moving);

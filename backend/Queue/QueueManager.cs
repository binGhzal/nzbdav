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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace NzbWebDAV.Queue;

public class QueueManager : IHostedService, IDisposable
{
    private const int DownloadWorkerRetryMaxAttempts = int.MaxValue;
    private const string DownloadWorkerSetupError = "Download worker setup failed.";

    private readonly Dictionary<Guid, InProgressQueueItem> _inProgressQueueItems = new();
    private readonly HashSet<InProgressQueueItem> _trackedInProgressQueueItems =
        new(ReferenceEqualityComparer.Instance);

    private readonly UsenetStreamingClient _usenetClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingLoopTask;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly ConfigManager _configManager;
    private readonly QueueWorkLaneCoordinator _queueWorkLaneCoordinator;
    private readonly WebsocketManager _websocketManager;
    private readonly ArrDownloadReportService _arrDownloadReportService;
    private readonly HistoryVisibilityNotifier _historyVisibilityNotifier;
    private readonly IWorkerJobCoordinator _workerJobCoordinator;
    private readonly WorkerLeaseOptions _workerLeaseOptions;
    private readonly Lock _inProgressQueueItemsLock = new();
    private readonly Lock _lifecycleLock = new();
    private CancellationTokenSource _sleepingQueueToken = new();
    private readonly Lock _sleepingQueueLock = new();
    private bool _disposed;

    public QueueManager(
        UsenetStreamingClient usenetClient,
        ConfigManager configManager,
        QueueWorkLaneCoordinator queueWorkLaneCoordinator,
        WebsocketManager websocketManager,
        ArrDownloadReportService arrDownloadReportService,
        IWorkerJobCoordinator? workerJobCoordinator = null,
        IOptions<WorkerLeaseOptions>? workerLeaseOptions = null,
        HistoryVisibilityNotifier? historyVisibilityNotifier = null
    )
    {
        _usenetClient = usenetClient;
        _configManager = configManager;
        _queueWorkLaneCoordinator = queueWorkLaneCoordinator;
        _websocketManager = websocketManager;
        _arrDownloadReportService = arrDownloadReportService;
        _historyVisibilityNotifier = historyVisibilityNotifier
            ?? new HistoryVisibilityNotifier(configManager, websocketManager);
        _workerLeaseOptions = WorkerLeaseOptions.Validate(
            workerLeaseOptions?.Value ?? new WorkerLeaseOptions());
        _workerJobCoordinator = workerJobCoordinator ?? new DatabaseWorkerJobCoordinator(
            new ConfigWorkerLaneCapacityPolicy(configManager),
            Options.Create(_workerLeaseOptions));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_processingLoopTask is { IsCompleted: false }) return Task.CompletedTask;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = CancellationTokenSource
                .CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
            _processingLoopTask = ProcessQueueAsync(_cancellationTokenSource.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifecycleCts;
        Task? processingLoop;
        lock (_lifecycleLock)
        {
            lifecycleCts = _cancellationTokenSource;
            processingLoop = _processingLoopTask;
            lifecycleCts?.Cancel();
        }

        if (processingLoop is not null)
            await processingLoop.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task[] activeWorkers;
        lock (_inProgressQueueItemsLock)
            activeWorkers = _trackedInProgressQueueItems
                .Select(x => x.ProcessingTask)
                .ToArray();
        if (activeWorkers.Length > 0)
            await Task.WhenAll(activeWorkers).WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_lifecycleLock)
        {
            if (!ReferenceEquals(_cancellationTokenSource, lifecycleCts)) return;
            _processingLoopTask = null;
            _cancellationTokenSource = null;
            lifecycleCts?.Dispose();
        }
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

            await dbClient.RemoveQueueItemsAsync(queueItemIds, ct).ConfigureAwait(false);

            foreach (var inProgressQueueItem in inProgressQueueItems)
                CancelUnlessDisposed(inProgressQueueItem.CancellationTokenSource);
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
        WorkerLeaseIdentity? ownedLease = null;
        DavDatabaseContext? dbContext = null;
        Stream? queueNzbStream = null;
        ArticleCachingNntpClient? cachingUsenetClient = null;
        CancellationTokenSource? queueItemCancellationTokenSource = null;
        var failureDisposition = PreHandoffLeaseDisposition.Retry;
        var handedOff = false;
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
            ownedLease = workerLease.Identity;
            InProgressQueueItem? existingLocalWorker;
            lock (_inProgressQueueItemsLock)
                _inProgressQueueItems.TryGetValue(workerLease.TargetId, out existingLocalWorker);
            if (existingLocalWorker is not null)
            {
                CancelUnlessDisposed(existingLocalWorker.CancellationTokenSource);
                await AcknowledgePreHandoffFailureAsync(
                        workerLease.Identity,
                        PreHandoffLeaseDisposition.Release)
                    .ConfigureAwait(false);
                ownedLease = null;
                return false;
            }

            dbContext = DavDatabaseContextRuntimeFactory.Create();
            var dbClient = new DavDatabaseClient(dbContext);
            var topItem = await dbClient.GetQueueItemForWorkerAsync(workerLease.TargetId, ct)
                .ConfigureAwait(false);
            queueNzbStream = topItem.queueNzbStream;
            if (topItem.queueItem is null)
            {
                await AcknowledgePreHandoffFailureAsync(
                        workerLease.Identity,
                        PreHandoffLeaseDisposition.Complete)
                    .ConfigureAwait(false);
                ownedLease = null;
                return false;
            }

            cachingUsenetClient = new ArticleCachingNntpClient(
                _usenetClient,
                maxCacheBytes: _configManager.GetArticleCacheMaxBytesPerQueueWorker(),
                sharedMaxCacheBytes: _configManager.GetArticleCacheMaxBytes());
            queueItemCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            BeginProcessingQueueItem(
                dbContext,
                dbClient,
                cachingUsenetClient,
                topItem.queueItem,
                queueNzbStream,
                workerLease.Identity,
                queueItemCancellationTokenSource);
            handedOff = true;

            return true;
        }
        catch (Exception)
        {
            if (ownedLease.HasValue)
            {
                await AcknowledgePreHandoffFailureAsync(
                        ownedLease.Value,
                        ct.IsCancellationRequested
                            ? PreHandoffLeaseDisposition.Release
                            : failureDisposition)
                    .ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (!handedOff)
            {
                await DisposePreHandoffResourcesAsync(
                        queueItemCancellationTokenSource,
                        queueNzbStream,
                        cachingUsenetClient,
                        dbContext)
                    .ConfigureAwait(false);
            }
            _queueLock.Release();
        }
    }

    private async Task<bool> AcknowledgePreHandoffFailureAsync(
        WorkerLeaseIdentity lease,
        PreHandoffLeaseDisposition disposition)
    {
        var now = DateTimeOffset.UtcNow;
        var nextAttemptAt = now.AddMinutes(1);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var acknowledged = disposition switch
                {
                    PreHandoffLeaseDisposition.Complete => await _workerJobCoordinator.CompleteAsync(
                            lease, null, now, CancellationToken.None)
                        .ConfigureAwait(false),
                    PreHandoffLeaseDisposition.Release => await _workerJobCoordinator.ReleaseAsync(
                            lease, now, CancellationToken.None)
                        .ConfigureAwait(false),
                    PreHandoffLeaseDisposition.Retry => await _workerJobCoordinator.FailAsync(
                            lease,
                            WorkerJob.FailureClass.Retryable,
                            DownloadWorkerSetupError,
                            nextAttemptAt,
                            DownloadWorkerRetryMaxAttempts,
                            now,
                            CancellationToken.None)
                        .ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
                };
                if (!acknowledged)
                {
                    Log.Warning(
                        "Lost download worker lease {WorkerJobId} during pre-handoff reconciliation.",
                        lease.JobId);
                }
                return acknowledged;
            }
            catch (Exception exception) when (attempt == 1)
            {
                Log.Warning(
                    exception,
                    "Download worker pre-handoff acknowledgement failed for {WorkerJobId}; reconciling once.",
                    lease.JobId);
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "Could not reconcile download worker lease {WorkerJobId} after setup failure.",
                    lease.JobId);
                return false;
            }
        }

        return false;
    }

    private static async Task DisposePreHandoffResourcesAsync(
        CancellationTokenSource? cancellationTokenSource,
        Stream? nzbStream,
        ArticleCachingNntpClient? usenetClient,
        DavDatabaseContext? dbContext)
    {
        try
        {
            cancellationTokenSource?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to dispose a pre-handoff queue cancellation token.");
        }

        try
        {
            if (nzbStream is not null)
                await nzbStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to dispose a pre-handoff queue NZB stream.");
        }

        try
        {
            if (usenetClient is not null)
                await usenetClient.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to dispose a pre-handoff queue Usenet client.");
        }

        try
        {
            if (dbContext is not null)
                await dbContext.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to dispose a pre-handoff queue database context.");
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
        lock (_inProgressQueueItemsLock)
        {
            _inProgressQueueItems[queueItem.Id] = inProgressQueueItem;
            _trackedInProgressQueueItems.Add(inProgressQueueItem);
            inProgressQueueItem.ProcessingTask =
                RunQueueItemAsync(inProgressQueueItem, dbClient, progressHook);
        }
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
            QueueItemProcessor.ProcessingOutcome outcome;
            try
            {
                outcome = await new QueueItemProcessor(
                    inProgressQueueItem.QueueItem,
                    inProgressQueueItem.NzbStream,
                    dbClient,
                    inProgressQueueItem.UsenetClient,
                    _configManager,
                    _websocketManager,
                    _arrDownloadReportService,
                    progressHook,
                    inProgressQueueItem.CancellationTokenSource.Token,
                    stage => inProgressQueueItem.Stage = stage,
                    _historyVisibilityNotifier,
                    workerLease: inProgressQueueItem.WorkerLease,
                    completionContextFactory: DavDatabaseContextRuntimeFactory.Create
                ).ProcessAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception.GetBaseException().IsCancellationException())
            {
                outcome = QueueItemProcessor.ProcessingOutcome.Cancelled;
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "Unexpected download worker failure for queue item {QueueItemId}; scheduling retry.",
                    inProgressQueueItem.QueueItem.Id);
                outcome = QueueItemProcessor.ProcessingOutcome.RetryScheduled;
            }

            if (!await ReconcileDownloadWorkerJobAsync(inProgressQueueItem, outcome)
                    .ConfigureAwait(false))
                Log.Warning("Lost download worker lease {WorkerJobId} before terminal update.",
                    inProgressQueueItem.WorkerLease.JobId);
        }
        finally
        {
            try
            {
                await renewalStop.CancelAsync().ConfigureAwait(false);
                await renewalTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "Download worker lease renewal shutdown failed for {WorkerJobId}.",
                    inProgressQueueItem.WorkerLease.JobId);
            }
            await DisposeAndUntrackQueueItemAsync(inProgressQueueItem)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> ReconcileDownloadWorkerJobAsync(
        InProgressQueueItem inProgressQueueItem,
        QueueItemProcessor.ProcessingOutcome outcome)
    {
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await UpdateDownloadWorkerJobCoreAsync(
                        _workerJobCoordinator,
                        inProgressQueueItem,
                        outcome,
                        now)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt == 1)
            {
                Log.Warning(
                    exception,
                    "Download worker terminal update failed for {WorkerJobId}; reconciling once.",
                    inProgressQueueItem.WorkerLease.JobId);
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "Download worker terminal reconciliation failed for {WorkerJobId}.",
                    inProgressQueueItem.WorkerLease.JobId);
                return false;
            }
        }

        return false;
    }

    private async Task DisposeAndUntrackQueueItemAsync(
        InProgressQueueItem inProgressQueueItem)
    {
        try
        {
            await inProgressQueueItem.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Download worker resource cleanup failed for queue item {QueueItemId}.",
                inProgressQueueItem.QueueItem.Id);
        }
        finally
        {
            try
            {
                lock (_inProgressQueueItemsLock)
                {
                    if (_inProgressQueueItems.TryGetValue(
                            inProgressQueueItem.QueueItem.Id,
                            out var trackedQueueItem)
                        && ReferenceEquals(trackedQueueItem, inProgressQueueItem))
                        _inProgressQueueItems.Remove(inProgressQueueItem.QueueItem.Id);
                }
                AwakenQueue();
            }
            finally
            {
                lock (_inProgressQueueItemsLock)
                    _trackedInProgressQueueItems.Remove(inProgressQueueItem);
            }
        }
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
        return await UpdateDownloadWorkerJobCoreAsync(
                workerJobCoordinator,
                inProgressQueueItem,
                outcome,
                now)
            .ConfigureAwait(false);
    }

    private static async Task<bool> UpdateDownloadWorkerJobCoreAsync
    (
        IWorkerJobCoordinator workerJobCoordinator,
        InProgressQueueItem inProgressQueueItem,
        QueueItemProcessor.ProcessingOutcome outcome,
        DateTimeOffset now
    )
    {
        if (outcome == QueueItemProcessor.ProcessingOutcome.Completed)
        {
            return await workerJobCoordinator.CompleteAsync(
                inProgressQueueItem.WorkerLease, null, now, CancellationToken.None).ConfigureAwait(false);
        }

        if (outcome == QueueItemProcessor.ProcessingOutcome.RetryScheduled)
        {
            var nextAttemptAt = inProgressQueueItem.QueueItem.PauseUntil.HasValue
                ? new DateTimeOffset(inProgressQueueItem.QueueItem.PauseUntil.Value)
                : now.AddMinutes(1);
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
            var acknowledged = await workerJobCoordinator.FailAsync(
                inProgressQueueItem.WorkerLease,
                WorkerJob.FailureClass.Cancelled,
                "Cancelled by request.",
                now,
                DownloadWorkerRetryMaxAttempts,
                now,
                CancellationToken.None).ConfigureAwait(false);
            if (acknowledged) return true;

            return await workerJobCoordinator.ReleaseAsync(
                inProgressQueueItem.WorkerLease, now, CancellationToken.None).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
        lock (_inProgressQueueItemsLock)
        {
            foreach (var inProgressQueueItem in _trackedInProgressQueueItems)
                CancelUnlessDisposed(inProgressQueueItem.CancellationTokenSource);
        }

        _queueLock.Dispose();
        _sleepingQueueToken.Dispose();
    }

    private static void CancelUnlessDisposed(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A worker may have completed and disposed its token while a
            // concurrent manager teardown was taking its final snapshot.
        }
    }

    private enum PreHandoffLeaseDisposition
    {
        Complete,
        Release,
        Retry
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
        private int _stage;

        public QueueProcessingStage Stage
        {
            get => (QueueProcessingStage)Volatile.Read(ref _stage);
            set => Volatile.Write(ref _stage, (int)value);
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;
            try
            {
                CancellationTokenSource.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                if (NzbStream is not null)
                    await NzbStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await UsenetClient.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await DbContext.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is not null)
                throw new AggregateException("One or more queue worker resources failed to dispose.", failures);
        }
    }
}

public sealed record QueueLaneSnapshot(
    int TotalActive,
    int DownloadActive,
    int WaitingForVerify,
    int Verifying,
    int Moving);

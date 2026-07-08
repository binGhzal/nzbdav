using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService
{
    private static readonly TimeSpan AutoRepairRetryDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan ProviderErrorRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WorkerLeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MissingSegmentCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan RecentlyVerifiedSegmentCacheTtl = TimeSpan.FromHours(6);
    private const int WorkerMaxAttempts = 3;
    private const int MaxMissingSegmentCacheEntries = 50_000;
    private const int MaxRecentlyVerifiedSegmentCacheEntries = 100_000;
    private const int VerificationJobEnqueueBatchSize = 64;
    private const int MaxBlobSegmentReadConcurrency = 8;
    private const int MaxDeduplicatedVerificationSegments = 20_000;
    private const int MaxProviderVerificationBatchSegments = 2_000;

    private readonly ConfigManager _configManager;
    private readonly INntpClient _usenetClient;
    private readonly QueueWorkLaneCoordinator _queueWorkLaneCoordinator;
    private readonly WebsocketManager _websocketManager;
    private readonly object _activeHealthChecksLock = new();
    private readonly HashSet<Guid> _activeHealthChecks = [];
    private readonly object _inFlightSegmentChecksLock = new();
    private readonly Dictionary<string, TaskCompletionSource<SegmentCheckResult>> _inFlightSegmentChecks =
        new(StringComparer.Ordinal);
    private int _activeVerificationJobs;
    private int _activeRepairJobs;

    private static readonly Dictionary<string, DateTimeOffset> _missingSegmentIds = [];
    private static readonly Dictionary<string, DateTimeOffset> _recentlyVerifiedSegmentIds = [];

    private enum SegmentMetadataReadState
    {
        DatabaseFallback,
        Found,
        TemporarilyUnavailable
    }

    private sealed record SegmentMetadataRead(
        DavItem Item,
        List<string> Segments,
        SegmentMetadataReadState State,
        string? Error);

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        QueueWorkLaneCoordinator queueWorkLaneCoordinator,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _queueWorkLaneCoordinator = queueWorkLaneCoordinator;
        _websocketManager = websocketManager;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
            lock (_missingSegmentIds) _missingSegmentIds.Clear();
            lock (_recentlyVerifiedSegmentIds) _recentlyVerifiedSegmentIds.Clear();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var startedWorker = false;
                var workerPolicy = GetWorkerSchedulingPolicy(_configManager.IsRepairJobEnabled());
                var segmentConcurrency = _configManager.GetAdaptiveHealthCheckConcurrency();
                var postDownloadSegmentConcurrency = _configManager.GetAdaptivePostDownloadVerificationConcurrency();
                var maxVerifyJobs = _configManager.GetAdaptiveMaxConcurrentVerifyJobs();
                var maxRepairJobs = _configManager.GetAdaptiveMaxConcurrentRepairJobs();

                var activeVerificationJobs = Volatile.Read(ref _activeVerificationJobs);
                while (workerPolicy.ProcessExplicitVerifyJobs && activeVerificationJobs < maxVerifyJobs)
                {
                    IDisposable? verifyLease = null;
                    try
                    {
                        verifyLease = _queueWorkLaneCoordinator.TryEnterVerify(maxVerifyJobs);
                        if (verifyLease is null) break;

                        var verifyJob = await LeaseNextVerificationJobAsync(
                                GetActiveHealthCheckIds(),
                                workerPolicy.AutoEnqueueDueVerifyItems,
                                stoppingToken)
                            .ConfigureAwait(false);
                        if (verifyJob == null) break;
                        if (!TryMarkHealthCheckActive(verifyJob.TargetId))
                        {
                            await ReleaseLeasedWorkerJobAsync(verifyJob, stoppingToken).ConfigureAwait(false);
                            continue;
                        }

                        startedWorker = true;
                        Interlocked.Increment(ref _activeVerificationJobs);
                        activeVerificationJobs++;
                        var workerLease = verifyLease;
                        var isPostDownloadVerification = IsPostDownloadVerificationJob(verifyJob);
                        var workerSegmentConcurrency = GetVerificationSegmentConcurrency(
                            segmentConcurrency,
                            postDownloadSegmentConcurrency,
                            maxVerifyJobs,
                            activeVerificationJobs - 1,
                            isPostDownloadVerification ? Math.Max(1, verifyJob.Priority) : verifyJob.Priority);
                        verifyLease = null;
                        _ = RunVerificationWorkerAsync(verifyJob, workerSegmentConcurrency, workerLease, stoppingToken);
                    }
                    finally
                    {
                        verifyLease?.Dispose();
                    }
                }

                var activeRepairJobs = Volatile.Read(ref _activeRepairJobs);
                while (workerPolicy.ProcessRepairJobs && activeRepairJobs < maxRepairJobs)
                {
                    var repairJob = await LeaseNextRepairJobAsync(GetActiveHealthCheckIds(), stoppingToken)
                        .ConfigureAwait(false);
                    if (repairJob == null) break;
                    if (!await TryStartRepairWorkerAsync(repairJob, stoppingToken).ConfigureAwait(false))
                        continue;

                    startedWorker = true;
                    activeRepairJobs++;
                }

                var delay = startedWorker
                    ? TimeSpan.FromMilliseconds(250)
                    : GetActiveHealthCheckCount() > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private int GetActiveHealthCheckCount()
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.Count;
        }
    }

    private Guid[] GetActiveHealthCheckIds()
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.ToArray();
        }
    }

    private bool TryMarkHealthCheckActive(Guid davItemId)
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.Add(davItemId);
        }
    }

    private void ClearHealthCheckActive(Guid davItemId)
    {
        lock (_activeHealthChecksLock)
        {
            _activeHealthChecks.Remove(davItemId);
        }
    }

    public WorkerSnapshot GetWorkerSnapshot()
    {
        return new WorkerSnapshot(
            VerifyActive: Volatile.Read(ref _activeVerificationJobs),
            RepairActive: Volatile.Read(ref _activeRepairJobs));
    }

    public sealed record WorkerSchedulingPolicy
    (
        bool ProcessExplicitVerifyJobs,
        bool AutoEnqueueDueVerifyItems,
        bool ProcessRepairJobs
    );

    public static WorkerSchedulingPolicy GetWorkerSchedulingPolicy(bool repairJobEnabled)
    {
        return new WorkerSchedulingPolicy(
            ProcessExplicitVerifyJobs: true,
            AutoEnqueueDueVerifyItems: repairJobEnabled,
            ProcessRepairJobs: repairJobEnabled);
    }

    public static int GetVerificationSegmentConcurrency
    (
        int segmentConcurrency,
        int maxVerifyJobs,
        int activeVerifyJobs
    )
    {
        return GetVerificationSegmentConcurrency(
            segmentConcurrency,
            postDownloadSegmentConcurrency: segmentConcurrency,
            maxVerifyJobs,
            activeVerifyJobs,
            workerJobPriority: 0);
    }

    public static int GetVerificationSegmentConcurrency
    (
        int segmentConcurrency,
        int postDownloadSegmentConcurrency,
        int maxVerifyJobs,
        int activeVerifyJobs,
        int workerJobPriority
    )
    {
        var verifyWorkerBudget = Math.Max(1, maxVerifyJobs);
        var plannedWorkers = Math.Clamp(activeVerifyJobs + 1, 1, verifyWorkerBudget);
        if (workerJobPriority > 0)
            return Math.Max(1, Math.Max(segmentConcurrency, postDownloadSegmentConcurrency) / verifyWorkerBudget);

        return Math.Max(1, Math.Max(1, segmentConcurrency) / plannedWorkers);
    }

    private async Task<WorkerJob?> LeaseNextVerificationJobAsync(
        IReadOnlyCollection<Guid> activeHealthCheckIds,
        bool autoEnqueueDueItems,
        CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var currentDateTime = DateTimeOffset.UtcNow;

        var existingJob = await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                owner: $"{Environment.MachineName}:{Environment.ProcessId}:verify",
                leaseDuration: WorkerLeaseDuration,
                now: currentDateTime,
                ct: ct,
                excludeTargetIds: activeHealthCheckIds)
            .ConfigureAwait(false);
        if (existingJob != null) return existingJob;

        if (!autoEnqueueDueItems) return null;

        IQueryable<DavItem> query = GetHealthCheckQueueItems(dbClient)
            .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime);

        if (activeHealthCheckIds.Count > 0)
            query = query.Where(x => !activeHealthCheckIds.Contains(x.Id));

        var dueItemIds = await query
            .Select(x => x.Id)
            .Take(VerificationJobEnqueueBatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await dbClient.EnqueueWorkerJobsAsync(
                WorkerJob.JobKind.Verify,
                dueItemIds,
                priority: 0,
                now: currentDateTime,
                ct: ct)
            .ConfigureAwait(false);

        return await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                owner: $"{Environment.MachineName}:{Environment.ProcessId}:verify",
                leaseDuration: WorkerLeaseDuration,
                now: currentDateTime,
                ct: ct,
                excludeTargetIds: activeHealthCheckIds)
            .ConfigureAwait(false);
    }

    private static async Task<WorkerJob?> LeaseNextRepairJobAsync(
        IReadOnlyCollection<Guid> activeHealthCheckIds,
        CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        return await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                owner: $"{Environment.MachineName}:{Environment.ProcessId}:repair",
                leaseDuration: WorkerLeaseDuration,
                ct: ct,
                excludeTargetIds: activeHealthCheckIds)
            .ConfigureAwait(false);
    }

    private static async Task ReleaseLeasedWorkerJobAsync(WorkerJob workerJob, CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        dbContext.WorkerJobs.Attach(workerJob);
        var dbClient = new DavDatabaseClient(dbContext);
        await dbClient.ReleaseWorkerJobLeaseAsync(workerJob, ct: ct).ConfigureAwait(false);
    }

    private async Task RunVerificationWorkerAsync(
        WorkerJob workerJob,
        int segmentConcurrency,
        IDisposable verifyLease,
        CancellationToken ct)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var davItem = await dbContext.Items
                .FirstOrDefaultAsync(x => x.Id == workerJob.TargetId, ct)
                .ConfigureAwait(false);
            dbContext.WorkerJobs.Attach(workerJob);
            var repairRunId = DavDatabaseClient.TryGetRepairRunId(workerJob.PayloadJson);
            if (davItem == null)
            {
                if (repairRunId.HasValue)
                {
                    var path = await dbContext.RepairEntryHealth
                        .Where(x => x.RepairRunId == repairRunId.Value && x.DavItemId == workerJob.TargetId)
                        .Select(x => x.Path)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false) ?? "";
                    await dbClient.UpsertRepairEntryAsync(
                            repairRunId.Value,
                            workerJob.TargetId,
                            path,
                            RepairEntryHealth.RepairEntryState.Deleted,
                            "File no longer exists.",
                            ct: ct)
                        .ConfigureAwait(false);
                    await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                await dbClient.CompleteWorkerJobAsync(workerJob, ct: ct).ConfigureAwait(false);
                return;
            }

            if (repairRunId.HasValue)
            {
                await dbClient.UpsertRepairEntryAsync(
                        repairRunId.Value,
                        davItem.Id,
                        davItem.Path,
                        RepairEntryHealth.RepairEntryState.Checking,
                        "Checking file articles.",
                        ct: ct)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            var isPostDownloadVerification = IsPostDownloadVerificationJob(workerJob);
            await PerformHealthCheckAsync(
                    davItem,
                    dbClient,
                    segmentConcurrency,
                    ct,
                    repairRunId,
                    skipReleaseDateProbe: isPostDownloadVerification,
                    useRecentlyVerifiedSegmentCache: isPostDownloadVerification)
                .ConfigureAwait(false);
            await dbClient.CompleteWorkerJobAsync(workerJob, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
        {
            await ReleaseWorkerJobAfterCancellationAsync(workerJob, WorkerJob.JobKind.Verify)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "Unexpected error performing background health check for {DavItemId}: {Message}", workerJob.TargetId, e.Message);
            var failedStatus = await FailLeasedWorkerJobAsync(workerJob, e, WorkerJob.JobKind.Verify, ct)
                .ConfigureAwait(false);
            var repairRunId = DavDatabaseClient.TryGetRepairRunId(workerJob.PayloadJson);
            if (repairRunId.HasValue)
                await MarkRepairVerificationFailureAsync(
                        repairRunId.Value,
                        workerJob.TargetId,
                        e,
                        failedStatus is WorkerJob.JobStatus.Quarantined,
                        ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeVerificationJobs);
            verifyLease.Dispose();
            ClearHealthCheckActive(workerJob.TargetId);
        }
    }

    private static bool IsPostDownloadVerificationJob(WorkerJob workerJob)
    {
        return workerJob.Priority > 0 || DavDatabaseClient.IsPostDownloadVerifyPayload(workerJob.PayloadJson);
    }

    private async Task<bool> TryStartRepairWorkerAsync(WorkerJob workerJob, CancellationToken ct)
    {
        if (!TryMarkHealthCheckActive(workerJob.TargetId))
        {
            await ReleaseLeasedWorkerJobAsync(workerJob, ct).ConfigureAwait(false);
            return false;
        }

        Interlocked.Increment(ref _activeRepairJobs);
        _ = RunRepairWorkerAsync(workerJob, ct);
        return true;
    }

    private async Task RunRepairWorkerAsync(WorkerJob workerJob, CancellationToken ct)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var davItem = await dbContext.Items
                .FirstOrDefaultAsync(x => x.Id == workerJob.TargetId, ct)
                .ConfigureAwait(false);
            dbContext.WorkerJobs.Attach(workerJob);
            var repairRunId = DavDatabaseClient.TryGetRepairRunId(workerJob.PayloadJson);
            if (davItem == null)
            {
                await dbClient.CompleteWorkerJobAsync(workerJob, ct: ct).ConfigureAwait(false);
                return;
            }

            await Repair(davItem, dbClient, ct, repairRunId).ConfigureAwait(false);
            await dbClient.CompleteWorkerJobAsync(workerJob, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
        {
            await ReleaseWorkerJobAfterCancellationAsync(workerJob, WorkerJob.JobKind.Repair)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "Unexpected error performing background repair for {DavItemId}: {Message}", workerJob.TargetId, e.Message);
            await FailLeasedWorkerJobAsync(workerJob, e, WorkerJob.JobKind.Repair, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRepairJobs);
            ClearHealthCheckActive(workerJob.TargetId);
        }
    }

    private static async Task<WorkerJob.JobStatus?> FailLeasedWorkerJobAsync
    (
        WorkerJob workerJob,
        Exception exception,
        WorkerJob.JobKind kind,
        CancellationToken ct
    )
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            dbContext.WorkerJobs.Attach(workerJob);
            var dbClient = new DavDatabaseClient(dbContext);
            await dbClient.FailWorkerJobAsync(
                    workerJob,
                    error: exception.Message,
                    nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(kind == WorkerJob.JobKind.Repair ? 15 : 5),
                    maxAttempts: WorkerMaxAttempts,
                    ct: ct)
                .ConfigureAwait(false);
            return workerJob.Status;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to update {WorkerKind} worker job after error: {Message}", kind, e.Message);
            return null;
        }
    }

    private static async Task ReleaseWorkerJobAfterCancellationAsync(WorkerJob workerJob, WorkerJob.JobKind kind)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            dbContext.WorkerJobs.Attach(workerJob);
            var dbClient = new DavDatabaseClient(dbContext);
            await dbClient.ReleaseWorkerJobLeaseAsync(workerJob, ct: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(
                e,
                "Failed to release cancelled {WorkerKind} worker job {WorkerJobId}: {Message}",
                kind,
                workerJob.Id,
                e.Message);
        }
    }

    private static async Task MarkRepairVerificationFailureAsync
    (
        Guid repairRunId,
        Guid davItemId,
        Exception exception,
        bool quarantined,
        CancellationToken ct
    )
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            await dbClient.MarkRepairVerificationFailureAsync(
                    repairRunId,
                    davItemId,
                    exception.Message,
                    quarantined,
                    ct: ct)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to update repair run {RepairRunId} after verify failure: {Message}", repairRunId, e.Message);
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck == null ? 1 : 0)
            .ThenBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.HistoryItemId == null);
    }

    public async Task PerformHealthCheckAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct,
        Guid? repairRunId = null,
        bool skipReleaseDateProbe = false,
        bool useRecentlyVerifiedSegmentCache = false
    )
    {
        try
        {
            if (davItem.Type == DavItem.ItemType.Directory)
            {
                await PerformDirectoryHealthCheckAsync(
                        davItem,
                        dbClient,
                        concurrency,
                        ct,
                        repairRunId,
                        useRecentlyVerifiedSegmentCache)
                    .ConfigureAwait(false);
                return;
            }

            // update the release date, if null
            var metadata = await GetAllSegmentMetadataAsync(davItem, dbClient, ct).ConfigureAwait(false);
            if (metadata.State == SegmentMetadataReadState.TemporarilyUnavailable)
            {
                await RecordTemporaryMetadataUnavailableAsync(davItem, dbClient, ct, repairRunId, metadata.Error)
                    .ConfigureAwait(false);
                return;
            }

            var segments = metadata.Segments;
            if (segments.Count == 0)
            {
                await RecordMissingFileMetadataAsync(davItem, dbClient, ct, repairRunId).ConfigureAwait(false);
                return;
            }

            if (davItem.ReleaseDate == null && !skipReleaseDateProbe)
            {
                try
                {
                    await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);
                }
                catch (UsenetArticleNotFoundException e)
                {
                    Log.Debug(e, "Could not update release date before health check for {Path}: {Message}", davItem.Path, e.Message);
                }
                catch (Exception e) when (!e.IsCancellationException())
                {
                    Log.Debug(e, "Could not update release date before health check for {Path}: {Message}", davItem.Path, e.Message);
                }
            }

            // setup progress tracking
            var debounce = DebounceUtil.CreateDebounce();
            var progressHook = ProgressExtensions.FromAction(progress =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            });

            // perform health check
            var progress = progressHook.ToPercentage(Math.Max(1, segments.Count));
            var checkBatch = await RunVerificationAsync(
                    segments,
                    concurrency,
                    progress,
                    ct,
                    useRecentlyVerifiedSegmentCache)
                .ConfigureAwait(false);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            if (checkBatch.Missing > 0 && checkBatch.ProviderErrors == 0 && checkBatch.Unknown == 0)
            {
                await RecordMissingSegmentsAsync(davItem, dbClient, checkBatch, ct, repairRunId).ConfigureAwait(false);
                return;
            }

            if (checkBatch.ProviderErrors > 0 || checkBatch.Unknown > 0)
            {
                await RecordUnknownVerificationAsync(davItem, dbClient, checkBatch, ct, repairRunId).ConfigureAwait(false);
                return;
            }

            // update the database
            await RecordHealthyVerificationAsync(
                    dbClient,
                    davItem,
                    segments,
                    repairRunId,
                    useRecentlyVerifiedSegmentCache,
                    ct)
                .ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                AddCachedMissingSegmentIds(NzbSegmentIdSet.Decode(e.SegmentId));

            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + AutoRepairRetryDelay;
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = "File had missing articles. Queued automatic repair."
            }));
            await dbClient.EnqueueWorkerJobAsync(
                    WorkerJob.JobKind.Repair,
                    davItem.Id,
                    priority: 0,
                    now: utcNow,
                    payloadJson: repairRunId.HasValue
                        ? DavDatabaseClient.CreateRepairRunPayloadJson(repairRunId.Value)
                        : null,
                    ct: ct)
                .ConfigureAwait(false);
            await MarkRepairEntryAsync(
                    dbClient,
                    repairRunId,
                    davItem,
                    RepairEntryHealth.RepairEntryState.Missing,
                    "File had missing articles. Queued automatic repair.",
                    ct)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PerformDirectoryHealthCheckAsync
    (
        DavItem directory,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct,
        Guid? repairRunId,
        bool useRecentlyVerifiedSegmentCache
    )
    {
        var fileItems = await GetDirectoryHealthCheckFileItems(directory, dbClient, ct).ConfigureAwait(false);
        if (fileItems.Count == 0) return;

        var itemSegments = await GetAllSegmentMetadataAsync(fileItems, dbClient, ct).ConfigureAwait(false);
        foreach (var metadata in itemSegments)
        {
            if (metadata.State == SegmentMetadataReadState.TemporarilyUnavailable)
            {
                await RecordTemporaryMetadataUnavailableAsync(
                        metadata.Item,
                        dbClient,
                        ct,
                        repairRunId,
                        metadata.Error)
                    .ConfigureAwait(false);
                continue;
            }

            if (metadata.Segments.Count == 0)
            {
                await RecordMissingFileMetadataAsync(metadata.Item, dbClient, ct, repairRunId).ConfigureAwait(false);
                continue;
            }
        }

        var allSegments = itemSegments
            .SelectMany(x => x.Segments)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (allSegments.Count == 0) return;

        var debounce = DebounceUtil.CreateDebounce();
        var progressHook = ProgressExtensions.FromAction(progress =>
        {
            var message = $"{directory.Id}|{progress}";
            debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
        });
        var progress = progressHook.ToPercentage(Math.Max(1, allSegments.Count));
        var checkBatch = await RunVerificationAsync(
                allSegments,
                concurrency,
                progress,
                ct,
                useRecentlyVerifiedSegmentCache)
            .ConfigureAwait(false);
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{directory.Id}|100");
        _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{directory.Id}|done");

        if (checkBatch.IsClean)
        {
            foreach (var metadata in itemSegments)
            {
                var item = metadata.Item;
                var segments = metadata.Segments;
                if (segments.Count == 0) continue;

                await RecordHealthyVerificationAsync(
                        dbClient,
                        item,
                        segments,
                        repairRunId,
                        useRecentlyVerifiedSegmentCache,
                        ct,
                        saveChanges: false)
                    .ConfigureAwait(false);
            }

            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var resultsBySegment = checkBatch.Results
            .GroupBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        foreach (var metadata in itemSegments)
        {
            var item = metadata.Item;
            var segments = metadata.Segments;
            if (segments.Count == 0) continue;

            var itemBatch = SegmentCheckBatch.FromResults(segments
                .Select(segment => GetSegmentCheckResultOrUnknown(
                    resultsBySegment,
                    segment,
                    "Segment check result was absent from directory verification batch."))
                .ToArray());

            if (itemBatch.Missing > 0 && itemBatch.ProviderErrors == 0 && itemBatch.Unknown == 0)
            {
                await RecordMissingSegmentsAsync(item, dbClient, itemBatch, ct, repairRunId).ConfigureAwait(false);
                continue;
            }

            if (itemBatch.ProviderErrors > 0 || itemBatch.Unknown > 0)
            {
                await RecordUnknownVerificationAsync(item, dbClient, itemBatch, ct, repairRunId).ConfigureAwait(false);
                continue;
            }

            await RecordHealthyVerificationAsync(
                    dbClient,
                    item,
                    segments,
                    repairRunId,
                    useRecentlyVerifiedSegmentCache,
                    ct,
                    saveChanges: false)
                .ConfigureAwait(false);
        }

        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<DavItem>> GetDirectoryHealthCheckFileItems
    (
        DavItem directory,
        DavDatabaseClient dbClient,
        CancellationToken ct
    )
    {
        if (directory.HistoryItemId.HasValue)
        {
            var historyItemId = directory.HistoryItemId.Value;
            var historyFileItems = await dbClient.Ctx.Items
                .Where(x => x.Type == DavItem.ItemType.UsenetFile)
                .Where(x => x.HistoryItemId == historyItemId)
                .WhereVideoFiles()
                .OrderBy(x => x.Path)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return historyFileItems;
        }

        var normalizedDirectoryPath = ContentPathUtil.NormalizeSeparators(directory.Path).TrimEnd('/');
        var directoryPrefix = normalizedDirectoryPath + "/";
        var query = dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.Path == normalizedDirectoryPath || x.Path.StartsWith(directoryPrefix))
            .WhereVideoFiles();

        return await query
            .OrderBy(x => x.Path)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<SegmentCheckBatch> RunVerificationAsync
    (
        List<string> segments,
        int concurrency,
        IProgress<int> progress,
        CancellationToken ct,
        bool useRecentlyVerifiedSegmentCache = false
    )
    {
        using var priorityScope = ct.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.Normal
        });
        if (!useRecentlyVerifiedSegmentCache)
            return await RunDirectVerificationAsync(segments, concurrency, progress, ct).ConfigureAwait(false);

        if (segments.Count > MaxDeduplicatedVerificationSegments)
            return await RunDirectVerificationAsync(segments, concurrency, progress, ct).ConfigureAwait(false);

        var cachedResults = GetRecentlyVerifiedSegmentResults(segments);
        var completed = CountCachedLogicalSegments(segments, cachedResults);
        if (completed > 0)
            progress.Report(completed);

        var segmentsToCheck = segments
            .Where(segment => !cachedResults.ContainsKey(segment))
            .ToList();
        if (segmentsToCheck.Count == 0)
            return SegmentCheckBatch.AllExists(segments);

        var progressOffset = completed;
        var uncachedProgress = new OffsetProgress(progress, progressOffset);
        var checkedBatch = await RunDeduplicatedVerificationAsync(segmentsToCheck, concurrency, uncachedProgress, ct)
            .ConfigureAwait(false);
        AddRecentlyVerifiedSegmentResults(checkedBatch.Results);
        if (checkedBatch.IsClean) return SegmentCheckBatch.AllExists(segments);
        if (cachedResults.Count == 0) return checkedBatch;

        var checkedResults = checkedBatch.Results.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        return SegmentCheckBatch.FromResults(segments
            .Select(segment =>
                checkedResults.TryGetValue(segment, out var result)
                    ? result
                    : GetSegmentCheckResultOrUnknown(
                        cachedResults,
                        segment,
                        "Segment check result was absent from checked and cached verification results."))
            .ToArray());
    }

    private async Task<SegmentCheckBatch> RunDirectVerificationAsync
    (
        List<string> segments,
        int concurrency,
        IProgress<int> progress,
        CancellationToken ct
    )
    {
        if (segments.Count <= MaxProviderVerificationBatchSegments)
        {
            var batch = await _usenetClient.CheckSegmentsAsync(segments, concurrency, progress, ct)
                .ConfigureAwait(false);
            return batch.IsClean ? SegmentCheckBatch.AllExists(segments) : batch;
        }

        var nonCleanResults = new List<SegmentCheckResult>();
        var checkedCount = 0;
        var missing = 0;
        var providerErrors = 0;
        var unknown = 0;

        for (var offset = 0; offset < segments.Count; offset += MaxProviderVerificationBatchSegments)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(MaxProviderVerificationBatchSegments, segments.Count - offset);
            var chunk = segments.GetRange(offset, count);
            var batch = await _usenetClient
                .CheckSegmentsAsync(chunk, concurrency, new OffsetProgress(progress, checkedCount), ct)
                .ConfigureAwait(false);

            checkedCount += batch.Checked;
            missing += batch.Missing;
            providerErrors += batch.ProviderErrors;
            unknown += batch.Unknown;

            if (batch.IsClean) continue;

            nonCleanResults.AddRange(batch.Results.Where(x => x.State != SegmentCheckState.Exists));
        }

        if (nonCleanResults.Count == 0 && missing == 0 && providerErrors == 0 && unknown == 0)
            return SegmentCheckBatch.AllExists(segments);

        return new SegmentCheckBatch(
            nonCleanResults,
            Checked: segments.Count,
            Missing: missing,
            ProviderErrors: providerErrors,
            Unknown: unknown);
    }

    private static int CountCachedLogicalSegments(
        IEnumerable<string> segments,
        IReadOnlyDictionary<string, SegmentCheckResult> cachedResults)
    {
        var completed = 0;
        foreach (var segment in segments)
        {
            if (cachedResults.ContainsKey(segment))
                completed++;
        }

        return completed;
    }

    private async Task<SegmentCheckBatch> RunDeduplicatedVerificationAsync
    (
        IReadOnlyList<string> segments,
        int concurrency,
        IProgress<int> progress,
        CancellationToken ct
    )
    {
        var progressReporter = new SegmentCheckProgressReporter(progress);
        var ownedSegments = new List<string>();
        var ownedCompletions = new Dictionary<string, TaskCompletionSource<SegmentCheckResult>>(StringComparer.Ordinal);
        var awaitedCompletions = new Dictionary<string, Task<SegmentCheckResult>>(StringComparer.Ordinal);
        var segmentCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        lock (_inFlightSegmentChecksLock)
        {
            foreach (var segment in segments)
            {
                if (segmentCounts.TryGetValue(segment, out var count))
                {
                    segmentCounts[segment] = count + 1;
                    continue;
                }

                segmentCounts[segment] = 1;
                if (_inFlightSegmentChecks.TryGetValue(segment, out var existingCompletion))
                {
                    awaitedCompletions[segment] = existingCompletion.Task;
                    continue;
                }

                var completion = new TaskCompletionSource<SegmentCheckResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightSegmentChecks[segment] = completion;
                ownedCompletions[segment] = completion;
                ownedSegments.Add(segment);
            }
        }

        var ownedTask = ownedSegments.Count == 0
            ? Task.FromResult(new Dictionary<string, SegmentCheckResult>(StringComparer.Ordinal))
            : CheckOwnedSegmentsAsync(
                ownedSegments,
                ownedCompletions,
                concurrency,
                progressReporter.CreateWeightedAbsoluteProgress(ownedSegments, segmentCounts),
                ct);
        var awaitedTask = AwaitInFlightSegmentsAsync(awaitedCompletions, segmentCounts, progressReporter.ReportCount, ct);

        await Task.WhenAll(ownedTask, awaitedTask).ConfigureAwait(false);

        var resultsBySegment = new Dictionary<string, SegmentCheckResult>(ownedTask.Result, StringComparer.Ordinal);
        foreach (var item in awaitedTask.Result)
            resultsBySegment[item.Key] = item.Value;

        return SegmentCheckBatch.FromResults(segments
            .Select(segment => GetSegmentCheckResultOrUnknown(
                resultsBySegment,
                segment,
                "Segment check result was absent from deduplicated verification results."))
            .ToArray());
    }

    private static SegmentCheckResult GetSegmentCheckResultOrUnknown(
        IReadOnlyDictionary<string, SegmentCheckResult> resultsBySegment,
        string segmentId,
        string reason)
    {
        if (resultsBySegment.TryGetValue(segmentId, out var result))
            return result;

        return new SegmentCheckResult(
            segmentId,
            SegmentCheckState.Unknown,
            Provider: null,
            Error: reason);
    }

    private async Task<Dictionary<string, SegmentCheckResult>> CheckOwnedSegmentsAsync
    (
        IReadOnlyList<string> ownedSegments,
        IReadOnlyDictionary<string, TaskCompletionSource<SegmentCheckResult>> ownedCompletions,
        int concurrency,
        IProgress<int> progress,
        CancellationToken ct
    )
    {
        try
        {
            var batch = await _usenetClient.CheckSegmentsAsync(ownedSegments, concurrency, progress, ct)
                .ConfigureAwait(false);
            var results = new Dictionary<string, SegmentCheckResult>(StringComparer.Ordinal);
            for (var i = 0; i < ownedSegments.Count; i++)
            {
                var segment = ownedSegments[i];
                var result = i < batch.Results.Count
                    ? batch.Results[i]
                    : new SegmentCheckResult(
                        segment,
                        SegmentCheckState.Unknown,
                        Provider: null,
                        Error: "Provider returned fewer segment check results than requested.");
                results[segment] = result;
                ownedCompletions[segment].TrySetResult(result);
            }

            return results;
        }
        catch (OperationCanceledException e)
        {
            foreach (var completion in ownedCompletions.Values)
                completion.TrySetCanceled(e.CancellationToken);
            throw;
        }
        catch (Exception e)
        {
            foreach (var completion in ownedCompletions.Values)
                completion.TrySetException(e);
            throw;
        }
        finally
        {
            lock (_inFlightSegmentChecksLock)
            {
                foreach (var item in ownedCompletions)
                {
                    if (_inFlightSegmentChecks.TryGetValue(item.Key, out var completion)
                        && ReferenceEquals(completion, item.Value))
                        _inFlightSegmentChecks.Remove(item.Key);
                }
            }
        }
    }

    private static async Task<Dictionary<string, SegmentCheckResult>> AwaitInFlightSegmentsAsync
    (
        IReadOnlyDictionary<string, Task<SegmentCheckResult>> awaitedCompletions,
        IReadOnlyDictionary<string, int> segmentCounts,
        Action<int> reportSegmentsCompleted,
        CancellationToken ct
    )
    {
        var results = new Dictionary<string, SegmentCheckResult>(StringComparer.Ordinal);
        foreach (var item in awaitedCompletions)
        {
            var result = await item.Value.WaitAsync(ct).ConfigureAwait(false);
            reportSegmentsCompleted(segmentCounts.TryGetValue(item.Key, out var count) ? count : 1);
            results[item.Key] = result;
        }

        return results;
    }

    private sealed class SegmentCheckProgressReporter(IProgress<int> progress)
    {
        private readonly object _lock = new();
        private int _completed;

        public IProgress<int> CreateWeightedAbsoluteProgress(
            IReadOnlyList<string> orderedSegments,
            IReadOnlyDictionary<string, int> segmentCounts)
        {
            return new WeightedAbsoluteProgress(this, orderedSegments, segmentCounts);
        }

        public void ReportCount(int count)
        {
            ReportDelta(count);
        }

        private void ReportDelta(int delta)
        {
            if (delta <= 0) return;
            int completed;
            lock (_lock)
            {
                _completed += delta;
                completed = _completed;
            }

            progress.Report(completed);
        }

        private sealed class WeightedAbsoluteProgress(
            SegmentCheckProgressReporter reporter,
            IReadOnlyList<string> orderedSegments,
            IReadOnlyDictionary<string, int> segmentCounts) : IProgress<int>
        {
            private int _previous;

            public void Report(int value)
            {
                var current = Math.Clamp(value, _previous, orderedSegments.Count);
                var delta = 0;
                for (var i = _previous; i < current; i++)
                    delta += segmentCounts.TryGetValue(orderedSegments[i], out var count) ? count : 1;
                _previous = current;
                reporter.ReportDelta(delta);
            }
        }
    }

    private sealed class OffsetProgress(IProgress<int> inner, int offset) : IProgress<int>
    {
        public void Report(int value)
        {
            inner.Report(offset + value);
        }
    }

    private async Task RecordMissingFileMetadataAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct,
        Guid? repairRunId
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        const string message = "File metadata is missing or contains no article segments. Queued automatic repair.";
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + AutoRepairRetryDelay;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
            Message = message
        }));
        await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                davItem.Id,
                priority: 0,
                now: utcNow,
                payloadJson: repairRunId.HasValue
                    ? DavDatabaseClient.CreateRepairRunPayloadJson(repairRunId.Value)
                    : null,
                ct: ct)
            .ConfigureAwait(false);
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                RepairEntryHealth.RepairEntryState.Missing,
                message,
                ct)
            .ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordTemporaryMetadataUnavailableAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct,
        Guid? repairRunId,
        string? error
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        var message = string.Join(" ", [
            "File metadata could not be read because the blob store is temporarily unavailable.",
            "Will retry before automatic repair.",
            string.IsNullOrWhiteSpace(error) ? "" : $"Error: {error}"
        ]).Trim();
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + ProviderErrorRetryDelay;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.None,
            Message = message
        }));
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                RepairEntryHealth.RepairEntryState.ProviderError,
                message,
                ct)
            .ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordMissingSegmentsAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        SegmentCheckBatch checkBatch,
        CancellationToken ct,
        Guid? repairRunId
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        var missingSegmentIds = checkBatch.Results
            .Where(x => x.State == SegmentCheckState.Missing)
            .SelectMany(x => NzbSegmentIdSet.Decode(x.SegmentId))
            .ToList();

        if (FilenameUtil.IsImportantFileType(davItem.Name))
            AddCachedMissingSegmentIds(missingSegmentIds);

        ApplyMissingSegmentPolicy(davItem, dbClient, checkBatch, utcNow, SendStatus);
        await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                davItem.Id,
                priority: 0,
                now: utcNow,
                payloadJson: repairRunId.HasValue
                    ? DavDatabaseClient.CreateRepairRunPayloadJson(repairRunId.Value)
                    : null,
                ct: ct)
            .ConfigureAwait(false);
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                RepairEntryHealth.RepairEntryState.Missing,
                $"File had {checkBatch.Missing} missing articles. Queued automatic repair.",
                ct)
            .ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordUnknownVerificationAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        SegmentCheckBatch checkBatch,
        CancellationToken ct,
        Guid? repairRunId
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        ApplyUnknownVerificationPolicy(davItem, dbClient, checkBatch, utcNow, SendStatus);
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                checkBatch.ProviderErrors > 0
                    ? RepairEntryHealth.RepairEntryState.ProviderError
                    : RepairEntryHealth.RepairEntryState.Unknown,
                $"Verification inconclusive: provider_errors={checkBatch.ProviderErrors}, unknown={checkBatch.Unknown}.",
                ct)
            .ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordHealthyVerificationAsync
    (
        DavDatabaseClient dbClient,
        DavItem davItem,
        List<string> segments,
        Guid? repairRunId,
        bool useRecentlyVerifiedSegmentCache,
        CancellationToken ct,
        bool saveChanges = true
    )
    {
        ClearCachedMissingSegmentIds(segments);
        if (useRecentlyVerifiedSegmentCache)
            AddRecentlyVerifiedSegmentIds(segments);
        davItem.LastHealthCheck = DateTimeOffset.UtcNow;
        davItem.NextHealthCheck = GetNextHealthyCheckAt(davItem.LastHealthCheck.Value, davItem.ReleaseDate);
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = DateTimeOffset.UtcNow,
            Result = HealthCheckResult.HealthResult.Healthy,
            RepairStatus = HealthCheckResult.RepairAction.None,
            Message = "File is healthy."
        }));
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                RepairEntryHealth.RepairEntryState.Healthy,
                "File is healthy.",
                ct)
            .ConfigureAwait(false);
        if (saveChanges)
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static void ApplyMissingSegmentPolicy
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        SegmentCheckBatch checkBatch,
        DateTimeOffset utcNow,
        Func<HealthCheckResult, HealthCheckResult>? onResult = null
    )
    {
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + AutoRepairRetryDelay;
        var result = new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
            Message = $"File had {checkBatch.Missing} missing articles. Queued automatic repair."
        };
        dbClient.Ctx.HealthCheckResults.Add(onResult?.Invoke(result) ?? result);
    }

    public static void ApplyUnknownVerificationPolicy
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        SegmentCheckBatch checkBatch,
        DateTimeOffset utcNow,
        Func<HealthCheckResult, HealthCheckResult>? onResult = null
    )
    {
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + ProviderErrorRetryDelay;
        var result = new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.None,
            Message = string.Join(" ", [
                "File verification could not prove articles are missing.",
                $"checked={checkBatch.Checked}, provider_errors={checkBatch.ProviderErrors}, unknown={checkBatch.Unknown}.",
                "Will retry before automatic repair."
            ])
        };
        dbClient.Ctx.HealthCheckResults.Add(onResult?.Invoke(result) ?? result);
    }

    private static async Task MarkRepairEntryAsync
    (
        DavDatabaseClient dbClient,
        Guid? repairRunId,
        DavItem davItem,
        RepairEntryHealth.RepairEntryState state,
        string? message,
        CancellationToken ct
    )
    {
        if (!repairRunId.HasValue) return;
        await dbClient.UpsertRepairEntryAsync(
                repairRunId.Value,
                davItem.Id,
                davItem.Path,
                state,
                message,
                ct: ct)
            .ConfigureAwait(false);
    }

    private static async Task MarkRepairActionNeededAsync
    (
        DavDatabaseClient dbClient,
        Guid? repairRunId,
        DavItem davItem,
        string message,
        CancellationToken ct
    )
    {
        if (!repairRunId.HasValue) return;
        await MarkRepairEntryAsync(
                dbClient,
                repairRunId,
                davItem,
                RepairEntryHealth.RepairEntryState.ActionNeeded,
                message,
                ct)
            .ConfigureAwait(false);
        await dbClient.UpsertRepairBrokenFileAsync(
                repairRunId.Value,
                davItem.Id,
                davItem.Path,
                message,
                ct: ct)
            .ConfigureAwait(false);
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = segments.FirstOrDefault().ToNullIfEmpty();
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await _usenetClient.HeadWithFallbackAsync(firstSegmentId, ct).ConfigureAwait(false);
        var articleHeaders = articleHeadersResponse.ArticleHeaders!;
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<SegmentMetadataRead> GetAllSegmentMetadataAsync(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct)
    {
        var blobBackedRead = await TryReadBlobBackedSegmentsAsync(davItem, ct).ConfigureAwait(false);
        if (blobBackedRead.State != SegmentMetadataReadState.DatabaseFallback)
            return blobBackedRead;

        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.Ctx.NzbFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
                .ConfigureAwait(false);
            return FoundSegmentMetadata(davItem, DistinctSegmentIds(nzbFile?.SegmentIds));
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.Ctx.RarFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
                .ConfigureAwait(false);
            return FoundSegmentMetadata(davItem, DistinctSegmentIds(GetRarFileSegmentIds(rarFile)));
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.Ctx.MultipartFiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
                .ConfigureAwait(false);
            return FoundSegmentMetadata(davItem, DistinctSegmentIds(GetMultipartFileSegmentIds(multipartFile)));
        }

        return FoundSegmentMetadata(davItem, []);
    }

    private static async Task<List<SegmentMetadataRead>> GetAllSegmentMetadataAsync
    (
        IReadOnlyList<DavItem> davItems,
        DavDatabaseClient dbClient,
        CancellationToken ct
    )
    {
        var metadataByItemId = new Dictionary<Guid, SegmentMetadataRead>(davItems.Count);
        var databaseBackedItems = new List<DavItem>(davItems.Count);
        var blobBackedLookups = davItems
            .Select(davItem => TryReadBlobBackedSegmentsAsync(davItem, ct))
            .WithConcurrencyAsync(GetBlobSegmentReadConcurrency(davItems.Count), ct);

        await foreach (var metadata in blobBackedLookups.ConfigureAwait(false))
        {
            if (metadata.State != SegmentMetadataReadState.DatabaseFallback)
            {
                metadataByItemId[metadata.Item.Id] = metadata;
                continue;
            }

            databaseBackedItems.Add(metadata.Item);
        }

        await AddDatabaseBackedSegmentsAsync(databaseBackedItems, metadataByItemId, dbClient, ct)
            .ConfigureAwait(false);

        return davItems
            .Select(davItem => metadataByItemId.TryGetValue(davItem.Id, out var metadata)
                ? metadata
                : FoundSegmentMetadata(davItem, []))
            .ToList();
    }

    private static int GetBlobSegmentReadConcurrency(int itemCount)
    {
        if (itemCount <= 1) return 1;

        var coreBased = Math.Max(2, Environment.ProcessorCount * 2);
        return Math.Min(Math.Min(itemCount, MaxBlobSegmentReadConcurrency), coreBased);
    }

    private static async Task<SegmentMetadataRead> TryReadBlobBackedSegmentsAsync
    (
        DavItem davItem,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!davItem.FileBlobId.HasValue) return DatabaseFallbackSegmentMetadata(davItem);

        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var blob = await BlobStore.TryReadBlob<DavNzbFile>(davItem.FileBlobId.Value).ConfigureAwait(false);
            return ToSegmentMetadataRead(
                davItem,
                blob,
                value => DistinctSegmentIds(value.SegmentIds));
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var blob = await BlobStore.TryReadBlob<DavRarFile>(davItem.FileBlobId.Value).ConfigureAwait(false);
            return ToSegmentMetadataRead(
                davItem,
                blob,
                value => DistinctSegmentIds(GetRarFileSegmentIds(value)));
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var blob = await BlobStore.TryReadBlob<DavMultipartFile>(davItem.FileBlobId.Value).ConfigureAwait(false);
            return ToSegmentMetadataRead(
                davItem,
                blob,
                value => DistinctSegmentIds(GetMultipartFileSegmentIds(value)));
        }

        return DatabaseFallbackSegmentMetadata(davItem);
    }

    private static SegmentMetadataRead ToSegmentMetadataRead<T>(
        DavItem davItem,
        BlobStore.BlobReadResult<T> blob,
        Func<T, List<string>> getSegments)
    {
        return blob.Status switch
        {
            BlobStore.BlobReadStatus.Found => FoundSegmentMetadata(davItem, getSegments(blob.Value!)),
            BlobStore.BlobReadStatus.TemporarilyUnavailable => new SegmentMetadataRead(
                davItem,
                [],
                SegmentMetadataReadState.TemporarilyUnavailable,
                blob.Error),
            _ => DatabaseFallbackSegmentMetadata(davItem)
        };
    }

    private static SegmentMetadataRead FoundSegmentMetadata(DavItem davItem, List<string> segments)
    {
        return new SegmentMetadataRead(davItem, segments, SegmentMetadataReadState.Found, Error: null);
    }

    private static SegmentMetadataRead DatabaseFallbackSegmentMetadata(DavItem davItem)
    {
        return new SegmentMetadataRead(davItem, [], SegmentMetadataReadState.DatabaseFallback, Error: null);
    }

    private static async Task AddDatabaseBackedSegmentsAsync
    (
        IReadOnlyList<DavItem> davItems,
        Dictionary<Guid, SegmentMetadataRead> metadataByItemId,
        DavDatabaseClient dbClient,
        CancellationToken ct
    )
    {
        var davItemsById = davItems.ToDictionary(x => x.Id);
        var nzbItemIds = davItems
            .Where(x => x.SubType == DavItem.ItemSubType.NzbFile)
            .Select(x => x.Id)
            .ToList();
        if (nzbItemIds.Count > 0)
        {
            var nzbFiles = await dbClient.Ctx.NzbFiles
                .AsNoTracking()
                .Where(x => nzbItemIds.Contains(x.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var nzbFile in nzbFiles)
            {
                if (!davItemsById.TryGetValue(nzbFile.Id, out var davItem)) continue;
                metadataByItemId[nzbFile.Id] = FoundSegmentMetadata(
                    davItem,
                    DistinctSegmentIds(nzbFile.SegmentIds));
            }
        }

        var rarItemIds = davItems
            .Where(x => x.SubType == DavItem.ItemSubType.RarFile)
            .Select(x => x.Id)
            .ToList();
        if (rarItemIds.Count > 0)
        {
            var rarFiles = await dbClient.Ctx.RarFiles
                .AsNoTracking()
                .Where(x => rarItemIds.Contains(x.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var rarFile in rarFiles)
            {
                if (!davItemsById.TryGetValue(rarFile.Id, out var davItem)) continue;
                metadataByItemId[rarFile.Id] = FoundSegmentMetadata(
                    davItem,
                    DistinctSegmentIds(GetRarFileSegmentIds(rarFile)));
            }
        }

        var multipartItemIds = davItems
            .Where(x => x.SubType == DavItem.ItemSubType.MultipartFile)
            .Select(x => x.Id)
            .ToList();
        if (multipartItemIds.Count > 0)
        {
            var multipartFiles = await dbClient.Ctx.MultipartFiles
                .AsNoTracking()
                .Where(x => multipartItemIds.Contains(x.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var multipartFile in multipartFiles)
            {
                if (!davItemsById.TryGetValue(multipartFile.Id, out var davItem)) continue;
                metadataByItemId[multipartFile.Id] = FoundSegmentMetadata(
                    davItem,
                    DistinctSegmentIds(GetMultipartFileSegmentIds(multipartFile)));
            }
        }
    }

    private static IEnumerable<string> GetRarFileSegmentIds(DavRarFile? rarFile)
    {
        return rarFile?.RarParts?.SelectMany(x => x?.SegmentIds ?? []) ?? [];
    }

    private static IEnumerable<string> GetMultipartFileSegmentIds(DavMultipartFile? multipartFile)
    {
        return multipartFile?.Metadata?.FileParts?.SelectMany(GetMultipartFilePartSegmentIds) ?? [];
    }

    private static IEnumerable<string> GetMultipartFilePartSegmentIds(DavMultipartFile.FilePart? filePart)
    {
        if (filePart is null) return [];

        if (TryGetUsableSegmentSliceIds(filePart, out var sliceSegmentIds))
            return sliceSegmentIds;

        return filePart.SegmentIds ?? [];
    }

    private static bool TryGetUsableSegmentSliceIds(
        DavMultipartFile.FilePart filePart,
        out string[] segmentIds)
    {
        segmentIds = [];
        if (filePart.SegmentSlices is not { Length: > 0 }) return false;

        var sliceSegmentIds = new List<string>(filePart.SegmentSlices.Length);
        foreach (var slice in filePart.SegmentSlices)
        {
            if (slice is null) return false;
            if (string.IsNullOrWhiteSpace(slice.SegmentId)) return false;
            if (slice.SegmentByteRange is null || slice.FilePartByteRange is null) return false;
            if (slice.SegmentByteRange.Count <= 0 || slice.FilePartByteRange.Count <= 0) return false;
            sliceSegmentIds.Add(slice.SegmentId);
        }

        segmentIds = sliceSegmentIds.ToArray();
        return true;
    }

    private static List<string> DistinctSegmentIds(IEnumerable<string>? segmentIds)
    {
        return segmentIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
    }

    private static DateTimeOffset GetNextHealthyCheckAt(DateTimeOffset lastHealthCheck, DateTimeOffset? releaseDate)
    {
        if (!releaseDate.HasValue)
            return lastHealthCheck + ProviderErrorRetryDelay;

        var nextHealthCheck = releaseDate.Value + 2 * (lastHealthCheck - releaseDate.Value);
        return nextHealthCheck > lastHealthCheck
            ? nextHealthCheck
            : lastHealthCheck + ProviderErrorRetryDelay;
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct, Guid? repairRunId = null)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                var message = string.Join(" ", [
                    "File had missing articles.",
                    "Filename pattern is marked in settings as an ignored (unwanted) file.",
                    "Deleted file."
                ]);
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = message
                }));
                await MarkRepairEntryAsync(
                        dbClient,
                        repairRunId,
                        davItem,
                        RepairEntryHealth.RepairEntryState.Deleted,
                        message,
                        ct)
                    .ConfigureAwait(false);
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                var message = string.Join(" ", [
                    "File had missing articles.",
                    "Could not find corresponding symlink or strm-file within Library Dir.",
                    "Deleted file."
                ]);
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = message
                }));
                await MarkRepairEntryAsync(
                        dbClient,
                        repairRunId,
                        davItem,
                        RepairEntryHealth.RepairEntryState.Deleted,
                        message,
                        ct)
                    .ConfigureAwait(false);
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            var arrErrors = new List<string>();
            var matchingArrHosts = new List<string>();
            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await GetArrRootFolders(arrClient, arrErrors, ct).ConfigureAwait(false);
                if (!rootFolders.Any(x => IsPathInsideRoot(symlinkOrStrmPath, x.Path))) continue;
                matchingArrHosts.Add(arrClient.Host);

                // if we found a corresponding arr instance,
                // then remove and search.
                try
                {
                    if (await arrClient.RemoveAndSearch(symlinkOrStrmPath, ct).ConfigureAwait(false))
                    {
                        var message = string.Join(" ", [
                            "File had missing articles.",
                            $"Corresponding {linkType} found within Library Dir.",
                            $"Triggered new Arr search through `{arrClient.Host}`."
                        ]);
                        dbClient.Ctx.Items.Remove(davItem);
                        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                        {
                            Id = Guid.NewGuid(),
                            DavItemId = davItem.Id,
                            Path = davItem.Path,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Result = HealthCheckResult.HealthResult.Unhealthy,
                            RepairStatus = HealthCheckResult.RepairAction.Repaired,
                            Message = message
                        }));
                        await MarkRepairEntryAsync(
                                dbClient,
                                repairRunId,
                                davItem,
                                RepairEntryHealth.RepairEntryState.Repaired,
                                message,
                                ct)
                            .ConfigureAwait(false);
                        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception e)
                {
                    arrErrors.Add($"`{arrClient.Host}`: {e.Message}");
                }
            }

            var arrErrorText = arrErrors.Count == 0
                ? ""
                : $" Arr errors: {string.Join(" ", arrErrors)}";
            var repairFailureMessage = matchingArrHosts.Count > 0
                ? $"Found matching Arr root folder in {string.Join(", ", matchingArrHosts.Select(x => $"`{x}`"))}, but no instance matched the link to a tracked media item."
                : "Could not find a configured Arr root folder for this link.";
            var needsActionMessage = string.Join(" ", [
                "File had missing articles.",
                $"Corresponding {linkType} found within Library Dir.",
                repairFailureMessage,
                "Left the webdav-file and link in place and will retry automatic repair.",
                arrErrorText
            ]);
            await MarkAutoRepairNeedsAction(
                davItem,
                dbClient,
                needsActionMessage,
                ct
            ).ConfigureAwait(false);
            await MarkRepairActionNeededAsync(dbClient, repairRunId, davItem, needsActionMessage, ct)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await MarkRepairActionNeededAsync(
                    dbClient,
                    repairRunId,
                    davItem,
                    $"Error performing file repair: {e.Message}",
                    ct)
                .ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<List<ArrRootFolder>> GetArrRootFolders
    (
        ArrClient arrClient,
        List<string> errors,
        CancellationToken ct
    )
    {
        try
        {
            return await arrClient.GetRootFolders(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            errors.Add($"`{arrClient.Host}`: {e.Message}");
            return [];
        }
    }

    private async Task MarkAutoRepairNeedsAction
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        string message,
        CancellationToken ct
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + AutoRepairRetryDelay;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
            Message = message
        }));
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static bool IsPathInsideRoot(string path, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath)) return false;

        var normalizedPath = NormalizePathForRootMatch(path);
        var normalizedRoot = NormalizePathForRootMatch(rootPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (normalizedRoot == "/") return normalizedPath.StartsWith("/", comparison);

        return normalizedPath.Equals(normalizedRoot, comparison)
               || normalizedPath.StartsWith($"{normalizedRoot}/", comparison);
    }

    private static string NormalizePathForRootMatch(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            PruneExpiredMissingSegmentIds(DateTimeOffset.UtcNow);
            foreach (var segmentId in segmentIds)
            {
                var candidateSegmentIds = NzbSegmentIdSet.Decode(segmentId)
                    .Where(candidateSegmentId => !string.IsNullOrWhiteSpace(candidateSegmentId))
                    .ToArray();
                if (candidateSegmentIds.Length > 0
                    && candidateSegmentIds.All(candidateSegmentId => _missingSegmentIds.ContainsKey(candidateSegmentId)))
                    throw new UsenetArticleNotFoundException(segmentId);
            }
        }
    }

    public static void RememberMissingSegmentId(string segmentId)
    {
        AddCachedMissingSegmentIds(NzbSegmentIdSet.Decode(segmentId));
    }

    public static void RememberMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        AddCachedMissingSegmentIds(segmentIds.SelectMany(NzbSegmentIdSet.Decode));
    }

    private static void ClearCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds.SelectMany(NzbSegmentIdSet.Decode))
                _missingSegmentIds.Remove(segmentId);
        }
    }

    private static void AddCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            var utcNow = DateTimeOffset.UtcNow;
            PruneExpiredMissingSegmentIds(utcNow);
            foreach (var segmentId in segmentIds)
                _missingSegmentIds[segmentId] = utcNow;
            PruneOldestMissingSegmentIds();
        }
    }

    private static Dictionary<string, SegmentCheckResult> GetRecentlyVerifiedSegmentResults(IEnumerable<string> segmentIds)
    {
        var utcNow = DateTimeOffset.UtcNow;
        lock (_recentlyVerifiedSegmentIds)
        {
            PruneExpiredRecentlyVerifiedSegmentIds(utcNow);
            return segmentIds
                .Where(IsRecentlyVerified)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    segmentId => segmentId,
                    segmentId => new SegmentCheckResult(segmentId, SegmentCheckState.Exists, Provider: null, Error: null),
                    StringComparer.Ordinal);
        }

        bool IsRecentlyVerified(string segmentId)
        {
            if (_recentlyVerifiedSegmentIds.ContainsKey(segmentId))
                return true;

            return NzbSegmentIdSet
                .Decode(segmentId)
                .Any(candidateSegmentId => _recentlyVerifiedSegmentIds.ContainsKey(candidateSegmentId));
        }
    }

    public static void RememberRecentlyVerifiedSegmentIds(IEnumerable<string> segmentIds)
    {
        AddRecentlyVerifiedSegmentIds(segmentIds);
    }

    private static void AddRecentlyVerifiedSegmentResults(IEnumerable<SegmentCheckResult> results)
    {
        AddRecentlyVerifiedSegmentIds(results
            .Where(result => result.State == SegmentCheckState.Exists)
            .Select(GetRecentlyVerifiedSegmentCacheKey));
    }

    private static string GetRecentlyVerifiedSegmentCacheKey(SegmentCheckResult result)
    {
        return string.IsNullOrWhiteSpace(result.CandidateSegmentId)
            ? result.SegmentId
            : result.CandidateSegmentId;
    }

    private static void AddRecentlyVerifiedSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_recentlyVerifiedSegmentIds)
        {
            var utcNow = DateTimeOffset.UtcNow;
            PruneExpiredRecentlyVerifiedSegmentIds(utcNow);
            foreach (var segmentId in segmentIds.Where(x => !string.IsNullOrWhiteSpace(x)))
                _recentlyVerifiedSegmentIds[segmentId] = utcNow;
            PruneOldestRecentlyVerifiedSegmentIds();
        }
    }

    private static void PruneExpiredRecentlyVerifiedSegmentIds(DateTimeOffset utcNow)
    {
        var expiredSegmentIds = _recentlyVerifiedSegmentIds
            .Where(x => utcNow - x.Value > RecentlyVerifiedSegmentCacheTtl)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in expiredSegmentIds)
            _recentlyVerifiedSegmentIds.Remove(segmentId);
    }

    private static void PruneOldestRecentlyVerifiedSegmentIds()
    {
        var excessCount = _recentlyVerifiedSegmentIds.Count - MaxRecentlyVerifiedSegmentCacheEntries;
        if (excessCount <= 0) return;

        var oldestSegmentIds = _recentlyVerifiedSegmentIds
            .OrderBy(x => x.Value)
            .Take(excessCount)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in oldestSegmentIds)
            _recentlyVerifiedSegmentIds.Remove(segmentId);
    }

    private static void PruneExpiredMissingSegmentIds(DateTimeOffset utcNow)
    {
        var expiredSegmentIds = _missingSegmentIds
            .Where(x => utcNow - x.Value > MissingSegmentCacheTtl)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in expiredSegmentIds)
            _missingSegmentIds.Remove(segmentId);
    }

    private static void PruneOldestMissingSegmentIds()
    {
        var excessCount = _missingSegmentIds.Count - MaxMissingSegmentCacheEntries;
        if (excessCount <= 0) return;

        var oldestSegmentIds = _missingSegmentIds
            .OrderBy(x => x.Value)
            .Take(excessCount)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in oldestSegmentIds)
            _missingSegmentIds.Remove(segmentId);
    }

    public sealed record WorkerSnapshot(int VerifyActive, int RepairActive);
}

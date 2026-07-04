using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
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
    private const int WorkerMaxAttempts = 3;
    private const int MaxMissingSegmentCacheEntries = 100_000;

    private readonly ConfigManager _configManager;
    private readonly INntpClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly object _activeHealthChecksLock = new();
    private readonly HashSet<Guid> _activeHealthChecks = [];
    private int _activeVerificationJobs;
    private int _activeRepairJobs;

    private static readonly Dictionary<string, DateTimeOffset> _missingSegmentIds = [];

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
            lock (_missingSegmentIds) _missingSegmentIds.Clear();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var startedWorker = false;
                var segmentConcurrency = _configManager.GetAdaptiveHealthCheckConcurrency();
                var maxVerifyJobs = _configManager.GetAdaptiveMaxConcurrentVerifyJobs();
                var maxRepairJobs = _configManager.GetAdaptiveMaxConcurrentRepairJobs();
                var workerSegmentConcurrency = Math.Max(1, segmentConcurrency / Math.Max(1, maxVerifyJobs));

                var activeVerificationJobs = Volatile.Read(ref _activeVerificationJobs);
                while (activeVerificationJobs < maxVerifyJobs)
                {
                    var verifyJob = await LeaseNextVerificationJobAsync(stoppingToken).ConfigureAwait(false);
                    if (verifyJob == null) break;
                    if (!TryMarkHealthCheckActive(verifyJob.TargetId)) continue;

                    startedWorker = true;
                    Interlocked.Increment(ref _activeVerificationJobs);
                    activeVerificationJobs++;
                    _ = RunVerificationWorkerAsync(verifyJob, workerSegmentConcurrency, stoppingToken);
                }

                var activeRepairJobs = Volatile.Read(ref _activeRepairJobs);
                while (activeRepairJobs < maxRepairJobs)
                {
                    var repairJob = await LeaseNextRepairJobAsync(stoppingToken).ConfigureAwait(false);
                    if (repairJob == null) break;
                    if (!TryMarkHealthCheckActive(repairJob.TargetId)) continue;

                    startedWorker = true;
                    Interlocked.Increment(ref _activeRepairJobs);
                    activeRepairJobs++;
                    _ = RunRepairWorkerAsync(repairJob, stoppingToken);
                }

                var delay = startedWorker
                    ? TimeSpan.FromMilliseconds(250)
                    : GetActiveHealthCheckCount() > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
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

    private async Task<WorkerJob?> LeaseNextVerificationJobAsync(CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var currentDateTime = DateTimeOffset.UtcNow;
        var activeHealthCheckIds = GetActiveHealthCheckIds();
        IQueryable<DavItem> query = GetHealthCheckQueueItems(dbClient)
            .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime);

        if (activeHealthCheckIds.Length > 0)
            query = query.Where(x => !activeHealthCheckIds.Contains(x.Id));

        var dueItemIds = await query
            .Select(x => x.Id)
            .Take(16)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var davItemId in dueItemIds)
            await dbClient.EnqueueWorkerJobAsync(
                    WorkerJob.JobKind.Verify,
                    davItemId,
                    priority: 0,
                    now: currentDateTime,
                    ct: ct)
                .ConfigureAwait(false);

        return await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                owner: $"{Environment.MachineName}:{Environment.ProcessId}:verify",
                leaseDuration: WorkerLeaseDuration,
                now: currentDateTime,
                ct: ct)
            .ConfigureAwait(false);
    }

    private static async Task<WorkerJob?> LeaseNextRepairJobAsync(CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        return await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                owner: $"{Environment.MachineName}:{Environment.ProcessId}:repair",
                leaseDuration: WorkerLeaseDuration,
                ct: ct)
            .ConfigureAwait(false);
    }

    private async Task RunVerificationWorkerAsync(WorkerJob workerJob, int segmentConcurrency, CancellationToken ct)
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

            await PerformHealthCheckAsync(davItem, dbClient, segmentConcurrency, ct, repairRunId).ConfigureAwait(false);
            await dbClient.CompleteWorkerJobAsync(workerJob, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
        {
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
            ClearHealthCheckActive(workerJob.TargetId);
        }
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
        Guid? repairRunId = null
    )
    {
        try
        {
            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            if (davItem.ReleaseDate == null)
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
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce();
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            var progress = progressHook.ToPercentage(segments.Count);
            var checkBatch = await RunVerificationAsync(segments, concurrency, progress, ct).ConfigureAwait(false);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            if (checkBatch.Missing > 0)
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
            ClearCachedMissingSegmentIds(segments);
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;
            davItem.NextHealthCheck = davItem.ReleaseDate + 2 * (davItem.LastHealthCheck - davItem.ReleaseDate);
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
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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

    private async Task<SegmentCheckBatch> RunVerificationAsync
    (
        List<string> segments,
        int concurrency,
        IProgress<int> progress,
        CancellationToken ct
    )
    {
        return await _usenetClient.CheckSegmentsAsync(segments, concurrency, progress, ct).ConfigureAwait(false);
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

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
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
                var rootFolders = await GetArrRootFolders(arrClient, arrErrors).ConfigureAwait(false);
                if (!rootFolders.Any(x => IsPathInsideRoot(symlinkOrStrmPath, x.Path))) continue;
                matchingArrHosts.Add(arrClient.Host);

                // if we found a corresponding arr instance,
                // then remove and search.
                try
                {
                    if (await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false))
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
        List<string> errors
    )
    {
        try
        {
            return await arrClient.GetRootFolders().ConfigureAwait(false);
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
                if (NzbSegmentIdSet.Decode(segmentId).All(candidateSegmentId => _missingSegmentIds.ContainsKey(candidateSegmentId)))
                    throw new UsenetArticleNotFoundException(segmentId);
        }
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

using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Logging;
using NzbWebDAV.Services;
using NzbWebDAV.Security;
using NzbWebDAV.Telemetry;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

internal sealed class QueueWorkerLeaseLostException(Guid queueItemId, WorkerLeaseIdentity lease)
    : InvalidOperationException(
        $"Download worker lease {lease.JobId} generation {lease.Generation} no longer owns queue item {queueItemId}.")
{
}

internal sealed class QueueCompletionIndeterminateException(Guid queueItemId, Exception innerException)
    : InvalidOperationException(
        $"Queue completion state for {queueItemId} is indeterminate after a database acknowledgement failure.",
        innerException)
{
}

internal sealed class QueueCompletionNotCommittedException(Guid queueItemId, Exception innerException)
    : InvalidOperationException(
        $"Queue completion transaction for {queueItemId} did not commit and can be retried.",
        innerException)
{
}

public class QueueItemProcessor(
    QueueItem queueItem,
    Stream? queueNzbStream,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ArrDownloadReportService arrDownloadReportService,
    IProgress<int> progress,
    CancellationToken ct,
    Action<QueueProcessingStage>? onStageChanged = null,
    HistoryVisibilityNotifier? historyVisibilityNotifier = null,
    TimeProvider? timeProvider = null,
    WorkerLeaseIdentity? workerLease = null,
    Func<DavDatabaseContext>? completionContextFactory = null
)
{
    private readonly HistoryVisibilityNotifier _historyVisibilityNotifier =
        historyVisibilityNotifier ?? new HistoryVisibilityNotifier(configManager, websocketManager);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProcessingOutcome> ProcessAsync()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            onStageChanged?.Invoke(QueueProcessingStage.Downloading);
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");
            await arrDownloadReportService
                .RecordQueueLifecycleAsync(dbClient, queueItem, "Downloading", ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception.GetBaseException().IsCancellationException())
        {
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            if (ct.IsCancellationRequested)
            {
                dbClient.Ctx.ClearChangeTracker();
                return ProcessingOutcome.Cancelled;
            }
            Log.ForContext(
                    V1SafeConsoleFormatter.EventIdPropertyName,
                    V1OperationalEventId.QueueTerminalFailure)
                .Error(
                exception,
                "Could not persist initial lifecycle state for queue item {QueueItemId}; scheduling retry.",
                queueItem.Id);
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.RetryScheduled;
        }

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTimestamp).ConfigureAwait(false);
            return ProcessingOutcome.Completed;
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException().IsCancellationException())
        {
            Log.Information($"Processing of queue item `{queueItem.JobName}` was cancelled.");
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.Cancelled;
        }

        catch (QueueWorkerLeaseLostException e)
        {
            Log.Warning(e, "Stopped stale queue worker for {QueueItemId} before terminal commit.", queueItem.Id);
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.Cancelled;
        }

        catch (QueueCompletionIndeterminateException e)
        {
            Log.Error(e, "Queue completion state is indeterminate for {QueueItemId}.", queueItem.Id);
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.RetryScheduled;
        }

        catch (QueueCompletionNotCommittedException e)
        {
            Log.Warning(e, "Queue completion did not commit for {QueueItemId}; scheduling retry.", queueItem.Id);
            dbClient.Ctx.ClearChangeTracker();
            return ProcessingOutcome.RetryScheduled;
        }

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Error($"Failed to process job, `{queueItem.JobName}` -- {e.Message}");
                ct.ThrowIfCancellationRequested();
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = _timeProvider.GetLocalNow().DateTime.AddMinutes(1);
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
                await arrDownloadReportService
                    .RecordQueueLifecycleAsync(dbClient, queueItem, "Queued", "Download retry scheduled.", ct)
                    .ConfigureAwait(false);
                await arrDownloadReportService
                    .RefreshMonitoredDownloadsDebouncedAsync(queueItem.Category, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return ProcessingOutcome.RetryScheduled;
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
                if (ct.IsCancellationRequested || ex.GetBaseException().IsCancellationException())
                {
                    dbClient.Ctx.ClearChangeTracker();
                    return ProcessingOutcome.Cancelled;
                }
                return ProcessingOutcome.RetryScheduled;
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception)
        {
            var safeError = PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure);
            Log.Error(
                "{FailureSummary} Queue item {QueueItemId} reached a terminal failure.",
                safeError,
                queueItem.Id);
            try
            {
                await MarkQueueItemCompleted(startTimestamp, error: safeError).ConfigureAwait(false);
                return ProcessingOutcome.Completed;
            }
            catch (Exception)
            {
                Log.ForContext(
                        V1SafeConsoleFormatter.EventIdPropertyName,
                        V1OperationalEventId.QueueTerminalCommitFailure)
                    .Error(
                    "Could not record the terminal outcome for queue item {QueueItemId}.",
                    queueItem.Id);
                dbClient.Ctx.ClearChangeTracker();
                return ProcessingOutcome.RetryScheduled;
            }
        }
    }

    public enum ProcessingOutcome
    {
        Completed,
        RetryScheduled,
        Cancelled
    }

    private async Task ProcessQueueItemAsync(long startTimestamp)
    {
        // if the `/blobs` folder is tampered with outside the nzbdav process,
        // then it is possible that the nzb file goes missing.
        if (queueNzbStream is null)
            throw new NonRetryableDownloadException("The NZB file is missing from the queue store.");

        // load config for handling duplicate nzbs
        var existingMountFolder = await GetMountFolder().ConfigureAwait(false);
        var duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

        // if the mount folder already exists and setting is `marked-failed`
        // then immediately mark the job as failed.
        var isDuplicateNzb = existingMountFolder is not null;
        if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
        {
            const string error = "Duplicate nzb: the download folder for this nzb already exists.";
            await MarkQueueItemCompleted(startTimestamp, error, () => Task.FromResult(existingMountFolder))
                .ConfigureAwait(false);
            return;
        }

        // read the nzb document
        var parseStart = Stopwatch.GetTimestamp();
        var parseFailed = true;
        NzbDocument nzb;
        try
        {
            nzb = await LoadQueueNzbDocumentAsync().ConfigureAwait(false);
            parseFailed = false;
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.QueueParse,
                parseStart,
                parseFailed);
        }
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();

        // Look for a password in filename, submission params, and nzb document.
        // The file name's password takes priority, as an easy override.
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName) ??
            queueItem.ArchivePassword.ToNullIfEmpty() ??
            nzb.Metadata.GetValueOrDefault("password");

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        var articlesToPrecheck = nzbFiles
            .SelectMany(x => x.GetSegmentIds())
            .Distinct(StringComparer.Ordinal);
        HealthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        // step 1 -- get name and size of each nzb file
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var firstSegmentStart = Stopwatch.GetTimestamp();
        var firstSegmentFailed = true;
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> segments;
        try
        {
            segments = await FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
            firstSegmentFailed = false;
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.QueueFirstSegmentDiscovery,
                firstSegmentStart,
                firstSegmentFailed);
        }
        var par2DiscoveryStart = Stopwatch.GetTimestamp();
        var par2DiscoveryFailed = true;
        List<FileDesc> par2FileDescriptors;
        try
        {
            par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
                segments, usenetClient, ct).ConfigureAwait(false);
            par2DiscoveryFailed = false;
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.QueuePar2Discovery,
                par2DiscoveryStart,
                par2DiscoveryFailed);
        }
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 2 -- perform file processing
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword).ToList();
        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToMultiProgress(fileProcessors.Count);
        var processorsStart = Stopwatch.GetTimestamp();
        var processorsFailed = true;
        List<BaseProcessor.Result?> fileProcessingResultsAll;
        try
        {
            fileProcessingResultsAll = await fileProcessors
                .Select(x => x!.ProcessAsync(part2Progress.SubProgress))
                .WithConcurrencyAsync(configManager.GetAdaptiveQueueFileProcessingConcurrency())
                .GetAllAsync(ct).ConfigureAwait(false);
            processorsFailed = false;
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.QueueProcessors,
                processorsStart,
                processorsFailed);
        }
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        // step 3 -- optionally enqueue full article existence verification.
        // The durable verify lane keeps large STAT sweeps out of the download lane.
        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        var queuePostDownloadVerification = healthCheckCategories.Contains(queueItem.Category.ToLower());

        // update the database
        onStageChanged?.Invoke(QueueProcessingStage.Moving);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Moving");
        await arrDownloadReportService
            .RecordQueueLifecycleAsync(dbClient, queueItem, "Moving", ct: ct)
            .ConfigureAwait(false);
        await MarkQueueItemCompleted(startTimestamp, error: null, async () =>
        {
            var categoryFolder = await GetOrCreateCategoryFolder().ConfigureAwait(false);
            var mountFolder = await CreateMountFolder(categoryFolder, existingMountFolder, duplicateNzbBehavior)
                .ConfigureAwait(false);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth: false).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth: false).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth: false).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth: false).UpdateDatabase(fileProcessingResults);

            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlocklistedFilePostProcessor(configManager, dbClient).RemoveBlocklistedFiles();

            // validate video files found
            if (configManager.IsEnsureImportableVideoEnabled())
                new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();

            if (queuePostDownloadVerification)
            {
                if (usenetClient is ArticleCachingNntpClient articleCachingClient)
                    HealthCheckService.RememberRecentlyVerifiedSegmentIds(
                        articleCachingClient.GetFetchedSegmentIdsSnapshot());
                await EnqueuePostDownloadVerifyJobAsync(mountFolder).ConfigureAwait(false);
            }

            return mountFolder;
        }).ConfigureAwait(false);
        if (queuePostDownloadVerification)
            HealthWorkerWakeSignal.Pulse();
    }

    private async Task EnqueuePostDownloadVerifyJobAsync(DavItem mountFolder)
    {
        var hasVerifiableOutput = dbClient.Ctx.ChangeTracker
            .Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.HistoryItemId == queueItem.Id)
            .Any(ShouldEnqueuePostDownloadVerify);
        if (!hasVerifiableOutput) return;

        await dbClient.EnqueueWorkerJobsAsync(
                WorkerJob.JobKind.Verify,
                [mountFolder.Id],
                priority: GetPostDownloadVerifyPriority(queueItem.Priority),
                now: DateTimeOffset.UtcNow,
                payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson(),
                ct: ct,
                saveChanges: false)
            .ConfigureAwait(false);
    }

    private static bool ShouldEnqueuePostDownloadVerify(DavItem davItem)
    {
        return davItem.Type == DavItem.ItemType.UsenetFile
               && FilenameUtil.IsVideoFile(davItem.Name);
    }

    private static int GetPostDownloadVerifyPriority(QueueItem.PriorityOption priority)
    {
        return priority switch
        {
            QueueItem.PriorityOption.Force => 100,
            _ => 50
        };
    }

    private async Task<NzbDocument> LoadQueueNzbDocumentAsync()
    {
        try
        {
            return await NzbDocument.LoadAsync(queueNzbStream!).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new NonRetryableDownloadException("The NZB file could not be read from the queue store.");
        }
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword
    )
    {
        var groups = fileInfos
            .GroupBy(GetGroup);

        foreach (var group in groups)
        {
            if (group.Key == "7z")
                yield return new SevenZipProcessor(group.ToList(), usenetClient, configManager, archivePassword, ct);

            else if (group.Key == "rar")
                foreach (var fileInfo in group)
                    yield return new RarProcessor(fileInfo, usenetClient, archivePassword, ct);

            else if (group.Key == "multipart-mkv")
                yield return new MultipartMkvProcessor(group.ToList(), usenetClient, ct);

            else if (group.Key == "other")
                foreach (var fileInfo in group)
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
        }

        yield break;

        string GetGroup(GetFileInfosStep.FileInfo x) => false ? "impossible"
            : FilenameUtil.Is7zFile(x.FileName) ? "7z"
            : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
            : FilenameUtil.IsMultipartMkv(x.FileName) ? "multipart-mkv"
            : "other";
    }

    private async Task<DavItem?> GetMountFolder()
    {
        var query = from mountFolder in dbClient.Ctx.Items
                    join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
                    where mountFolder.Name == queueItem.JobName
                          && mountFolder.ParentId != null
                          && categoryFolder.Name == queueItem.Category
                          && categoryFolder.ParentId == DavItem.ContentFolder.Id
                    select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder()
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(categoryFolder);

        var mountFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: queueItem.Id,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder =
                await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: Guid.NewGuid(),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                subType: DavItem.ItemSubType.Directory,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: queueItem.Id,
                fileBlobId: null
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new Exception("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, long jobStartTimestamp, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = _timeProvider.GetLocalNow().DateTime,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = GetDownloadTimeSeconds(jobStartTimestamp, Stopwatch.GetTimestamp()),
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbBlobId = queueItem.Id,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        long startTimestamp,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        var safeError = PublicDiagnosticContract.FromOptional(error, PublicDiagnosticKind.QueueFailure);
        var completionStart = Stopwatch.GetTimestamp();
        var completionFailed = true;
        try
        {
            CompletionCommitResult completion;
            try
            {
                completion = await CommitQueueCompletionAsync(
                        startTimestamp,
                        safeError,
                        databaseOperations)
                    .ConfigureAwait(false);
            }
            catch (Exception commitException)
            {
                if (completionContextFactory is null)
                    throw UnwrapCompletionException(commitException);

                QueueCompletionReconciliation reconciliation;
                try
                {
                    reconciliation = await ReconcileQueueCompletionAsync(safeError).ConfigureAwait(false);
                }
                catch (Exception reconciliationException)
                {
                    throw new QueueCompletionIndeterminateException(
                        queueItem.Id,
                        new AggregateException(commitException, reconciliationException));
                }

                if (reconciliation == QueueCompletionReconciliation.NotCommitted)
                {
                    if (commitException is QueueCompletionPersistenceException persistenceException)
                    {
                        throw new QueueCompletionNotCommittedException(
                            queueItem.Id,
                            persistenceException.InnerException!);
                    }

                    throw UnwrapCompletionException(commitException);
                }
                if (reconciliation == QueueCompletionReconciliation.Indeterminate)
                    throw new QueueCompletionIndeterminateException(queueItem.Id, commitException);

                completionFailed = false;
                await PublishReconciledCompletionAsync().ConfigureAwait(false);
                return;
            }

            completionFailed = false;
            if (completion.StagedImportRefresh)
                ArrImportCommandWakeSignal.Pulse();
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
            try
            {
                if (!completion.StagedImportRefresh)
                {
                    await _historyVisibilityNotifier
                        .PublishIfVisibleAsync(dbClient.Ctx, completion.HistoryItemId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "Could not publish history visibility after completing queue item {QueueItemId}: {Message}",
                    queueItem.Id,
                    exception.Message);
            }
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.QueueCompletion,
                completionStart,
                completionFailed);
        }
    }

    private async Task<CompletionCommitResult> CommitQueueCompletionAsync(
        long startTimestamp,
        string? error,
        Func<Task<DavItem?>>? databaseOperations)
    {
        try
        {
            return await CommitQueueCompletionCoreAsync(startTimestamp, error, databaseOperations)
                .ConfigureAwait(false);
        }
        catch (QueueWorkerLeaseLostException)
        {
            throw;
        }
        catch (QueueCompletionBusinessException)
        {
            throw;
        }
        catch (Exception exception) when (exception.GetBaseException().IsCancellationException())
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new QueueCompletionPersistenceException(exception);
        }
    }

    private async Task<CompletionCommitResult> CommitQueueCompletionCoreAsync(
        long startTimestamp,
        string? error,
        Func<Task<DavItem?>>? databaseOperations)
    {
        var isolationLevel = dbClient.Ctx.Database.IsNpgsql()
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.Serializable;
        await using var transaction = await dbClient.Ctx.Database
            .BeginTransactionAsync(isolationLevel, ct)
            .ConfigureAwait(false);

        if (workerLease.HasValue)
        {
            var lease = workerLease.Value;
            var fenced = await dbClient.Ctx.WorkerJobs
                .Where(job => job.Id == lease.JobId
                              && job.Kind == WorkerJob.JobKind.Download
                              && job.TargetId == queueItem.Id
                              && job.Status == WorkerJob.JobStatus.Leased
                              && job.LeaseOwner == lease.Owner
                              && job.LeaseToken == lease.Token
                              && job.LeaseGeneration == lease.Generation
                              && job.CancelRequestedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(job => job.UpdatedAt, job => job.UpdatedAt),
                    ct)
                .ConfigureAwait(false);
            if (fenced != 1)
                throw new QueueWorkerLeaseLostException(queueItem.Id, lease);
        }

        dbClient.Ctx.ClearChangeTracker();
        DavItem? mountFolder;
        try
        {
            mountFolder = databaseOperations != null
                ? await databaseOperations.Invoke().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            throw new QueueCompletionBusinessException(exception);
        }
        var historyItem = CreateHistoryItem(mountFolder, startTimestamp, error);
        dbClient.Ctx.QueueItems.Remove(queueItem);
        dbClient.Ctx.HistoryItems.Add(historyItem);
        dbClient.Ctx.EnqueueRcloneVfsForgetPaths(["/nzbs"]);
        var stagedImportRefresh = false;
        if (error == null)
        {
            await new ImportReceiptService(dbClient.Ctx)
                .StageAvailableReceiptsAsync(historyItem.Id, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
            var changedDavItems = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
                .Where(x => x.State is EntityState.Added or EntityState.Deleted)
                .Select(x => x.Entity)
                .ToList();
            stagedImportRefresh = await arrDownloadReportService
                .StageCompletionRefreshAsync(
                    dbClient,
                    historyItem,
                    DavDatabaseContext.GetRcloneVfsForgetDirectories(changedDavItems),
                    ct)
                .ConfigureAwait(false);
        }
        await arrDownloadReportService
            .RecordHistoryLifecycleAsync(
                dbClient,
                historyItem,
                error == null ? "Completed" : "Failed",
                error,
                ct,
                saveChanges: false)
            .ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new CompletionCommitResult(historyItem.Id, stagedImportRefresh);
    }

    private static Exception UnwrapCompletionException(Exception exception)
    {
        return exception switch
        {
            QueueCompletionBusinessException businessException => businessException.InnerException!,
            QueueCompletionPersistenceException persistenceException => persistenceException.InnerException!,
            _ => exception
        };
    }

    private async Task<QueueCompletionReconciliation> ReconcileQueueCompletionAsync(string? error)
    {
        await using var freshContext = completionContextFactory!();
        var queueExists = await freshContext.QueueItems.AsNoTracking()
            .AnyAsync(item => item.Id == queueItem.Id, CancellationToken.None)
            .ConfigureAwait(false);
        var historyItem = await freshContext.HistoryItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == queueItem.Id, CancellationToken.None)
            .ConfigureAwait(false);
        var expectedStatus = error == null
            ? HistoryItem.DownloadStatusOption.Completed
            : HistoryItem.DownloadStatusOption.Failed;
        var historyMatches = historyItem is not null
                             && historyItem.DownloadStatus == expectedStatus
                             && historyItem.NzbBlobId == queueItem.Id
                             && (error == null || historyItem.FailMessage == error);

        if (!queueExists && historyMatches) return QueueCompletionReconciliation.Committed;
        if (queueExists && historyItem is null) return QueueCompletionReconciliation.NotCommitted;
        return QueueCompletionReconciliation.Indeterminate;
    }

    private async Task PublishReconciledCompletionAsync()
    {
        ArrImportCommandWakeSignal.Pulse();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        try
        {
            await using var freshContext = completionContextFactory!();
            await _historyVisibilityNotifier
                .PublishIfVisibleAsync(freshContext, queueItem.Id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Could not publish reconciled history visibility after completing queue item {QueueItemId}: {Message}",
                queueItem.Id,
                exception.Message);
        }
    }

    private sealed record CompletionCommitResult(Guid HistoryItemId, bool StagedImportRefresh);

    private sealed class QueueCompletionBusinessException(Exception innerException)
        : Exception("Queue completion business operation failed.", innerException)
    {
    }

    private sealed class QueueCompletionPersistenceException(Exception innerException)
        : Exception("Queue completion persistence failed.", innerException)
    {
    }

    private enum QueueCompletionReconciliation
    {
        Committed,
        NotCommitted,
        Indeterminate
    }

    private static int GetDownloadTimeSeconds(long startTimestamp, long endTimestamp)
    {
        var elapsedSeconds = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalSeconds;
        if (elapsedSeconds <= 0) return 0;
        if (elapsedSeconds >= int.MaxValue) return int.MaxValue;
        return (int)elapsedSeconds;
    }
}

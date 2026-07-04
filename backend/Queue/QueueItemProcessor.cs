using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    Stream? queueNzbStream,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    QueueWorkLaneCoordinator queueWorkLaneCoordinator,
    WebsocketManager websocketManager,
    ArrDownloadReportService arrDownloadReportService,
    IProgress<int> progress,
    CancellationToken ct,
    Action<QueueProcessingStage>? onStageChanged = null
)
{
    public async Task<ProcessingOutcome> ProcessAsync()
    {
        // initialize
        var startTime = DateTime.Now;
        onStageChanged?.Invoke(QueueProcessingStage.Downloading);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");
        await arrDownloadReportService
            .RecordQueueLifecycleAsync(dbClient, queueItem, "Downloading", ct: ct)
            .ConfigureAwait(false);

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
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

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Error($"Failed to process job, `{queueItem.JobName}` -- {e.Message}");
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now.AddMinutes(1);
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
                await arrDownloadReportService
                    .RecordQueueLifecycleAsync(dbClient, queueItem, "Queued", "Download retry scheduled.", ct)
                    .ConfigureAwait(false);
                await arrDownloadReportService
                    .RefreshMonitoredDownloadsDebouncedAsync(queueItem.Category, ct)
                    .ConfigureAwait(false);
                return ProcessingOutcome.RetryScheduled;
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
                return ProcessingOutcome.RetryScheduled;
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            try
            {
                await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
                return ProcessingOutcome.Completed;
            }
            catch (Exception ex)
            {
                Log.Error(e, ex.Message);
                return ProcessingOutcome.Completed;
            }
        }
    }

    public enum ProcessingOutcome
    {
        Completed,
        RetryScheduled,
        Cancelled
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        // if the `/blobs` folder is tampered with outside the nzbdav process,
        // then it is possible that the nzb file goes missing.
        if (queueNzbStream is null)
            throw new Exception($"The NZB file could not be found.");

        // load config for handling duplicate nzbs
        var existingMountFolder = await GetMountFolder().ConfigureAwait(false);
        var duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

        // if the mount folder already exists and setting is `marked-failed`
        // then immediately mark the job as failed.
        var isDuplicateNzb = existingMountFolder is not null;
        if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
        {
            const string error = "Duplicate nzb: the download folder for this nzb already exists.";
            await MarkQueueItemCompleted(startTime, error, () => Task.FromResult(existingMountFolder))
                .ConfigureAwait(false);
            return;
        }

        // read the nzb document
        var nzb = await NzbDocument.LoadAsync(queueNzbStream).ConfigureAwait(false);
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();

        // Look for a password in filename, submission params, and nzb document.
        // The file name's password takes priority, as an easy override.
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName) ??
            queueItem.ArchivePassword.ToNullIfEmpty() ??
            nzb.Metadata.GetValueOrDefault("password");

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.GetSegmentIds());
        HealthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        // step 1 -- get name and size of each nzb file
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
            nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, ct).ConfigureAwait(false);
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 2 -- perform file processing
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword).ToList();
        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToMultiProgress(fileProcessors.Count);
        var fileProcessingResultsAll = await fileProcessors
            .Select(x => x!.ProcessAsync(part2Progress.SubProgress))
            .WithConcurrencyAsync(configManager.GetAdaptiveQueueFileProcessingConcurrency())
            .GetAllAsync(ct).ConfigureAwait(false);
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        // step 3 -- Optionally check full article existence
        var checkedFullHealth = false;
        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        if (healthCheckCategories.Contains(queueItem.Category.ToLower()))
        {
            onStageChanged?.Invoke(QueueProcessingStage.WaitingForVerify);
            _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Verifying");
            await arrDownloadReportService
                .RecordQueueLifecycleAsync(dbClient, queueItem, "Verifying", ct: ct)
                .ConfigureAwait(false);
            var articlesToCheck = fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .SelectMany(x => x.NzbFile.GetSegmentIds())
                .ToList();
            var part3Progress = progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count);
            var healthCheckConcurrency = configManager
                .GetAdaptiveHealthCheckConcurrency();
            using (await queueWorkLaneCoordinator
                       .EnterVerifyAsync(configManager.GetAdaptiveMaxConcurrentVerifyJobs(), ct)
                       .ConfigureAwait(false))
            {
                onStageChanged?.Invoke(QueueProcessingStage.Verifying);
                await usenetClient
                    .CheckAllSegmentsAsync(articlesToCheck, healthCheckConcurrency, part3Progress, ct)
                    .ConfigureAwait(false);
            }
            checkedFullHealth = true;
        }

        // update the database
        onStageChanged?.Invoke(QueueProcessingStage.Moving);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Moving");
        await arrDownloadReportService
            .RecordQueueLifecycleAsync(dbClient, queueItem, "Moving", ct: ct)
            .ConfigureAwait(false);
        await MarkQueueItemCompleted(startTime, error: null, async () =>
        {
            var categoryFolder = await GetOrCreateCategoryFolder().ConfigureAwait(false);
            var mountFolder = await CreateMountFolder(categoryFolder, existingMountFolder, duplicateNzbBehavior)
                .ConfigureAwait(false);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlocklistedFilePostProcessor(configManager, dbClient).RemoveBlocklistedFiles();

            // validate video files found
            if (configManager.IsEnsureImportableVideoEnabled())
                new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();

            // create strm files, if necessary
            if (configManager.GetImportStrategy() is "strm" or "both")
                await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync(mountFolder)
                    .ConfigureAwait(false);

            return mountFolder;
        }).ConfigureAwait(false);
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

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = DateTime.Now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbBlobId = queueItem.Id,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DateTime startTime,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        dbClient.Ctx.ClearChangeTracker();
        var mountFolder = databaseOperations != null ? await databaseOperations.Invoke().ConfigureAwait(false) : null;
        var historyItem = CreateHistoryItem(mountFolder, startTime, error);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(historyItem, mountFolder, configManager);
        dbClient.Ctx.QueueItems.Remove(queueItem);
        dbClient.Ctx.HistoryItems.Add(historyItem);
        dbClient.Ctx.EnqueueRcloneVfsForgetPaths(["/nzbs"]);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await arrDownloadReportService
            .RecordHistoryLifecycleAsync(
                dbClient,
                historyItem,
                error == null ? "Completed" : "Failed",
                error,
                ct)
            .ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = arrDownloadReportService.RefreshMonitoredDownloadsDebouncedAsync(queueItem.Category);
    }
}

using System.Diagnostics;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Telemetry;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ArrDownloadReportService arrDownloadReportService,
    ArrOperationsService arrOperationsService,
    NzbBlobIngestCoordinator nzbBlobIngestCoordinator,
    TimeProvider? timeProvider = null
) : SabApiController.BaseController(httpContext, configManager)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore
    };

    public async Task<AddFileResponse> AddFileAsync(AddFileRequest request)
    {
        if (request.NzbFileStream == null)
            throw new BadHttpRequestException("Invalid nzbFile/name param");

        var id = Guid.NewGuid();
        var jobName = FilenameUtil.GetJobName(request.FileName);
        if (ConfigManager.GetDuplicateNzbBehavior() == "reject"
            && await arrOperationsService.HasRejectableDuplicateAsync(
                    dbClient.Ctx,
                    request.FileName,
                    jobName,
                    request.Category,
                    request.CancellationToken)
                .ConfigureAwait(false))
        {
            throw new BadHttpRequestException("Duplicate NZB rejected because an equivalent item is already active or recently completed.");
        }

        using var blobLease = await nzbBlobIngestCoordinator
            .AcquireAsync(id, request.CancellationToken)
            .ConfigureAwait(false);
        var cleanupIntent = new NzbBlobCleanupItem { Id = id };
        dbClient.Ctx.NzbBlobCleanupItems.Add(cleanupIntent);
        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);

        // write the file to the blob-store
        await using var stream = request.NzbFileStream;
        var blobWriteStart = Stopwatch.GetTimestamp();
        var blobWriteFailed = true;
        try
        {
            await BlobStore.WriteBlob(id, stream);
            blobWriteFailed = false;
        }
        finally
        {
            CriticalPathTelemetry.Shared.RecordElapsed(
                CriticalPathStage.AddFileBlobWrite,
                blobWriteStart,
                blobWriteFailed);
        }

        // save the queue item to the database
        QueueItem? queueItem;
        try
        {
            // backup the nzb file if enabled
            if (ConfigManager.IsNzbBackupEnabled())
            {
                var backupLocation = ConfigManager.GetNzbBackupLocation();
                if (backupLocation != null)
                {
                    await BackupNzbAsync(id, request.FileName, request.Category, backupLocation);
                }
            }

            // compute the total segment bytes
            await using var nzbFileStream = BlobStore.ReadBlob(id)
                ?? throw new FileNotFoundException($"NZB blob `{id}` was not found.");
            var nzbScanStart = Stopwatch.GetTimestamp();
            var nzbScanFailed = true;
            long totalSegmentBytes;
            try
            {
                totalSegmentBytes = ComputeTotalSegmentBytes(nzbFileStream);
                nzbScanFailed = false;
            }
            finally
            {
                CriticalPathTelemetry.Shared.RecordElapsed(
                    CriticalPathStage.AddFileNzbScan,
                    nzbScanStart,
                    nzbScanFailed);
            }

            // create the queue item record
            queueItem = CreateQueueItem(
                id,
                jobName,
                nzbFileStream.Length,
                totalSegmentBytes,
                request,
                _timeProvider);

            // record the original NZB filename so it can be served at download time
            var nzbName = new NzbName
            {
                Id = id,
                FileName = request.FileName
            };

            // save
            var commitStart = Stopwatch.GetTimestamp();
            var commitFailed = true;
            try
            {
                dbClient.Ctx.QueueItems.Add(queueItem);
                dbClient.Ctx.NzbNames.Add(nzbName);
                dbClient.Ctx.EnqueueRcloneVfsForgetPaths(["/nzbs"]);
                await dbClient.EnqueueWorkerJobsAsync(
                        WorkerJob.JobKind.Download,
                        [queueItem.Id],
                        (int)queueItem.Priority,
                        now: DateTimeOffset.UtcNow,
                        ct: request.CancellationToken,
                        saveChanges: false)
                    .ConfigureAwait(false);
                await arrDownloadReportService
                    .RecordQueueLifecycleAsync(
                        dbClient,
                        queueItem,
                        "Queued",
                        "NZB accepted.",
                        request.CancellationToken,
                        saveChanges: false)
                    .ConfigureAwait(false);
                dbClient.Ctx.NzbBlobCleanupItems.Remove(cleanupIntent);
                await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
                commitFailed = false;
            }
            finally
            {
                CriticalPathTelemetry.Shared.RecordElapsed(
                    CriticalPathStage.AddFileAtomicCommit,
                    commitStart,
                    commitFailed);
            }
        }
        catch
        {
            // The precommitted cleanup intent remains unless acceptance committed
            // the queue reference and removed the intent in the same transaction.
            throw;
        }

        // inform the frontend that a new item was added to the queue
        var message = GetQueueResponse.QueueSlot.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);

        // awaken the queue if it is sleeping
        queueManager.AwakenQueue(request.PauseUntil);

        // return response
        return new AddFileResponse()
        {
            Status = true,
            NzoIds = [queueItem.Id.ToString()],
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddFileRequest.New(RequestContext, ConfigManager).ConfigureAwait(false);
        return Ok(await AddFileAsync(request).ConfigureAwait(false));
    }

    private static async Task BackupNzbAsync(Guid id, string fileName, string category, string backupLocation)
    {
        try
        {
            if (!Directory.Exists(backupLocation))
                Directory.CreateDirectory(backupLocation);

            var destDir = Path.Combine(backupLocation, category);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".nzb";

            var destPath = Path.Combine(destDir, $"{baseName}{ext}");
            var counter = 2;
            while (System.IO.File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{baseName} ({counter}){ext}");
                counter++;
            }

            await using var src = BlobStore.ReadBlob(id)
                ?? throw new FileNotFoundException($"NZB blob `{id}` was not found.");
            await using var dst = System.IO.File.Create(destPath);
            await src.CopyToAsync(dst);
        }
        catch (Exception e)
        {
            throw new Exception($"Could not save nzb to `{backupLocation}`", e);
        }
    }

    private static long ComputeTotalSegmentBytes(Stream stream)
    {
        long totalBytes = 0;
        using var reader = XmlReader.Create(stream, XmlSettings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "segment") continue;
            var bytesAttr = reader.GetAttribute("bytes");
            if (bytesAttr != null && long.TryParse(bytesAttr, out var bytes))
            {
                totalBytes += bytes;
            }
        }

        return totalBytes;
    }

    internal static QueueItem CreateQueueItem(
        Guid id,
        string jobName,
        long nzbFileSize,
        long totalSegmentBytes,
        AddFileRequest request,
        TimeProvider timeProvider)
    {
        return new QueueItem
        {
            Id = id,
            CreatedAt = timeProvider.GetLocalNow().DateTime,
            FileName = request.FileName,
            JobName = jobName,
            NzbFileSize = nzbFileSize,
            TotalSegmentBytes = totalSegmentBytes,
            Category = request.Category,
            ArchivePassword = request.ArchivePassword,
            Priority = request.Priority,
            PostProcessing = request.PostProcessing,
            PauseUntil = request.PauseUntil
        };
    }
}

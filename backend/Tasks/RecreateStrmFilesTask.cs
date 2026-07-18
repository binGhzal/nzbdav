using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RecreateStrmFilesTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager,
    MaintenanceProgressReporter? progressReporter = null
) : BaseTask(websocketManager, WebsocketTopic.RecreateStrmFilesTaskProgress, progressReporter)
{
    public async Task RecreateStrmFiles(CancellationToken cancellationToken = default)
    {
        Report("Collecting all strm file candidates...");

        var files = dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .WhereVideoFiles()
            .AsAsyncEnumerable();
        var progress = 0;

        await foreach (var file in files.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportDebounced($"Creating strm file {progress + 1}.", progress, null);
            try
            {
                await StrmFileUtil.CreateStrmFileAsync(configManager, file).ConfigureAwait(false);
                progress++;
            }
            catch (Exception e) when (IsPerFileFilesystemException(e))
            {
                Log.Warning(e, "Skipping strm file recreation for DAV item {DavItemId} at {Path}.", file.Id, file.Path);
            }
        }

        Report($"Done. Created {progress} strm files.", progress, progress);
    }

    private static bool IsPerFileFilesystemException(Exception exception) =>
        exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;

    protected override Task ExecuteInternal(CancellationToken cancellationToken)
    {
        return RecreateStrmFiles(cancellationToken);
    }
}

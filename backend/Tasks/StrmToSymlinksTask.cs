using System.Web;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class StrmToSymlinksTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager
) : BaseTask(websocketManager, WebsocketTopic.StrmToSymlinksTaskProgress)
{
    protected override async Task ExecuteInternal()
    {
        try
        {
            var ct = SigtermUtil.GetCancellationToken();
            await ConvertAllStrmFilesToSymlinks(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to convert *.strm files to symlinks.");
        }
    }

    private async Task ConvertAllStrmFilesToSymlinks(CancellationToken token)
    {
        var completedCount = 0;
        var batches = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager)
            .Where(x => x.SymlinkOrStrmInfo is SymlinkAndStrmUtil.StrmInfo)
            .ToBatches(batchSize: 100);

        ReportProgress("Scanning library for strm files...", completedCount);
        foreach (var batch in batches)
            await ConvertBatchOfStrmFilesToSymlinks(batch, OnItemCompleted, token).ConfigureAwait(false);
        ReportProgress("Done!", completedCount);
        return;

        void OnItemCompleted()
        {
            completedCount++;
            ReportProgress("Scanning library for strm files...", completedCount, true);
        }
    }

    private async Task ConvertBatchOfStrmFilesToSymlinks
    (
        List<OrganizedLinksUtil.DavItemLink> batch,
        Action onItemCompleted,
        CancellationToken token
    )
    {
        var items = batch
            .Select(x => new { Link = x, Extension = GetExtension(x) })
            .ToList();
        var davItemsToFetch = items
            .Select(x => x.Link.DavItemId)
            .Distinct()
            .ToList();
        var davItems = await dbClient.Ctx.Items
            .AsNoTracking()
            .Where(x => davItemsToFetch.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x, token).ConfigureAwait(false);
        var itemsWithExtension = items
            .Select(x =>
            {
                if (!davItems.TryGetValue(x.Link.DavItemId, out var davItem))
                {
                    Log.Warning(
                        "Skipping stale strm file {StrmPath}: DAV item {DavItemId} no longer exists.",
                        x.Link.LinkPath,
                        x.Link.DavItemId);
                    return null;
                }

                var extension = GetSafeExtension(x.Extension) ?? GetSafeExtension(Path.GetExtension(davItem.Name));
                if (extension is null)
                {
                    Log.Warning(
                        "Skipping strm file {StrmPath}: no safe extension could be resolved for DAV item {DavItemId}.",
                        x.Link.LinkPath,
                        x.Link.DavItemId);
                    return null;
                }

                return new
                {
                    x.Link,
                    Extension = extension,
                };
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        var mountDir = configManager.GetMountDir();
        foreach (var item in itemsWithExtension)
        {
            var symlinkPath = PathUtil.ReplaceExtension(item.Link.LinkPath, item.Extension);
            var symlinkTarget = DatabaseStoreSymlinkFile.GetTargetPath(item.Link.DavItemId, mountDir);
            try
            {
                await Task.Run(() => ConvertStrmToSymlink(item.Link.LinkPath, symlinkPath, symlinkTarget), token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (IsPerFileFilesystemException(e))
            {
                Log.Warning(
                    e,
                    "Skipping strm file {StrmPath}: failed to create symlink {SymlinkPath}.",
                    item.Link.LinkPath,
                    symlinkPath);
            }
            finally
            {
                onItemCompleted?.Invoke();
            }
        }
    }

    private static void ConvertStrmToSymlink(string strmPath, string symlinkPath, string symlinkTarget)
    {
        if (TryGetSymlinkTarget(symlinkPath, out var existingTarget))
        {
            if (existingTarget == symlinkTarget)
            {
                File.Delete(strmPath);
                return;
            }

            Log.Warning(
                "Skipping strm file {StrmPath}: target path {SymlinkPath} already points to {ExistingTarget}.",
                strmPath,
                symlinkPath,
                existingTarget);
            return;
        }

        if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
        {
            Log.Warning(
                "Skipping strm file {StrmPath}: target path {SymlinkPath} already exists and is not an expected symlink.",
                strmPath,
                symlinkPath);
            return;
        }

        File.CreateSymbolicLink(symlinkPath, symlinkTarget);
        File.Delete(strmPath);
    }

    private static bool TryGetSymlinkTarget(string path, out string? target)
    {
        try
        {
            target = new FileInfo(path).LinkTarget;
            return target is not null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            target = null;
            return false;
        }
    }

    private static bool IsPerFileFilesystemException(Exception exception) =>
        exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;

    private string? GetExtension(OrganizedLinksUtil.DavItemLink link)
    {
        if (link.SymlinkOrStrmInfo is not SymlinkAndStrmUtil.StrmInfo strmInfo) return null;
        if (!Uri.TryCreate(strmInfo.TargetUrl, UriKind.Absolute, out var targetUri)) return null;
        var queryParams = HttpUtility.ParseQueryString(targetUri.Query);
        return queryParams.Get("extension");
    }

    private static string? GetSafeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;

        var normalized = extension.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized is "." or "..") return null;
        if (normalized.Contains('\0')) return null;
        if (normalized.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) return null;
        if (normalized.Contains(Path.VolumeSeparatorChar)) return null;

        return normalized;
    }

    private void ReportProgress(string message, int completedCount, bool debounce = false)
    {
        Action<string> report = debounce ? ReportDebounced : Report;
        report($"{message}\nConverted: {completedCount} strm file(s) to symlinks.");
    }
}

using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.PostProcessors;

public class CreateStrmFilesPostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    public async Task CreateStrmFilesAsync(DavItem mountFolder)
    {
        var videoItems = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is not EntityState.Deleted and not EntityState.Detached)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => IsInsideMountFolder(x, mountFolder))
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .DistinctBy(x => x.Id)
            .ToList();

        foreach (var videoItem in videoItems)
            await StrmFileUtil.CreateStrmFileAsync(configManager, videoItem).ConfigureAwait(false);
    }

    private static bool IsInsideMountFolder(DavItem item, DavItem mountFolder)
    {
        var mountPath = mountFolder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return item.Path.StartsWith(mountPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || item.Path.StartsWith(mountPath + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

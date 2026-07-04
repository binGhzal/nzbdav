using System.Text;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreSymlinkFile(DavItem davFile, ConfigManager configManager) : BaseStoreReadonlyItem
{
    public override string Name => davFile.Name + ".rclonelink";
    public override string UniqueKey => davFile.Id + ".rclonelink";
    public override long FileSize => ContentBytes.Length;
    public override DateTime CreatedAt => davFile.CreatedAt;

    private byte[] ContentBytes => Encoding.UTF8.GetBytes(GetTargetPath());

    public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<Stream>(new MemoryStream(ContentBytes));
    }

    private string GetTargetPath()
    {
        return GetTargetPath(
            davFile.Id,
            configManager.GetMountDir(),
            configManager.GetSymlinkTargetMode(),
            davFile.Path);
    }

    public static string GetTargetPath(Guid davItemId, string mountDir, char? pathSeparator = null)
    {
        return GetTargetPath(davItemId, mountDir, "absolute", contentPath: null, pathSeparator: pathSeparator);
    }

    public static string GetTargetPath
    (
        Guid davItemId,
        string mountDir,
        string targetMode,
        string? contentPath,
        char? pathSeparator = null
    )
    {
        if (targetMode.Equals("relative", StringComparison.OrdinalIgnoreCase))
            return GetRelativeTargetPath(davItemId, contentPath, pathSeparator);

        var pathParts = new List<string> { mountDir, GetTargetPath(davItemId, pathSeparator) };
        return string.Join(pathSeparator ?? Path.DirectorySeparatorChar, pathParts);
    }

    private static string GetRelativeTargetPath(Guid davItemId, string? contentPath, char? pathSeparator = null)
    {
        var separator = pathSeparator ?? Path.DirectorySeparatorChar;
        var targetPath = GetTargetPath(davItemId, pathSeparator);
        if (string.IsNullOrWhiteSpace(contentPath)) return targetPath;

        var symlinkPath = contentPath.Replace('\\', '/');
        if (symlinkPath.StartsWith("/content/", StringComparison.Ordinal))
            symlinkPath = "/" + DavItem.SymlinkFolder.Name + symlinkPath["/content".Length..];
        else if (symlinkPath.Equals("/content", StringComparison.Ordinal))
            symlinkPath = "/" + DavItem.SymlinkFolder.Name;

        var parentDepth = Math.Max(0, symlinkPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Length - 1);
        if (parentDepth == 0) return targetPath;

        var upSegments = Enumerable.Repeat("..", parentDepth);
        return string.Join(separator, upSegments.Append(targetPath));
    }

    public static string GetTargetPath(Guid davItemId, char? pathSeparator = null)
    {
        var pathParts = davItemId.GetFiveLengthPrefix()
            .Select(x => x.ToString())
            .Prepend(DavItem.IdsFolder.Name)
            .Append(davItemId.ToString())
            .ToArray();
        return string.Join(pathSeparator ?? Path.DirectorySeparatorChar, pathParts);
    }
}

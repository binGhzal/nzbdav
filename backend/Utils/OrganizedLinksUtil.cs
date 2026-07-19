using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Utils;

public static class OrganizedLinksUtil
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();

    /// <summary>
    /// Searches the organized media library for a symlink pointing to the given target.
    /// </summary>
    /// <param name="targetDavItem">The given target</param>
    /// <param name="configManager">The application config</param>
    /// <returns>The path to a symlink in the organized media library that points to the given target.</returns>
    public static string? GetLink(DavItem targetDavItem, ConfigManager configManager)
    {
        return !TryGetLinkFromCache(targetDavItem, configManager, out var linkFromCache)
            ? SearchForLink(targetDavItem, configManager)
            : linkFromCache;
    }

    /// <summary>
    /// Enumerates all DavItemLinks within the organized media library that point to nzbdav dav-items.
    /// </summary>
    /// <param name="configManager">The application config</param>
    /// <returns>All DavItemLinks within the organized media library that point to nzbdav dav-items.</returns>
    public static IEnumerable<DavItemLink> GetLibraryDavItemLinks(ConfigManager configManager)
    {
        var libraryRoot = configManager.GetLibraryDir()!;
        var allSymlinks = SymlinkUtil.GetAllSymlinks(libraryRoot);
        return GetDavItemLinks(allSymlinks, configManager);
    }

    private static bool TryGetLinkFromCache
    (
        DavItem targetDavItem,
        ConfigManager configManager,
        out string? linkFromCache
    )
    {
        return Cache.TryGetValue(targetDavItem.Id, out linkFromCache)
               && Verify(linkFromCache, targetDavItem, configManager);
    }

    private static bool Verify(string linkFromCache, DavItem targetDavItem, ConfigManager configManager)
    {
        if (!IsLinkInsideCurrentLibrary(linkFromCache, configManager.GetLibraryDir())) return false;

        var mountDir = configManager.GetMountDir();
        var fileInfo = new FileInfo(linkFromCache);
        var symlinkInfo = SymlinkUtil.GetSymlinkInfo(fileInfo);
        if (symlinkInfo == null) return false;
        var davItemLink = GetDavItemLink(symlinkInfo.Value, mountDir);
        return davItemLink?.DavItemId == targetDavItem.Id;
    }

    private static string? SearchForLink(DavItem targetDavItem, ConfigManager configManager)
    {
        string? result = null;
        foreach (var davItemLink in GetLibraryDavItemLinks(configManager))
        {
            Cache[davItemLink.DavItemId] = davItemLink.LinkPath;
            if (davItemLink.DavItemId == targetDavItem.Id)
                result = davItemLink.LinkPath;
        }

        return result;
    }

    private static bool IsLinkInsideCurrentLibrary(string linkPath, string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(libraryRoot)) return false;

        try
        {
            var normalizedLink = TrimDirectorySeparator(Path.GetFullPath(linkPath));
            var normalizedRoot = TrimDirectorySeparator(Path.GetFullPath(libraryRoot));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return normalizedLink.Equals(normalizedRoot, comparison)
                   || normalizedLink.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", comparison)
                   || normalizedLink.StartsWith($"{normalizedRoot}{Path.AltDirectorySeparatorChar}", comparison);
        }
        catch (Exception e) when (e is ArgumentException
                                  or IOException
                                  or NotSupportedException
                                  or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string TrimDirectorySeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<DavItemLink> GetDavItemLinks
    (
        IEnumerable<SymlinkUtil.SymlinkInfo> symlinkInfos,
        ConfigManager configManager
    )
    {
        var mountDir = configManager.GetMountDir();
        return symlinkInfos
            .Select(x => GetDavItemLink(x, mountDir))
            .Where(x => x != null)
            .Select(x => x!.Value);
    }

    private static DavItemLink? GetDavItemLink(SymlinkUtil.SymlinkInfo symlinkInfo, string mountDir)
    {
        if (!TryGetDavItemIdFromSymlinkTarget(symlinkInfo, mountDir, out var davItemId)) return null;
        return new DavItemLink()
        {
            LinkPath = symlinkInfo.SymlinkPath,
            DavItemId = davItemId,
            SymlinkInfo = symlinkInfo
        };
    }

    private static bool TryGetDavItemIdFromSymlinkTarget
    (
        SymlinkUtil.SymlinkInfo symlinkInfo,
        string mountDir,
        out Guid davItemId
    )
    {
        davItemId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(symlinkInfo.TargetPath)
            || string.IsNullOrWhiteSpace(symlinkInfo.SymlinkPath)
            || string.IsNullOrWhiteSpace(mountDir))
            return false;

        try
        {
            var symlinkParent = Path.GetDirectoryName(Path.GetFullPath(symlinkInfo.SymlinkPath));
            if (string.IsNullOrWhiteSpace(symlinkParent)) return false;

            var resolvedTarget = Path.GetFullPath(symlinkInfo.TargetPath, symlinkParent);
            if (!Guid.TryParseExact(Path.GetFileName(resolvedTarget), "D", out davItemId)) return false;

            var expectedTarget = Path.GetFullPath(
                Path.Join(mountDir, DatabaseStoreSymlinkFile.GetTargetPath(davItemId)));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (resolvedTarget.Equals(expectedTarget, comparison)) return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or UnauthorizedAccessException)
        {
            // Treat malformed or inaccessible link targets as unrecognized library entries.
        }

        davItemId = Guid.Empty;
        return false;
    }

    public struct DavItemLink
    {
        public string LinkPath;
        public Guid DavItemId;
        public SymlinkUtil.SymlinkInfo SymlinkInfo;
    }
}

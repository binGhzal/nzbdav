using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Utils;

/// <summary>
/// Note: In this class, a `Link` refers to either a symlink or strm file.
/// </summary>
public static class OrganizedLinksUtil
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();

    /// <summary>
    /// Searches organized media library for a symlink or strm pointing to the given target
    /// </summary>
    /// <param name="targetDavItem">The given target</param>
    /// <param name="configManager">The application config</param>
    /// <returns>The path to a symlink or strm in the organized media library that points to the given target.</returns>
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
        var allSymlinksAndStrms = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot);
        return GetDavItemLinks(allSymlinksAndStrms, configManager);
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
        var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);
        if (symlinkOrStrmInfo == null) return false;
        var davItemLink = GetDavItemLink(symlinkOrStrmInfo, mountDir);
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
        IEnumerable<SymlinkAndStrmUtil.ISymlinkOrStrmInfo> symlinkOrStrmInfos,
        ConfigManager configManager
    )
    {
        var mountDir = configManager.GetMountDir();
        return symlinkOrStrmInfos
            .Select(x => GetDavItemLink(x, mountDir))
            .Where(x => x != null)
            .Select(x => x!.Value);
    }

    private static DavItemLink? GetDavItemLink
    (
        SymlinkAndStrmUtil.ISymlinkOrStrmInfo symlinkOrStrmInfo,
        string mountDir
    )
    {
        return symlinkOrStrmInfo switch
        {
            SymlinkAndStrmUtil.SymlinkInfo symlinkInfo => GetDavItemLink(symlinkInfo, mountDir),
            SymlinkAndStrmUtil.StrmInfo strmInfo => GetDavItemLink(strmInfo),
            _ => throw new Exception("Unknown link type")
        };
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.SymlinkInfo symlinkInfo, string mountDir)
    {
        if (!TryGetDavItemIdFromSymlinkTarget(symlinkInfo.TargetPath, mountDir, out var davItemId)) return null;
        return new DavItemLink()
        {
            LinkPath = symlinkInfo.SymlinkPath,
            DavItemId = davItemId,
            SymlinkOrStrmInfo = symlinkInfo
        };
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.StrmInfo strmInfo)
    {
        var targetUrl = strmInfo.TargetUrl;
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)) return null;
        if (!TryGetDavItemIdFromStrmPath(uri.AbsolutePath, out var davItemId)) return null;
        return new DavItemLink()
        {
            LinkPath = strmInfo.StrmPath,
            DavItemId = davItemId,
            SymlinkOrStrmInfo = strmInfo
        };
    }

    private static bool TryGetDavItemIdFromStrmPath(string path, out Guid davItemId)
    {
        const string viewIdsPrefix = "/view/.ids";

        if (TryGetDavItemIdFromPath(path, viewIdsPrefix, out davItemId))
            return true;

        var normalizedPath = NormalizeLinkPath(path);
        var prefixIndex = normalizedPath.IndexOf(viewIdsPrefix + "/", StringComparison.Ordinal);
        if (prefixIndex < 0) return false;

        return TryGetDavItemIdFromPath(normalizedPath[prefixIndex..], viewIdsPrefix, out davItemId);
    }

    private static bool TryGetDavItemIdFromPath(string path, string requiredPrefix, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (!path.StartsWith(requiredPrefix, StringComparison.Ordinal)) return false;

        var fileNameStart = path.LastIndexOfAny(['/', '\\']);
        var fileName = fileNameStart >= 0 ? path[(fileNameStart + 1)..] : path;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        return Guid.TryParse(Path.GetFileNameWithoutExtension(fileName), out davItemId);
    }

    private static bool TryGetDavItemIdFromSymlinkTarget(string targetPath, string mountDir, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        var normalizedTarget = NormalizeLinkPath(targetPath);
        var normalizedMount = NormalizeLinkPath(mountDir).TrimEnd('/');

        if (IsRootedPath(normalizedTarget))
        {
            if (!IsAtOrInsideMount(normalizedTarget, normalizedMount)) return false;
            var targetRelativeToMount = normalizedTarget.RemovePrefix(normalizedMount);
            targetRelativeToMount = targetRelativeToMount.StartsWith('/')
                ? targetRelativeToMount
                : $"/{targetRelativeToMount}";
            return TryGetDavItemIdFromPath(targetRelativeToMount, "/.ids", out davItemId);
        }

        var relativeIdsPath = TrimLeadingRelativeSegments(normalizedTarget);
        return TryGetDavItemIdFromPath(relativeIdsPath, "/.ids", out davItemId);
    }

    private static bool IsAtOrInsideMount(string path, string mountDir)
    {
        if (string.IsNullOrWhiteSpace(mountDir)) return false;
        return path.Equals(mountDir, StringComparison.Ordinal)
               || path.StartsWith($"{mountDir}/", StringComparison.Ordinal);
    }

    private static bool IsRootedPath(string path)
    {
        return (path.Length > 0 && path[0] == '/')
               || (path.Length > 0 && path[0] == '\\')
               || (path.Length >= 2 && path[1] == ':');
    }

    private static string NormalizeLinkPath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private static string TrimLeadingRelativeSegments(string path)
    {
        var parts = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(x => x is "." or "..")
            .ToArray();
        return parts.Length == 0 ? "/" : "/" + string.Join('/', parts);
    }

    public struct DavItemLink
    {
        public string LinkPath; // Path to either a symlink or strm file.
        public Guid DavItemId;
        public SymlinkAndStrmUtil.ISymlinkOrStrmInfo SymlinkOrStrmInfo;
    }
}

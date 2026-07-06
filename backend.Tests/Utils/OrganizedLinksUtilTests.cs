using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using System.Collections.Concurrent;
using System.Reflection;

namespace backend.Tests.Utils;

public sealed class OrganizedLinksUtilTests
{
    [Fact]
    public void GetLibraryDavItemLinks_SkipsMalformedStrmUrls()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            File.WriteAllText(Path.Join(libraryRoot, "Broken.strm"), "not a url");
            File.WriteAllText(
                Path.Join(libraryRoot, "Valid.strm"),
                $"http://localhost:3000/view/.ids/{validId}.mkv?downloadKey=test&extension=mkv");
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_SkipsMalformedDavItemIds()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            File.WriteAllText(
                Path.Join(libraryRoot, "Broken.strm"),
                "http://localhost:3000/view/.ids/not-a-guid.mkv?downloadKey=test&extension=mkv");
            File.WriteAllText(
                Path.Join(libraryRoot, "Valid.strm"),
                $"http://localhost:3000/view/.ids/{validId}.mkv?downloadKey=test&extension=mkv");
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_MapsStrmUrlsBehindUrlBase()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            File.WriteAllText(
                Path.Join(libraryRoot, "Valid.strm"),
                $"http://localhost:3000/nzbdav/view/.ids/{validId}.mkv?downloadKey=test&extension=mkv");
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_UsesFirstStrmLine()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            File.WriteAllText(
                Path.Join(libraryRoot, "Valid.strm"),
                string.Join(Environment.NewLine, [
                    $"http://localhost:3000/view/.ids/{validId}.mkv?downloadKey=test&extension=mkv",
                    "partial trailing write that should not be part of the target url"
                ]));
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_SkipsMalformedSymlinkDavItemIds()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Broken.mkv"),
                "/mnt/nzbdav/.ids/not-a-guid.mkv");
            File.WriteAllText(
                Path.Join(libraryRoot, "Valid.strm"),
                $"http://localhost:3000/view/.ids/{validId}.mkv?downloadKey=test&extension=mkv");
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_SkipsSymlinksOutsideMountDirectoryBoundary()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var outsideId = Guid.NewGuid();
            var validId = Guid.NewGuid();
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Outside.mkv"),
                $"/mnt/nzbdav-other/.ids/{outsideId}.mkv");
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Valid.mkv"),
                $"/mnt/nzbdav/.ids/{validId}.mkv");
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_MapsRelativeIdsSymlinkTargets()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var validId = Guid.NewGuid();
            var relativeTarget = "../" + DatabaseStoreSymlinkFile.GetTargetPath(validId, '/');
            var symlinkPath = Path.Join(libraryRoot, "Relative.mkv");
            File.CreateSymbolicLink(symlinkPath, relativeTarget);
            var configManager = CreateConfigManager(libraryRoot);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
            Assert.Equal(symlinkPath, link.LinkPath);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLink_CachesMatchedLinkInsteadOfLastScannedLink()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            ClearCache();
            var targetId = Guid.NewGuid();
            var unrelatedId = Guid.NewGuid();
            var targetPath = Path.Join(libraryRoot, "000-target.strm");
            var unrelatedPath = Path.Join(libraryRoot, "999-unrelated.strm");
            File.WriteAllText(
                targetPath,
                $"http://localhost:3000/view/.ids/{targetId}.mkv?downloadKey=test&extension=mkv");
            File.WriteAllText(
                unrelatedPath,
                $"http://localhost:3000/view/.ids/{unrelatedId}.mkv?downloadKey=test&extension=mkv");
            var configManager = CreateConfigManager(libraryRoot);
            var davItem = new DavItem { Id = targetId };

            var link = OrganizedLinksUtil.GetLink(davItem, configManager);

            Assert.Equal(targetPath, link);
            Assert.True(GetCache().TryGetValue(targetId, out var cachedPath));
            Assert.Equal(targetPath, cachedPath);
        }
        finally
        {
            ClearCache();
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLink_IgnoresCachedLinkOutsideCurrentLibraryRoot()
    {
        var oldLibraryRoot = CreateTempDirectory();
        var currentLibraryRoot = CreateTempDirectory();
        try
        {
            ClearCache();
            var targetId = Guid.NewGuid();
            var stalePath = Path.Join(oldLibraryRoot, "Movie.strm");
            File.WriteAllText(
                stalePath,
                $"http://localhost:3000/view/.ids/{targetId}.mkv?downloadKey=test&extension=mkv");
            GetCache()[targetId] = stalePath;
            var configManager = CreateConfigManager(currentLibraryRoot);
            var davItem = new DavItem { Id = targetId };

            var link = OrganizedLinksUtil.GetLink(davItem, configManager);

            Assert.Null(link);
        }
        finally
        {
            ClearCache();
            Directory.Delete(oldLibraryRoot, recursive: true);
            Directory.Delete(currentLibraryRoot, recursive: true);
        }
    }

    private static ConfigManager CreateConfigManager(string libraryRoot)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "media.library-dir", ConfigValue = libraryRoot },
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = "/mnt/nzbdav" }
        ]);
        return configManager;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), "nzbdav-organized-links", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ConcurrentDictionary<Guid, string> GetCache()
    {
        var field = typeof(OrganizedLinksUtil).GetField(
            "Cache",
            BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsType<ConcurrentDictionary<Guid, string>>(field?.GetValue(null));
    }

    private static void ClearCache()
    {
        GetCache().Clear();
    }
}

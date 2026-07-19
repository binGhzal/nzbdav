using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using System.Collections.Concurrent;
using System.Reflection;

namespace backend.Tests.Utils;

public sealed class OrganizedLinksUtilTests
{
    private const string MountDir = "/mnt/nzbdav";

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
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Valid.mkv"),
                DatabaseStoreSymlinkFile.GetTargetPath(validId, MountDir));
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
                DatabaseStoreSymlinkFile.GetTargetPath(validId, MountDir));
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
        var testRoot = CreateTempDirectory();
        try
        {
            var mountDir = Path.Join(testRoot, "mount");
            var libraryRoot = Path.Join(mountDir, "library");
            Directory.CreateDirectory(libraryRoot);
            var validId = Guid.NewGuid();
            var symlinkPath = Path.Join(libraryRoot, "Relative.mkv");
            var absoluteTarget = DatabaseStoreSymlinkFile.GetTargetPath(validId, mountDir);
            var relativeTarget = Path.GetRelativePath(Path.GetDirectoryName(symlinkPath)!, absoluteTarget);
            File.CreateSymbolicLink(symlinkPath, relativeTarget);
            var configManager = CreateConfigManager(libraryRoot, mountDir);

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(configManager).ToList();

            var link = Assert.Single(links);
            Assert.Equal(validId, link.DavItemId);
            Assert.Equal(symlinkPath, link.LinkPath);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_RejectsIdsDirectoryPrefixConfusion()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var id = Guid.NewGuid();
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Prefix-confusion.mkv"),
                Path.Join(MountDir, ".ids-evil", id.ToString("D")));

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(CreateConfigManager(libraryRoot));

            Assert.Empty(links);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_RejectsTraversalOutsideIdsDirectory()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var id = Guid.NewGuid();
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Traversal.mkv"),
                Path.Join(MountDir, ".ids", "..", "decoy", id.ToString("D")));

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(CreateConfigManager(libraryRoot));

            Assert.Empty(links);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_RejectsWrongIdsShardForGuid()
    {
        var libraryRoot = CreateTempDirectory();
        try
        {
            var id = Guid.NewGuid();
            File.CreateSymbolicLink(
                Path.Join(libraryRoot, "Wrong-shard.mkv"),
                Path.Join(MountDir, ".ids", "w", "r", "o", "n", "g", id.ToString("D")));

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(CreateConfigManager(libraryRoot));

            Assert.Empty(links);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetLibraryDavItemLinks_RejectsRelativeIdsDecoyOutsideMount()
    {
        var testRoot = CreateTempDirectory();
        try
        {
            var mountDir = Path.Join(testRoot, "mount");
            var libraryRoot = Path.Join(testRoot, "library");
            Directory.CreateDirectory(mountDir);
            Directory.CreateDirectory(libraryRoot);
            var id = Guid.NewGuid();
            var symlinkPath = Path.Join(libraryRoot, "Relative-decoy.mkv");
            File.CreateSymbolicLink(
                symlinkPath,
                Path.Join("..", ".ids", "decoy", id.ToString("D")));

            var links = OrganizedLinksUtil.GetLibraryDavItemLinks(
                CreateConfigManager(libraryRoot, mountDir));

            Assert.Empty(links);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
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
            var targetPath = Path.Join(libraryRoot, "000-target.mkv");
            var unrelatedPath = Path.Join(libraryRoot, "999-unrelated.mkv");
            File.CreateSymbolicLink(targetPath, DatabaseStoreSymlinkFile.GetTargetPath(targetId, MountDir));
            File.CreateSymbolicLink(
                unrelatedPath,
                DatabaseStoreSymlinkFile.GetTargetPath(unrelatedId, MountDir));
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
            var stalePath = Path.Join(oldLibraryRoot, "Movie.mkv");
            File.CreateSymbolicLink(stalePath, DatabaseStoreSymlinkFile.GetTargetPath(targetId, MountDir));
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

    private static ConfigManager CreateConfigManager(string libraryRoot, string mountDir = MountDir)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "media.library-dir", ConfigValue = libraryRoot },
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = mountDir }
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

using NzbWebDAV.Utils;

namespace backend.Tests.Utils;

public sealed class SymlinkUtilTests
{
    [Fact]
    public void GetAllSymlinks_DoesNotRecurseIntoDirectorySymlinks()
    {
        var parent = Path.Join(Path.GetTempPath(), "nzbdav-symlink-scan", Guid.NewGuid().ToString("N"));
        var root = Path.Join(parent, "library");
        var target = Path.Join(parent, "external-target");
        var directoryLink = Path.Join(root, "link-to-target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.CreateSymbolicLink(Path.Join(target, "Nested.mkv"), "/mnt/nzbdav/.ids/nested.mkv");
        Directory.CreateSymbolicLink(directoryLink, target);

        try
        {
            var links = SymlinkUtil.GetAllSymlinks(root).ToList();

            var link = Assert.Single(links);
            Assert.Equal(directoryLink, link.SymlinkPath);
            Assert.Equal(target, link.TargetPath);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void GetSymlinkInfo_SkipsMissingFile()
    {
        var path = Path.Join(Path.GetTempPath(), "nzbdav-missing-symlink", $"{Guid.NewGuid():N}.mkv");

        var info = SymlinkUtil.GetSymlinkInfo(new FileInfo(path));

        Assert.Null(info);
    }

    [Fact]
    public void GetSymlinkInfo_ReturnsPathAndTarget()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-symlink-info", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Join(root, "Movie.mkv");
        const string target = "/mnt/nzbdav/.ids/movie.mkv";
        File.CreateSymbolicLink(path, target);

        try
        {
            var info = Assert.IsType<SymlinkUtil.SymlinkInfo>(
                SymlinkUtil.GetSymlinkInfo(new FileInfo(path)));
            Assert.Equal(path, info.SymlinkPath);
            Assert.Equal(target, info.TargetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

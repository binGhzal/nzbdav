using NzbWebDAV.Utils;

namespace backend.Tests.Utils;

public sealed class SymlinkAndStrmUtilTests
{
    [Fact]
    public void GetAllSymlinksAndStrms_DoesNotRecurseIntoDirectorySymlinks()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-link-scan", Guid.NewGuid().ToString("N"));
        var target = Path.Join(root, "target");
        var link = Path.Join(root, "link-to-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Join(target, "Nested.strm"), "http://example/nested");
        Directory.CreateSymbolicLink(link, target);

        try
        {
            var links = SymlinkAndStrmUtil.GetAllSymlinksAndStrms(root).ToList();

            Assert.Contains(links, x => x is SymlinkAndStrmUtil.SymlinkInfo info && info.SymlinkPath == link);
            Assert.DoesNotContain(
                links,
                x => x is SymlinkAndStrmUtil.StrmInfo info
                     && info.StrmPath == Path.Join(link, "Nested.strm"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetSymlinkOrStrmInfo_SkipsMissingStrmFile()
    {
        var path = Path.Join(Path.GetTempPath(), "nzbdav-missing-strm", $"{Guid.NewGuid():N}.strm");
        var fileInfo = new FileInfo(path);

        var info = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);

        Assert.Null(info);
    }

    [Fact]
    public void GetSymlinkOrStrmInfo_ReadsOnlyFirstStrmLine()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-strm-first-line", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Join(root, "Movie.strm");
        var target = "http://localhost:3000/view/.ids/11111111-1111-1111-1111-111111111111.mkv?downloadKey=test&extension=mkv";
        File.WriteAllText(
            path,
            string.Join(Environment.NewLine, [
                target,
                new string('x', 1024)
            ]));

        try
        {
            var info = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(path));
            var strmInfo = Assert.IsType<SymlinkAndStrmUtil.StrmInfo>(info);
            Assert.Equal(target, strmInfo.TargetUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

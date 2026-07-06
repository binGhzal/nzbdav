using NzbWebDAV.Utils;

namespace backend.Tests.Utils;

public sealed class FilePermissionUtilTests
{
    [Fact]
    public async Task WriteAllTextAsync_CreatesMissingParentDirectory()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-file-write", Guid.NewGuid().ToString("N"));
        var path = Path.Join(root, "nested", "Movie.strm");
        try
        {
            await FilePermissionUtil.WriteAllTextAsync(path, "http://localhost/view/test");

            Assert.Equal("http://localhost/view/test", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAllTextAsync_DoesNotRewriteUnchangedFile()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-file-write", Guid.NewGuid().ToString("N"));
        var path = Path.Join(root, "Movie.strm");
        var timestamp = new DateTime(2024, 01, 01, 00, 00, 00, DateTimeKind.Utc);
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(path, "http://localhost/view/test");
            File.SetLastWriteTimeUtc(path, timestamp);

            await FilePermissionUtil.WriteAllTextAsync(path, "http://localhost/view/test");

            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

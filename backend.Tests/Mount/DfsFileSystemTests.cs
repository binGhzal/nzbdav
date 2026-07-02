using System.Reflection;
using Mono.Unix.Native;
using NzbWebDAV.Mount;

namespace backend.Tests.Mount;

public sealed class DfsFileSystemTests
{
    [Fact]
    public void CreateStat_UsesEpochForUnsetCreatedAt()
    {
        var node = new DfsDavNode
        {
            Kind = DfsDavNodeKind.Directory,
            Name = "/",
            Path = "/",
            CreatedAt = default
        };

        var stat = CreateStat(node);

        Assert.Equal(0, stat.st_atime);
        Assert.Equal(0, stat.st_mtime);
        Assert.Equal(0, stat.st_ctime);
    }

    [Fact]
    public void CreateStat_TreatsUnspecifiedDatesAsUtc()
    {
        var node = new DfsDavNode
        {
            Kind = DfsDavNodeKind.File,
            Name = "file.mkv",
            Path = "/content/file.mkv",
            CreatedAt = new DateTime(2026, 07, 02, 20, 0, 0, DateTimeKind.Unspecified),
            Size = 1024
        };

        var stat = CreateStat(node);

        Assert.Equal(new DateTimeOffset(2026, 07, 02, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), stat.st_mtime);
        Assert.Equal(1024, stat.st_size);
    }

    private static Stat CreateStat(DfsDavNode node)
    {
        var method = typeof(DfsFileSystem).GetMethod("CreateStat", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<Stat>(method.Invoke(null, [node]));
    }
}

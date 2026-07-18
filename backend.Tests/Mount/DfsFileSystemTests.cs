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
    public void ToUnixTimeSeconds_TreatsUnspecifiedDatesAsDeploymentLocalWallTime()
    {
        var localZone = TimeZoneInfo.CreateCustomTimeZone(
            "legacy-local-plus-four",
            TimeSpan.FromHours(4),
            "legacy-local-plus-four",
            "legacy-local-plus-four");
        var localWallTime = new DateTime(2026, 07, 02, 20, 0, 0, DateTimeKind.Unspecified);
        var method = typeof(DfsFileSystem).GetMethod(
            "ToUnixTimeSeconds",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(DateTime), typeof(TimeZoneInfo)],
            modifiers: null);

        Assert.NotNull(method);
        var result = Assert.IsType<long>(method.Invoke(null, [localWallTime, localZone]));
        Assert.Equal(new DateTimeOffset(2026, 07, 02, 16, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), result);
    }

    private static Stat CreateStat(DfsDavNode node)
    {
        var method = typeof(DfsFileSystem).GetMethod("CreateStat", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<Stat>(method.Invoke(null, [node]));
    }
}

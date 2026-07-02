using System.Runtime.InteropServices;
using NzbWebDAV.Mount;

namespace backend.Tests.Mount;

public sealed class DfsMountServiceTests
{
    [Fact]
    public void GetNativeFuseUnsupportedReason_AllowsLinuxX64()
    {
        var reason = DfsMountService.GetNativeFuseUnsupportedReason(isLinux: true, Architecture.X64);

        Assert.Null(reason);
    }

    [Fact]
    public void GetNativeFuseUnsupportedReason_RejectsNonLinux()
    {
        var reason = DfsMountService.GetNativeFuseUnsupportedReason(isLinux: false, Architecture.X64);

        Assert.Equal("DFS mount requires Linux FUSE", reason);
    }

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X86)]
    public void GetNativeFuseUnsupportedReason_RejectsLinuxArchitecturesWithoutNativeHelper(Architecture architecture)
    {
        var reason = DfsMountService.GetNativeFuseUnsupportedReason(isLinux: true, architecture);

        Assert.Contains("linux-x64", reason);
    }
}

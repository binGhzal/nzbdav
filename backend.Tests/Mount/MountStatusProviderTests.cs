using NzbWebDAV.Mount;

namespace backend.Tests.Mount;

public sealed class MountStatusProviderTests
{
    [Fact]
    public void DefaultRcloneMount_IsExplicitlyUnverified()
    {
        var snapshot = new MountStatusProvider().GetSnapshot();

        Assert.Equal("rclone", snapshot.Type);
        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.Ready);
        Assert.Equal("external-unverified", snapshot.State);
        Assert.Contains("not been verified", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalMount_IsExplicitlyUnverified()
    {
        var provider = new MountStatusProvider();

        provider.SetExternal("rclone", "/mnt/media");
        var snapshot = provider.GetSnapshot();

        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.Ready);
        Assert.Equal("external-unverified", snapshot.State);
        Assert.Contains("not been verified", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledMount_RemainsDistinctFromUnverifiedExternalMount()
    {
        var provider = new MountStatusProvider();

        provider.SetDisabled("none", "/mnt/media", "mount disabled");
        var snapshot = provider.GetSnapshot();

        Assert.False(snapshot.Enabled);
        Assert.True(snapshot.Ready);
        Assert.Equal("disabled", snapshot.State);
    }
}

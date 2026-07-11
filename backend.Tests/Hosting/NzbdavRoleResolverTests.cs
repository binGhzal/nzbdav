using NzbWebDAV.Hosting;

namespace backend.Tests.Hosting;

public sealed class NzbdavRoleResolverTests
{
    [Theory]
    [InlineData(null, NzbdavRole.All)]
    [InlineData("all", NzbdavRole.All)]
    [InlineData("control", NzbdavRole.Control)]
    [InlineData("gateway", NzbdavRole.Gateway)]
    [InlineData("worker-download", NzbdavRole.WorkerDownload)]
    [InlineData("worker-verify", NzbdavRole.WorkerVerify)]
    [InlineData("worker-repair", NzbdavRole.WorkerRepair)]
    [InlineData("ui", NzbdavRole.Ui)]
    public void ResolveMapsSupportedValues(string? value, NzbdavRole expected)
    {
        Assert.Equal(expected, NzbdavRoleResolver.Resolve(value));
    }

    [Fact]
    public void ResolveRejectsUnknownValues()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            NzbdavRoleResolver.Resolve("worker-everything"));
        Assert.Contains("Unsupported NZBDAV_ROLE", error.Message);
    }

    [Fact]
    public void GatewayOwnsProviderPoolAndWebDavButNotDatabase()
    {
        var capabilities = NzbdavRoleCapabilities.For(NzbdavRole.Gateway);
        Assert.Contains(NzbdavCapability.ProviderPool, capabilities);
        Assert.Contains(NzbdavCapability.WebDav, capabilities);
        Assert.DoesNotContain(NzbdavCapability.Database, capabilities);
    }
}

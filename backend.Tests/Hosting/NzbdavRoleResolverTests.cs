using NzbWebDAV.Hosting;

namespace backend.Tests.Hosting;

public sealed class NzbdavRoleResolverTests
{
    private static readonly IReadOnlyDictionary<NzbdavRole, NzbdavCapability[]> ExpectedSeparatedRoleCapabilities =
        new Dictionary<NzbdavRole, NzbdavCapability[]>
        {
            [NzbdavRole.Control] =
            [
                NzbdavCapability.Database,
                NzbdavCapability.AdminApi,
                NzbdavCapability.SabApi,
                NzbdavCapability.ArrBackground,
                NzbdavCapability.Maintenance,
                NzbdavCapability.InternalRpc,
            ],
            [NzbdavRole.Gateway] =
            [
                NzbdavCapability.WebDav,
                NzbdavCapability.ProviderPool,
                NzbdavCapability.SparseCache,
                NzbdavCapability.InternalRpc,
            ],
            [NzbdavRole.WorkerDownload] =
            [
                NzbdavCapability.DownloadWorker,
                NzbdavCapability.InternalRpc,
            ],
            [NzbdavRole.WorkerVerify] =
            [
                NzbdavCapability.VerifyWorker,
                NzbdavCapability.InternalRpc,
            ],
            [NzbdavRole.WorkerRepair] =
            [
                NzbdavCapability.RepairWorker,
                NzbdavCapability.InternalRpc,
            ],
            [NzbdavRole.Ui] =
            [
                NzbdavCapability.UiFrontend,
            ],
        };

    public static IEnumerable<object[]> ExactSeparatedRoleCapabilities =>
        ExpectedSeparatedRoleCapabilities.Select(entry => new object[] { entry.Key, entry.Value });

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

    [Theory]
    [MemberData(nameof(ExactSeparatedRoleCapabilities))]
    public void SeparatedRoleOwnsItsExactCapabilitySet(
        NzbdavRole role,
        NzbdavCapability[] expectedCapabilities)
    {
        var actualCapabilities = NzbdavRoleCapabilities.For(role);

        Assert.Equal(
            expectedCapabilities.OrderBy(capability => capability),
            actualCapabilities.OrderBy(capability => capability));
    }

    [Fact]
    public void AllOwnsTheUnionOfExactSeparatedRoleCapabilities()
    {
        var expectedCapabilities = ExpectedSeparatedRoleCapabilities.Values
            .SelectMany(capabilities => capabilities)
            .ToHashSet();
        var actualCapabilities = NzbdavRoleCapabilities.For(NzbdavRole.All);

        Assert.Equal(
            expectedCapabilities.OrderBy(capability => capability),
            actualCapabilities.OrderBy(capability => capability));
    }
}

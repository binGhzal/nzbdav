using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public sealed class UsenetProviderConfigTests
{
    [Theory]
    [InlineData(119, false, false, false)]
    [InlineData(119, true, true, false)]
    [InlineData(563, false, true, true)]
    [InlineData(443, false, true, true)]
    [InlineData(563, true, true, false)]
    public void ConnectionDetails_UsesImplicitTlsForSslPorts
    (
        int port,
        bool configuredUseSsl,
        bool expectedEffectiveUseSsl,
        bool expectedImplicitTls
    )
    {
        var provider = CreateProvider(port, configuredUseSsl);

        Assert.Equal(expectedEffectiveUseSsl, provider.GetEffectiveUseSsl());
        Assert.Equal(expectedImplicitTls, provider.IsImplicitTlsEnabled());
    }

    private static UsenetProviderConfig.ConnectionDetails CreateProvider(int port, bool useSsl)
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Type = ProviderType.Pooled,
            Host = "news.example.invalid",
            Port = port,
            UseSsl = useSsl,
            User = "user",
            Pass = "pass",
            MaxConnections = 1
        };
    }
}

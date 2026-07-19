using Microsoft.Extensions.Configuration;
using NzbWebDAV.Hosting;

namespace backend.Tests.Hosting;

public sealed class BackendListenPolicyTests
{
    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("http://127.0.0.1:1", 1)]
    [InlineData("http://127.0.0.1:8080", 8080)]
    [InlineData("http://127.0.0.1:65535", 65535)]
    internal void ResolvePortAcceptsOnlyTheExactLoopbackContract(string? configured, int expected)
    {
        Assert.Equal(expected, BackendListenPolicy.ResolvePort(configured, EmptyConfiguration()));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://[::]:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:65536")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://127.0.0.1:8080/path")]
    [InlineData("http://127.0.0.1:8080?query")]
    [InlineData("http://127.0.0.1:8080#fragment")]
    [InlineData("http://user@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080;http://0.0.0.0:8081")]
    [InlineData(" http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080 ")]
    internal void ResolvePortRejectsEveryNoncanonicalOrNonLoopbackAddress(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BackendListenPolicy.ResolvePort(configured, EmptyConfiguration()));

        Assert.Equal(BackendListenPolicy.InvalidListenUrlMessage, error.Message);
    }

    [Fact]
    internal void ResolvePortRejectsConfiguredKestrelEndpointsBeforeBinding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Public:Url"] = "http://0.0.0.0:8080",
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            BackendListenPolicy.ResolvePort("http://127.0.0.1:8080", configuration));

        Assert.Equal(BackendListenPolicy.ConfiguredEndpointsUnavailableMessage, error.Message);
    }

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().Build();
}

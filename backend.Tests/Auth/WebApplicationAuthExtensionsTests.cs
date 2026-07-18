using NzbWebDAV.Auth;

namespace backend.Tests.Auth;

[Collection(nameof(WebdavAuthEnvironmentCollection))]
public sealed class WebApplicationAuthExtensionsTests
{
    private const string DisableWebdavAuth = "DISABLE_WEBDAV_AUTH";

    [Theory]
    [InlineData("y")]
    [InlineData("YES")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void EnsureWebdavAuthenticationRequired_RejectsLegacyBypass(string value)
    {
        WithEnvironment(value, () =>
        {
            var exception = Assert.Throws<InvalidOperationException>(
                WebApplicationAuthExtensions.EnsureWebdavAuthenticationRequired);

            Assert.Contains(DisableWebdavAuth, exception.Message, StringComparison.Ordinal);
            Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("V1", exception.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("no")]
    public void EnsureWebdavAuthenticationRequired_AllowsAuthenticatedContract(string? value)
    {
        WithEnvironment(value, WebApplicationAuthExtensions.EnsureWebdavAuthenticationRequired);
    }

    private static void WithEnvironment(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(DisableWebdavAuth);
        try
        {
            Environment.SetEnvironmentVariable(DisableWebdavAuth, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DisableWebdavAuth, previous);
        }
    }
}

[CollectionDefinition(nameof(WebdavAuthEnvironmentCollection), DisableParallelization = true)]
public sealed class WebdavAuthEnvironmentCollection;

using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace backend.Tests.Config;

public sealed class ConfigSecretRedactorTests
{
    [Fact]
    public void RedactValue_RedactsStrmMasterKey()
    {
        Assert.Equal(
            ConfigSecretRedactor.RedactedSecret,
            ConfigSecretRedactor.RedactValue("api.strm-key", "strm-secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(ConfigSecretRedactor.RedactedSecret)]
    public void PreserveExistingSecretValue_UnresolvedScalarMarkerIsRejected(string? existingValue)
    {
        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue(
                "rclone.pass",
                ConfigSecretRedactor.RedactedSecret,
                existingValue));
    }

    [Fact]
    public void PreserveExistingSecretValue_UsenetReorderUsesExactEndpointIdentity()
    {
        var existing = SerializeProviders(
            Provider("news-a.example", 563, "alice", "secret-a"),
            Provider("news-b.example", 563, "bob", "secret-b"));
        var submitted = SerializeProviders(
            Provider("news-b.example", 563, "bob", ConfigSecretRedactor.RedactedSecret),
            Provider("news-a.example", 563, "alice", ConfigSecretRedactor.RedactedSecret));

        var preserved = DeserializeProviders(ConfigSecretRedactor.PreserveExistingSecretValue(
            "usenet.providers",
            submitted,
            existing));

        Assert.Equal("secret-b", preserved.Providers[0].Pass);
        Assert.Equal("secret-a", preserved.Providers[1].Pass);
    }

    [Fact]
    public void PreserveExistingSecretValue_UsenetEndpointChangeRejectsUnresolvedMarker()
    {
        var existing = SerializeProviders(Provider("news-a.example", 563, "alice", "secret-a"));
        var submitted = SerializeProviders(
            Provider("news-typo.example", 563, "alice", ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("usenet.providers", submitted, existing));
    }

    [Fact]
    public void PreserveExistingSecretValue_UsenetTlsDowngradeRejectsUnresolvedMarker()
    {
        var existing = SerializeProviders(Provider("news.example", 119, "alice", "secret", useSsl: true));
        var submitted = SerializeProviders(
            Provider("news.example", 119, "alice", ConfigSecretRedactor.RedactedSecret, useSsl: false));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("usenet.providers", submitted, existing));
    }

    [Fact]
    public void PreserveExistingSecretValue_UsenetUsernameWhitespaceChangeRejectsUnresolvedMarker()
    {
        var existing = SerializeProviders(Provider("news.example", 563, "alice", "secret"));
        var submitted = SerializeProviders(
            Provider("news.example", 563, " alice ", ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("usenet.providers", submitted, existing));
    }

    [Fact]
    public void PreserveExistingSecretValue_DuplicateUsenetIdentityRejectsAmbiguousMarker()
    {
        var existing = SerializeProviders(
            Provider("news.example", 563, "alice", "secret-a"),
            Provider("NEWS.EXAMPLE", 563, "alice", "secret-b"));
        var submitted = SerializeProviders(
            Provider("news.example", 563, "alice", ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("usenet.providers", submitted, existing));
    }

    [Fact]
    public void PreserveExistingSecretValue_ArrReorderUsesExactEndpointIdentity()
    {
        var existing = SerializeArr(
            Arr("http://radarr-a:7878", "secret-a"),
            Arr("http://radarr-b:7878", "secret-b"));
        var submitted = SerializeArr(
            Arr("http://radarr-b:7878/", ConfigSecretRedactor.RedactedSecret),
            Arr("http://radarr-a:7878/", ConfigSecretRedactor.RedactedSecret));

        var preserved = DeserializeArr(ConfigSecretRedactor.PreserveExistingSecretValue(
            "arr.instances",
            submitted,
            existing));

        Assert.Equal("secret-b", preserved.RadarrInstances[0].ApiKey);
        Assert.Equal("secret-a", preserved.RadarrInstances[1].ApiKey);
    }

    [Fact]
    public void PreserveExistingSecretValue_ArrEndpointChangeRejectsUnresolvedMarker()
    {
        var existing = SerializeArr(Arr("http://radarr:7878", "secret-a"));
        var submitted = SerializeArr(
            Arr("http://radarr-typo:7878", ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("arr.instances", submitted, existing));
    }

    [Theory]
    [InlineData("http://radarr:7878/Radarr", "http://radarr:7878/radarr")]
    [InlineData("http://radarr:7878/api?Token=Value", "http://radarr:7878/api?Token=value")]
    [InlineData("http://radarr:7878/Radarr", "http://radarr:7878/Radarr/")]
    public void PreserveExistingSecretValue_ArrPathOrQueryChangeRejectsUnresolvedMarker(
        string existingHost,
        string submittedHost)
    {
        var existing = SerializeArr(Arr(existingHost, "secret"));
        var submitted = SerializeArr(Arr(submittedHost, ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("arr.instances", submitted, existing));
    }

    [Fact]
    public void PreserveExistingSecretValue_ArrCanonicalDefaultPortAndRootSlashMatch()
    {
        var existing = SerializeArr(Arr("http://RADARR", "secret"));
        var submitted = SerializeArr(Arr("http://radarr:80/", ConfigSecretRedactor.RedactedSecret));

        var preserved = DeserializeArr(ConfigSecretRedactor.PreserveExistingSecretValue(
            "arr.instances",
            submitted,
            existing));

        Assert.Equal("secret", Assert.Single(preserved.RadarrInstances).ApiKey);
        Assert.Equal("http://RADARR", Assert.Single(preserved.RadarrInstances).Host);
    }

    [Fact]
    public void PreserveExistingSecretValue_DuplicateArrIdentityRejectsAmbiguousMarker()
    {
        var existing = SerializeArr(
            Arr("http://radarr:7878", "secret-a"),
            Arr("HTTP://RADARR:7878/", "secret-b"));
        var submitted = SerializeArr(Arr("http://radarr:7878", ConfigSecretRedactor.RedactedSecret));

        Assert.Throws<ConfigSecretResolutionException>(() =>
            ConfigSecretRedactor.PreserveExistingSecretValue("arr.instances", submitted, existing));
    }

    private static UsenetProviderConfig.ConnectionDetails Provider(
        string host,
        int port,
        string user,
        string pass,
        bool useSsl = true) => new()
    {
        Type = ProviderType.Pooled,
        Host = host,
        Port = port,
        UseSsl = useSsl,
        User = user,
        Pass = pass,
        MaxConnections = 10
    };

    private static ArrConfig.ConnectionDetails Arr(string host, string apiKey) => new()
    {
        Host = host,
        ApiKey = apiKey
    };

    private static string SerializeProviders(params UsenetProviderConfig.ConnectionDetails[] providers) =>
        JsonSerializer.Serialize(new UsenetProviderConfig { Providers = [.. providers] });

    private static UsenetProviderConfig DeserializeProviders(string value) =>
        JsonSerializer.Deserialize<UsenetProviderConfig>(value)
        ?? throw new InvalidOperationException("Expected a valid Usenet provider config.");

    private static string SerializeArr(params ArrConfig.ConnectionDetails[] instances) =>
        JsonSerializer.Serialize(new ArrConfig { RadarrInstances = [.. instances] });

    private static ArrConfig DeserializeArr(string value) =>
        JsonSerializer.Deserialize<ArrConfig>(value)
        ?? throw new InvalidOperationException("Expected a valid ARR config.");
}

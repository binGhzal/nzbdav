using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestArrConnection;
using NzbWebDAV.Api.Controllers.TestRcloneConnection;
using NzbWebDAV.Api.Controllers.TestUsenetConnection;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace backend.Tests.Api;

public sealed class ConnectionTestSecretResolutionTests
{
    [Fact]
    public void ArrRedactionMarkerResolvesOnlyForExactApplicationAndHost()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = JsonSerializer.Serialize(new ArrConfig
                {
                    RadarrInstances =
                    [
                        new ArrConfig.ConnectionDetails
                        {
                            Host = "http://radarr:7878",
                            ApiKey = "arr-secret"
                        }
                    ]
                })
            }
        ]);
        var controller = new TestArrConnectionController(configManager);

        Assert.Equal("arr-secret", controller.ResolveApiKey(CreateArrRequest("radarr", "http://radarr:7878/")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolveApiKey(CreateArrRequest("sonarr", "http://radarr:7878/")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolveApiKey(CreateArrRequest("radarr", "http://radarr-typo:7878")));
    }

    [Fact]
    public void ArrRedactionMarkerRejectsPathCaseMismatchDuplicateIdentityAndMissingTypeOrConfig()
    {
        var configManager = CreateArrConfigManager(
            new ArrConfig.ConnectionDetails { Host = "http://radarr:7878/Radarr", ApiKey = "secret-a" },
            new ArrConfig.ConnectionDetails { Host = "http://duplicate:7878", ApiKey = "secret-b" },
            new ArrConfig.ConnectionDetails { Host = "HTTP://DUPLICATE:7878/", ApiKey = "secret-c" });
        var controller = new TestArrConnectionController(configManager);

        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolveApiKey(CreateArrRequest("radarr", "http://radarr:7878/radarr")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolveApiKey(CreateArrRequest("radarr", "http://duplicate:7878")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolveApiKey(CreateArrRequest(null, "http://radarr:7878/Radarr")));
        Assert.Throws<BadHttpRequestException>(() =>
            new TestArrConnectionController().ResolveApiKey(
                CreateArrRequest("radarr", "http://radarr:7878/Radarr")));
    }

    [Fact]
    public void ArrRedactionMarkerMatchesCanonicalDefaultPortAndTerminalRootSlash()
    {
        var controller = new TestArrConnectionController(CreateArrConfigManager(
            new ArrConfig.ConnectionDetails { Host = "http://RADARR", ApiKey = "arr-secret" }));

        Assert.Equal(
            "arr-secret",
            controller.ResolveApiKey(CreateArrRequest("radarr", "http://radarr:80/")));
    }

    [Fact]
    public void UsenetRedactionMarkerResolvesOnlyForExactEndpointIdentity()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            Type = ProviderType.Pooled,
                            Host = "news.example",
                            Port = 563,
                            UseSsl = true,
                            User = "alice",
                            Pass = "usenet-secret",
                            MaxConnections = 10
                        }
                    ]
                })
            }
        ]);

        Assert.Equal(
            "usenet-secret",
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("news.example", "alice"),
                configManager));
        Assert.Throws<BadHttpRequestException>(() =>
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("news-typo.example", "alice"),
                configManager));
    }

    [Fact]
    public void UsenetRedactionMarkerRejectsTlsDowngradeUsernameWhitespaceAndDuplicateIdentity()
    {
        var configManager = CreateUsenetConfigManager(
            Provider("news.example", 119, true, "alice", "secret-a"),
            Provider("duplicate.example", 563, true, "bob", "secret-b"),
            Provider("DUPLICATE.EXAMPLE", 563, false, "bob", "secret-c"));

        Assert.Throws<BadHttpRequestException>(() =>
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("news.example", "alice", port: 119, useSsl: false),
                configManager));
        Assert.Throws<BadHttpRequestException>(() =>
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("news.example", " alice ", port: 119, useSsl: true),
                configManager));
        Assert.Throws<BadHttpRequestException>(() =>
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("duplicate.example", "bob"),
                configManager));
        Assert.Throws<BadHttpRequestException>(() =>
            TestUsenetConnectionController.ResolvePassword(
                CreateUsenetRequest("news.example", "alice", port: 119, useSsl: true),
                configManager: null));
    }

    [Fact]
    public void RcloneRedactionMarkerResolvesOnlyForExactTopologyIdentity()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "alice" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "rclone-secret" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "nzbdav:" }
        ]);
        var controller = new TestRcloneConnectionController(configManager);

        Assert.Equal(
            "rclone-secret",
            controller.ResolvePassword(CreateRcloneRequest("http://rclone:5572/", "nzbdav:")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolvePassword(CreateRcloneRequest("http://rclone:5572/", "other:")));
    }

    [Fact]
    public void RcloneRedactionMarkerRejectsPathCaseMismatchMissingSelectorAndMissingConfig()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572/Rclone" },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "alice" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "rclone-secret" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "nzbdav:" }
        ]);
        var controller = new TestRcloneConnectionController(configManager);

        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolvePassword(CreateRcloneRequest("http://rclone:5572/rclone", "nzbdav:")));
        Assert.Throws<BadHttpRequestException>(() =>
            controller.ResolvePassword(CreateRcloneRequest("http://rclone:5572/Rclone", fs: null)));
        Assert.Throws<BadHttpRequestException>(() =>
            new TestRcloneConnectionController().ResolvePassword(
                CreateRcloneRequest("http://rclone:5572/Rclone", "nzbdav:")));
    }

    private static ConfigManager CreateArrConfigManager(params ArrConfig.ConnectionDetails[] instances)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = JsonSerializer.Serialize(new ArrConfig { RadarrInstances = [.. instances] })
            }
        ]);
        return configManager;
    }

    private static ConfigManager CreateUsenetConfigManager(
        params UsenetProviderConfig.ConnectionDetails[] providers)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig { Providers = [.. providers] })
            }
        ]);
        return configManager;
    }

    private static UsenetProviderConfig.ConnectionDetails Provider(
        string host,
        int port,
        bool useSsl,
        string user,
        string pass) => new()
    {
        Type = ProviderType.Pooled,
        Host = host,
        Port = port,
        UseSsl = useSsl,
        User = user,
        Pass = pass,
        MaxConnections = 10
    };

    private static TestArrConnectionRequest CreateArrRequest(string? type, string host)
    {
        var context = new DefaultHttpContext();
        var form = new Dictionary<string, StringValues>
        {
            ["host"] = host,
            ["apiKey"] = ConfigSecretRedactor.RedactedSecret
        };
        if (type is not null) form["type"] = type;
        context.Request.Form = new FormCollection(form);
        return new TestArrConnectionRequest(context);
    }

    private static TestUsenetConnectionRequest CreateUsenetRequest(
        string host,
        string user,
        int port = 563,
        bool useSsl = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = host,
            ["user"] = user,
            ["pass"] = ConfigSecretRedactor.RedactedSecret,
            ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["use-ssl"] = useSsl ? "true" : "false"
        });
        return new TestUsenetConnectionRequest(context);
    }

    private static TestRcloneConnectionRequest CreateRcloneRequest(string host, string? fs)
    {
        var context = new DefaultHttpContext();
        var form = new Dictionary<string, StringValues>
        {
            ["host"] = host,
            ["user"] = "alice",
            ["pass"] = ConfigSecretRedactor.RedactedSecret
        };
        if (fs is not null) form["fs"] = fs;
        context.Request.Form = new FormCollection(form);
        return new TestRcloneConnectionRequest(context);
    }
}

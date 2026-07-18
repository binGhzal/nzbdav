using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;
using backend.Tests.Services;
using NzbWebDAV.Models;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class UpdateConfigControllerTests
{
    private const string RedactedSecret = "__NZBDAV_REDACTED__";

    private readonly ContentIndexDatabaseFixture _fixture;

    public UpdateConfigControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForNoOpUpdate()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "rclone.host",
                ConfigValue = "http://rclone:5572"
            }
        ]);
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "http://rclone:5572"
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("http://rclone:5572", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.host")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_RaisesConfigChangedOnlyForChangedValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "usenet.max-download-connections", "32");

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "rclone.host",
                ConfigValue = "http://rclone:5572"
            },
            new ConfigItem
            {
                ConfigName = "usenet.max-download-connections",
                ConfigValue = "32"
            }
        ]);
        Dictionary<string, string>? changedConfig = null;
        configManager.OnConfigChanged += (_, args) => changedConfig = args.ChangedConfig;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "http://rclone:5572",
                ["usenet.max-download-connections"] = "64"
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.NotNull(changedConfig);
        Assert.Equal(["usenet.max-download-connections"], changedConfig.Keys);
        Assert.Equal("64", changedConfig["usenet.max-download-connections"]);
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForEquivalentBooleanValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.rc-enabled", "true");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.rc-enabled"] = "True"
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("true", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.rc-enabled")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForEquivalentJsonValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var compactProviderConfig = CreateProviderConfigJson(writeIndented: false);
        var prettyProviderConfig = CreateProviderConfigJson(writeIndented: true);
        await SetConfigAsync(dbContext, "usenet.providers", compactProviderConfig);

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "usenet.providers", ConfigValue = compactProviderConfig });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["usenet.providers"] = prettyProviderConfig
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal(compactProviderConfig, (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "usenet.providers")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForEquivalentRcloneHostValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "rclone.pass", "saved-secret");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "saved-secret" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = " http://rclone:5572/ "
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("http://rclone:5572", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.host")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotInsertUnsetOptionalRcloneValuesOrRaiseConfigChanged()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var requestedKeys = new[] { "rclone.host", "rclone.user", "rclone.pass", "rclone.mount-dir" };
        await dbContext.ConfigItems
            .Where(x => requestedKeys.Contains(x.ConfigName))
            .ExecuteDeleteAsync();
        var beforeRows = await GetConfigRowsAsync(dbContext, requestedKeys);
        var configManager = new ConfigManager();
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "",
                ["rclone.user"] = "",
                ["rclone.pass"] = "",
                ["rclone.mount-dir"] = ""
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal(beforeRows, await GetConfigRowsAsync(dbContext, requestedKeys));
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotInsertWhitespaceOnlyRcloneEndpointOrMountPath()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var requestedKeys = new[] { "rclone.host", "rclone.mount-dir" };
        await dbContext.ConfigItems
            .Where(x => requestedKeys.Contains(x.ConfigName))
            .ExecuteDeleteAsync();
        var beforeRows = await GetConfigRowsAsync(dbContext, requestedKeys);
        var configManager = new ConfigManager();
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "   ",
                ["rclone.mount-dir"] = " \t "
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal(beforeRows, await GetConfigRowsAsync(dbContext, requestedKeys));
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForEquivalentRcloneMountDirValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.mount-dir", "/mnt/nzbdav");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = "/mnt/nzbdav" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.mount-dir"] = "/mnt/nzbdav/"
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("/mnt/nzbdav", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.mount-dir")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_DoesNotRaiseConfigChangedForEquivalentMountTypeValues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "Mount:Type", "rclone");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "Mount:Type", ConfigValue = "rclone" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["Mount:Type"] = " RCLONE "
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("rclone", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "Mount:Type")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_PreservesRedactedRclonePasswordWithoutRaisingConfigChanged()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.pass", "rclone-secret");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "rclone-secret" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["rclone.pass"] = RedactedSecret
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("rclone-secret", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.pass")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_PreservesRedactedWebdavPasswordWithoutHashingOrRaisingConfigChanged()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "webdav.pass", "webdav-hash");

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "webdav.pass", ConfigValue = "webdav-hash" });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["webdav.pass"] = RedactedSecret
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        Assert.Equal(0, eventCount);
        Assert.Equal("webdav-hash", (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "webdav.pass")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_PreservesRedactedProviderPasswordWhenNonSecretFieldsChange()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var existingConfig = CreateProviderConfigJson("provider-secret", maxConnections: 50);
        var submittedConfig = CreateProviderConfigJson(RedactedSecret, maxConnections: 80);
        await SetConfigAsync(dbContext, "usenet.providers", existingConfig);

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "usenet.providers", ConfigValue = existingConfig });
        Dictionary<string, string>? changedConfig = null;
        configManager.OnConfigChanged += (_, args) => changedConfig = args.ChangedConfig;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["usenet.providers"] = submittedConfig
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        var storedConfig = (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "usenet.providers")).ConfigValue;
        Assert.Contains("provider-secret", storedConfig);
        Assert.DoesNotContain(RedactedSecret, storedConfig);
        Assert.Contains("80", storedConfig);
        Assert.NotNull(changedConfig);
        Assert.Contains("provider-secret", changedConfig["usenet.providers"]);
    }

    [Fact]
    public async Task HandleApiRequest_PreservesRedactedArrApiKeyWhenNonSecretFieldsChange()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var existingConfig = CreateArrConfigJson("arr-secret", host: "http://radarr:7878");
        var submittedConfig = CreateArrConfigJson(RedactedSecret, host: "http://radarr:7878/");
        await SetConfigAsync(dbContext, "arr.instances", existingConfig);

        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "arr.instances", ConfigValue = existingConfig });
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;

        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues>
            {
                ["arr.instances"] = submittedConfig
            });

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<UpdateConfigResponse>(ok.Value).Status);
        var storedConfig = (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "arr.instances")).ConfigValue;
        Assert.Contains("arr-secret", storedConfig);
        Assert.DoesNotContain(RedactedSecret, storedConfig);
        Assert.Equal(0, eventCount);
    }

    [Theory]
    [InlineData("rclone.host", "http://other-rclone:5572")]
    [InlineData("rclone.user", "bob")]
    [InlineData("rclone.fs", "other:")]
    public async Task HandleApiRequest_RejectsRcloneTopologyChangeWhenSavedPasswordWouldBeRetained(
        string changedConfigName,
        string changedConfigValue)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "rclone.user", "alice");
        await SetConfigAsync(dbContext, "rclone.fs", "nzbdav:");
        await SetConfigAsync(dbContext, "rclone.pass", "saved-secret");
        var configManager = CreateLoadedConfigManager(
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
            new ConfigItem { ConfigName = "rclone.user", ConfigValue = "alice" },
            new ConfigItem { ConfigName = "rclone.fs", ConfigValue = "nzbdav:" },
            new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "saved-secret" });
        var controller = CreateController(
            dbContext,
            configManager,
            new Dictionary<string, StringValues> { [changedConfigName] = changedConfigValue });

        var result = await HandleWithApiKeyAsync(controller);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "saved-secret",
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.pass")).ConfigValue);
        Assert.Equal(
            changedConfigName switch
            {
                "rclone.host" => "http://rclone:5572",
                "rclone.user" => "alice",
                "rclone.fs" => "nzbdav:",
                _ => throw new InvalidOperationException()
            },
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == changedConfigName)).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_RejectsRcloneTopologyChangeWithRedactedPassword()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "rclone.pass", "saved-secret");
        var controller = CreateController(
            dbContext,
            CreateLoadedConfigManager(
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
                new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "saved-secret" }),
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "http://other-rclone:5572",
                ["rclone.pass"] = RedactedSecret
            });

        var result = await HandleWithApiKeyAsync(controller);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "http://rclone:5572",
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.host")).ConfigValue);
        Assert.Equal(
            "saved-secret",
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.pass")).ConfigValue);
    }

    [Theory]
    [InlineData("replacement-secret")]
    [InlineData("")]
    public async Task HandleApiRequest_AllowsRcloneTopologyChangeWithExplicitPasswordReplacementOrClear(
        string submittedPassword)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "rclone.pass", "saved-secret");
        var controller = CreateController(
            dbContext,
            CreateLoadedConfigManager(
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" },
                new ConfigItem { ConfigName = "rclone.pass", ConfigValue = "saved-secret" }),
            new Dictionary<string, StringValues>
            {
                ["rclone.host"] = "http://other-rclone:5572",
                ["rclone.pass"] = submittedPassword
            });

        var result = await HandleWithApiKeyAsync(controller);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(
            "http://other-rclone:5572",
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.host")).ConfigValue);
        Assert.Equal(
            submittedPassword,
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.pass")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_UnresolvedRedactedUsenetMarkerReturnsBadRequestWithoutMutation()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var existingConfig = SerializeProviderConfig(
            "news.example.invalid", 119, true, "user", "saved-secret");
        var submittedConfig = SerializeProviderConfig(
            "news.example.invalid", 119, false, "user", RedactedSecret);
        await SetConfigAsync(dbContext, "usenet.providers", existingConfig);
        var controller = CreateController(
            dbContext,
            CreateLoadedConfigManager(
                new ConfigItem { ConfigName = "usenet.providers", ConfigValue = existingConfig }),
            new Dictionary<string, StringValues> { ["usenet.providers"] = submittedConfig });

        var result = await HandleWithApiKeyAsync(controller);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            existingConfig,
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "usenet.providers")).ConfigValue);
    }

    [Fact]
    public async Task HandleApiRequest_UnresolvedRedactedArrMarkerReturnsBadRequestWithoutMutation()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var existingConfig = CreateArrConfigJson("saved-secret", "http://radarr:7878/Radarr");
        var submittedConfig = CreateArrConfigJson(RedactedSecret, "http://radarr:7878/radarr");
        await SetConfigAsync(dbContext, "arr.instances", existingConfig);
        var controller = CreateController(
            dbContext,
            CreateLoadedConfigManager(
                new ConfigItem { ConfigName = "arr.instances", ConfigValue = existingConfig }),
            new Dictionary<string, StringValues> { ["arr.instances"] = submittedConfig });

        var result = await HandleWithApiKeyAsync(controller);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            existingConfig,
            (await dbContext.ConfigItems.SingleAsync(x => x.ConfigName == "arr.instances")).ConfigValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleApiRequest_RejectsAnyReservedImportStateBeforeDatabaseQuery(bool mixedRequest)
    {
        await using var setup = await _fixture.ResetAndCreateMigratedContextAsync();
        await setup.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ConfigItems WHERE ConfigName = {TransferV3ReservedConfigPolicy.ImportStateKey}");
        await setup.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ({TransferV3ReservedConfigPolicy.ImportStateKey}, {TransferV3ImportStateCodec.FreshCanonicalJson})");
        await SetConfigAsync(setup, "rclone.host", "http://rclone:5572");

        try
        {
            var queryGuard = new DatabaseCommandGuard();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(setup.Database.GetConnectionString())
                .AddInterceptors(queryGuard)
                .Options;
            await using var guardedContext = new DavDatabaseContext(options);
            var configManager = CreateLoadedConfigManager(
                new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://rclone:5572" });
            var eventCount = 0;
            configManager.OnConfigChanged += (_, _) => eventCount++;
            var form = new Dictionary<string, StringValues>
            {
                [TransferV3ReservedConfigPolicy.ImportStateKey] = "attacker-controlled"
            };
            if (mixedRequest)
                form["rclone.host"] = "http://rclone:5573";
            var controller = CreateController(guardedContext, configManager, form);

            var result = await HandleWithApiKeyAsync(controller);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(0, queryGuard.CommandCount);
            Assert.Equal(0, eventCount);
            setup.ChangeTracker.Clear();
            Assert.Equal(
                TransferV3ImportStateCodec.FreshCanonicalJson,
                (await setup.ConfigItems.SingleAsync(x =>
                    x.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey)).ConfigValue);
            Assert.Equal(
                "http://rclone:5572",
                (await setup.ConfigItems.SingleAsync(x => x.ConfigName == "rclone.host")).ConfigValue);
        }
        finally
        {
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM ConfigItems WHERE ConfigName = {TransferV3ReservedConfigPolicy.ImportStateKey}");
        }
    }

    private static UpdateConfigController CreateController
    (
        DavDatabaseContext dbContext,
        ConfigManager configManager,
        Dictionary<string, StringValues> form
    )
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(form);

        return new UpdateConfigController(new DavDatabaseClient(dbContext), configManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context
            }
        };
    }

    private static async Task SetConfigAsync(DavDatabaseContext dbContext, string name, string value)
    {
        var existing = await dbContext.ConfigItems.SingleOrDefaultAsync(x => x.ConfigName == name);
        if (existing is null)
        {
            dbContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = name,
                ConfigValue = value
            });
        }
        else
        {
            existing.ConfigValue = value;
        }

        await dbContext.SaveChangesAsync();
    }

    private static async Task<List<string>> GetConfigRowsAsync(DavDatabaseContext dbContext, IEnumerable<string> names)
    {
        var nameList = names.ToList();
        return await dbContext.ConfigItems
            .Where(x => nameList.Contains(x.ConfigName))
            .OrderBy(x => x.ConfigName)
            .Select(x => $"{x.ConfigName}={x.ConfigValue}")
            .ToListAsync();
    }

    private static ConfigManager CreateLoadedConfigManager(params ConfigItem[] items)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(items.ToList());
        return configManager;
    }

    private static string CreateProviderConfigJson(bool writeIndented)
    {
        return CreateProviderConfigJson("pass", writeIndented: writeIndented);
    }

    public static string CreateProviderConfigJson
    (
        string password,
        int maxConnections = 50,
        bool writeIndented = false
    )
    {
        return System.Text.Json.JsonSerializer.Serialize(
            new UsenetProviderConfig
            {
                Providers =
                [
                    new UsenetProviderConfig.ConnectionDetails
                    {
                        Type = ProviderType.Pooled,
                        Host = "news.example.invalid",
                        Port = 563,
                        UseSsl = true,
                        User = "user",
                        Pass = password,
                        MaxConnections = maxConnections,
                        Priority = 100
                    }
                ]
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = writeIndented });
    }

    private static string SerializeProviderConfig(
        string host,
        int port,
        bool useSsl,
        string user,
        string password)
    {
        return System.Text.Json.JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = host,
                    Port = port,
                    UseSsl = useSsl,
                    User = user,
                    Pass = password,
                    MaxConnections = 10
                }
            ]
        });
    }

    public static string CreateArrConfigJson(string apiKey, string host = "http://radarr:7878")
    {
        return System.Text.Json.JsonSerializer.Serialize(new ArrConfig
        {
            RadarrInstances =
            [
                new ArrConfig.ConnectionDetails
                {
                    Host = host,
                    ApiKey = apiKey
                }
            ]
        });
    }

    private static async Task<IActionResult> HandleWithApiKeyAsync(UpdateConfigController controller)
    {
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            return await controller.HandleApiRequest();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private sealed class DatabaseCommandGuard : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database query.");
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database query.");
        }

        public override InterceptionResult<int> NonQueryExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database command.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database command.");
        }

        public override InterceptionResult<object> ScalarExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database command.");
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;
            throw new InvalidOperationException("Reserved config validation ran a database command.");
        }
    }
}

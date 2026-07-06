using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.GetConfig;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class GetConfigControllerTests
{
    private const string RedactedSecret = "__NZBDAV_REDACTED__";

    private readonly ContentIndexDatabaseFixture _fixture;

    public GetConfigControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HandleApiRequest_RedactsSecretsByDefault()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.host", "http://rclone:5572");
        await SetConfigAsync(dbContext, "rclone.pass", "rclone-secret");
        await SetConfigAsync(dbContext, "api.key", "sab-secret");
        await SetConfigAsync(dbContext, "webdav.pass", "webdav-hash");
        await SetConfigAsync(dbContext, "usenet.providers", UpdateConfigControllerTests.CreateProviderConfigJson("provider-secret"));
        await SetConfigAsync(dbContext, "arr.instances", UpdateConfigControllerTests.CreateArrConfigJson("arr-secret"));

        var controller = CreateController(
            dbContext,
            [
                "rclone.host",
                "rclone.pass",
                "api.key",
                "webdav.pass",
                "usenet.providers",
                "arr.instances"
            ]);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetConfigResponse>(ok.Value);
        var items = response.ConfigItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        Assert.Equal("http://rclone:5572", items["rclone.host"]);
        Assert.Equal(RedactedSecret, items["rclone.pass"]);
        Assert.Equal(RedactedSecret, items["api.key"]);
        Assert.Equal(RedactedSecret, items["webdav.pass"]);
        Assert.DoesNotContain("provider-secret", items["usenet.providers"]);
        Assert.DoesNotContain("arr-secret", items["arr.instances"]);
        Assert.Contains(RedactedSecret, items["usenet.providers"]);
        Assert.Contains(RedactedSecret, items["arr.instances"]);
    }

    [Fact]
    public async Task HandleApiRequest_CanIncludeSecretsWhenExplicitlyRequested()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(dbContext, "rclone.pass", "rclone-secret");

        var controller = CreateController(dbContext, ["rclone.pass"], includeSecrets: true);

        var result = await HandleWithApiKeyAsync(controller);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetConfigResponse>(ok.Value);
        var item = Assert.Single(response.ConfigItems);
        Assert.Equal("rclone.pass", item.ConfigName);
        Assert.Equal("rclone-secret", item.ConfigValue);
    }

    private static GetConfigController CreateController
    (
        DavDatabaseContext dbContext,
        IEnumerable<string> configKeys,
        bool includeSecrets = false
    )
    {
        var form = new Dictionary<string, StringValues>
        {
            ["config-keys"] = new StringValues(configKeys.ToArray())
        };
        if (includeSecrets)
            form["include-secrets"] = "true";

        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(form);

        return new GetConfigController(new DavDatabaseClient(dbContext))
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

    private static async Task<IActionResult> HandleWithApiKeyAsync(GetConfigController controller)
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
}

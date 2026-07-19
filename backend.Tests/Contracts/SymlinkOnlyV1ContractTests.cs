using System.Reflection;
using System.Text.Json.Nodes;
using backend.Tests.Database;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Api.Controllers.Maintenance;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using AdminGetConfigController = NzbWebDAV.Api.Controllers.GetConfig.GetConfigController;
using AdminGetConfigResponse = NzbWebDAV.Api.Controllers.GetConfig.GetConfigResponse;
using SabGetConfigController = NzbWebDAV.Api.SabControllers.GetConfig.GetConfigController;
using UpdateConfigController = NzbWebDAV.Api.Controllers.UpdateConfig.UpdateConfigController;

namespace backend.Tests.Contracts;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class SymlinkOnlyV1ContractTests(ContentIndexDatabaseFixture fixture)
{
    private const string RetiredStrmSeedMigration = "20251106165542_Ensure-Strm-Key-Exists";
    private const string InternalFixtureKey = "fixture-internal";
    private static readonly string[] RetiredConfigNameValues =
    [
        "api.strm-key",
        "api.import-strategy",
        "api.completed-downloads-dir",
        "general.base-url"
    ];

    public static TheoryData<string> RetiredConfigNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var configName in RetiredConfigNameValues)
                data.Add(configName);
            return data;
        }
    }

    public static TheoryData<string> RetiredImportStrategies => new()
    {
        "strm",
        "both"
    };

    [Fact]
    public async Task FreshSqlite_DoesNotApplyOrSeedRetiredStrmKey()
    {
        await using var database = new SqliteContractDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();

        var actual = new
        {
            AppliedRetiredMigration = (await context.Database.GetAppliedMigrationsAsync())
                .Contains(RetiredStrmSeedMigration, StringComparer.Ordinal),
            SeededRetiredKey = await context.ConfigItems
                .AnyAsync(item => item.ConfigName == "api.strm-key")
        };

        Assert.Equal(
            new { AppliedRetiredMigration = false, SeededRetiredKey = false },
            actual);
    }

    [Theory]
    [MemberData(nameof(RetiredConfigNames))]
    public async Task UpdateConfig_RejectsRetiredStrmKeysWithoutMutation(string configName)
    {
        await using var context = await fixture.ResetAndCreateMigratedContextAsync();
        await SetConfigAsync(context, "api.manual-category", "old-category");
        await context.ConfigItems
            .Where(item => item.ConfigName == configName)
            .ExecuteDeleteAsync();

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.manual-category", ConfigValue = "old-category" }
        ]);
        var eventCount = 0;
        configManager.OnConfigChanged += (_, _) => eventCount++;
        var controller = CreateUpdateController(
            context,
            configManager,
            new Dictionary<string, StringValues>
            {
                [configName] = "retired-fixture-value",
                ["api.manual-category"] = "new-category"
            });

        var result = await HandleWithInternalKeyAsync(controller.HandleApiRequest);
        context.ChangeTracker.Clear();
        var actual = new
        {
            BadRequest = result is BadRequestObjectResult,
            RetiredRows = await context.ConfigItems.CountAsync(item => item.ConfigName == configName),
            OrdinaryValue = (await context.ConfigItems
                    .SingleAsync(item => item.ConfigName == "api.manual-category"))
                .ConfigValue,
            EventCount = eventCount
        };

        Assert.Equal(
            new
            {
                BadRequest = true,
                RetiredRows = 0,
                OrdinaryValue = "old-category",
                EventCount = 0
            },
            actual);
    }

    [Fact]
    public async Task GetConfig_OmitsRetiredStrmKeysEvenWhenPersisted()
    {
        await using var context = await fixture.ResetAndCreateMigratedContextAsync();
        foreach (var configName in RetiredConfigNameValues)
            await SetConfigAsync(context, configName, "retired-fixture-value");
        await SetConfigAsync(context, "rclone.host", "http://rclone.invalid");
        var requestedNames = RetiredConfigNameValues.Append("rclone.host");
        var controller = CreateGetConfigController(context, requestedNames);

        var result = await HandleWithInternalKeyAsync(controller.HandleApiRequest);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AdminGetConfigResponse>(ok.Value);
        Assert.Equal(
            ["rclone.host"],
            response.ConfigItems.Select(item => item.ConfigName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void IdsPreview_AcceptsInternalSignatureWithoutRetiredStrmConfig()
    {
        var itemPath = $".ids/{Guid.NewGuid():D}.mkv";
        var context = CreateSignedPreviewContext(
            itemPath,
            GetWebdavItemRequest.GenerateDownloadKey(InternalFixtureKey, itemPath),
            new ConfigManager());

        var request = WithInternalKey(() => new GetWebdavItemRequest(context));

        Assert.Equal(itemPath, request.Item);
    }

    [Fact]
    public void IdsPreview_RejectsPersistedRetiredStrmSignature()
    {
        var itemPath = $".ids/{Guid.NewGuid():D}.mkv";
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "retired" }
        ]);
        var context = CreateSignedPreviewContext(
            itemPath,
            GetWebdavItemRequest.GenerateDownloadKey("retired", itemPath),
            configManager);

        Assert.Throws<UnauthorizedAccessException>(
            () => WithInternalKey(() => new GetWebdavItemRequest(context)));
    }

    [Theory]
    [InlineData("api/convert-strm-to-symlinks")]
    [InlineData("api/recreate-strm-files")]
    public void ControllerAssembly_DoesNotExposeRetiredStrmRoute(string retiredRoute)
    {
        var routes = typeof(AdminGetConfigController).Assembly.DefinedTypes
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => template is not null);

        Assert.DoesNotContain(retiredRoute, routes);
    }

    [Fact]
    public void MaintenanceContract_ExposesOnlySymlinkCleanupKinds()
    {
        Assert.Equal(
            [nameof(MaintenanceRunKind.RemoveUnlinkedFiles), nameof(MaintenanceRunKind.RemoveUnlinkedFilesDryRun)],
            Enum.GetNames<MaintenanceRunKind>());
    }

    [Theory]
    [InlineData("convert-strm-to-symlinks")]
    [InlineData("recreate-strm-files")]
    public void MaintenanceContract_RejectsRetiredStrmApiKind(string apiKind)
    {
        Assert.False(MaintenanceRunApiValues.TryParseKind(apiKind, out _));
    }

    [Theory]
    [MemberData(nameof(RetiredImportStrategies))]
    public async Task SabGetConfig_AlwaysReportsCompletedSymlinks(string legacyImportStrategy)
    {
        var configManager = CreateLegacyImportConfig(legacyImportStrategy);
        var context = CreateSabContext();
        var controller = new SabGetConfigController(context, configManager);

        var result = await HandleWithInternalKeyAsync(controller.HandleRequest);

        var content = Assert.IsType<ContentResult>(result);
        var root = JsonNode.Parse(Assert.IsType<string>(content.Content));
        Assert.Equal(ExpectedCompletedDirectory(), root?["config"]?["misc"]?["complete_dir"]?.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(RetiredImportStrategies))]
    public void SabStatus_AlwaysReportsCompletedSymlinks(string legacyImportStrategy)
    {
        Assert.Equal(
            ExpectedCompletedDirectory(),
            GetStatusController.GetCompleteDir(CreateLegacyImportConfig(legacyImportStrategy)));
    }

    [Theory]
    [MemberData(nameof(RetiredImportStrategies))]
    public void SabHistory_AlwaysReportsCompletedSymlinks(string legacyImportStrategy)
    {
        var historyId = Guid.NewGuid();
        var downloadFolder = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Example",
            fileSize: null,
            DavItem.ItemType.Directory,
            DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: null);
        var historyItem = new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            DownloadDirId = downloadFolder.Id
        };

        var slot = GetHistoryResponse.HistorySlot.FromHistoryItem(
            historyItem,
            downloadFolder,
            CreateLegacyImportConfig(legacyImportStrategy));

        Assert.Equal(
            Path.Join(ExpectedCompletedDirectory(), "movies", "Example"),
            slot.DownloadPath);
    }

    [Fact]
    public void OrganizedDiscovery_IgnoresValidLookingStrmFiles()
    {
        var libraryRoot = Path.Join(
            Path.GetTempPath(),
            "nzbdav-symlink-only-contract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryRoot);
        try
        {
            var symlinkId = Guid.NewGuid();
            var strmId = Guid.NewGuid();
            var symlinkPath = Path.Join(libraryRoot, "Symlink.mkv");
            File.CreateSymbolicLink(
                symlinkPath,
                DatabaseStoreSymlinkFile.GetTargetPath(symlinkId, "/mnt/nzbdav"));
            File.WriteAllText(
                Path.Join(libraryRoot, "Retired.strm"),
                $"http://example.invalid/view/.ids/{strmId}.mkv");
            var configManager = new ConfigManager();
            configManager.UpdateValues([
                new ConfigItem { ConfigName = "media.library-dir", ConfigValue = libraryRoot },
                new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = "/mnt/nzbdav" }
            ]);

            var link = Assert.Single(OrganizedLinksUtil.GetLibraryDavItemLinks(configManager));

            Assert.Equal(symlinkId, link.DavItemId);
            Assert.Equal(symlinkPath, link.LinkPath);
            Assert.Equal(symlinkPath, link.SymlinkInfo.SymlinkPath);
        }
        finally
        {
            Directory.Delete(libraryRoot, recursive: true);
        }
    }

    private static UpdateConfigController CreateUpdateController(
        DavDatabaseContext context,
        ConfigManager configManager,
        Dictionary<string, StringValues> form)
    {
        var httpContext = CreateInternalContext();
        httpContext.Request.Form = new FormCollection(form);
        return new UpdateConfigController(new DavDatabaseClient(context), configManager)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static AdminGetConfigController CreateGetConfigController(
        DavDatabaseContext context,
        IEnumerable<string> configNames)
    {
        var httpContext = CreateInternalContext();
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["config-keys"] = new StringValues(configNames.ToArray())
        });
        return new AdminGetConfigController(new DavDatabaseClient(context))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static DefaultHttpContext CreateInternalContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = InternalFixtureKey;
        return context;
    }

    private static DefaultHttpContext CreateSabContext()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?apikey=fixture-public");
        return context;
    }

    private static DefaultHttpContext CreateSignedPreviewContext(
        string itemPath,
        string signature,
        ConfigManager configManager)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/view/{itemPath}";
        context.Request.QueryString = new QueryString($"?downloadKey={signature}");
        context.Items["configManager"] = configManager;
        return context;
    }

    private static ConfigManager CreateLegacyImportConfig(string importStrategy)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.key", ConfigValue = "fixture-public" },
            new ConfigItem { ConfigName = "api.import-strategy", ConfigValue = importStrategy },
            new ConfigItem { ConfigName = "api.completed-downloads-dir", ConfigValue = "/legacy/completed-downloads" },
            new ConfigItem { ConfigName = "rclone.mount-dir", ConfigValue = "/mnt/nzbdav" }
        ]);
        return configManager;
    }

    private static string ExpectedCompletedDirectory() =>
        Path.Join("/mnt/nzbdav", DavItem.SymlinkFolder.Name);

    private static async Task SetConfigAsync(
        DavDatabaseContext context,
        string configName,
        string configValue)
    {
        var existing = await context.ConfigItems
            .SingleOrDefaultAsync(item => item.ConfigName == configName);
        if (existing is null)
        {
            context.ConfigItems.Add(new ConfigItem
            {
                ConfigName = configName,
                ConfigValue = configValue
            });
        }
        else
        {
            existing.ConfigValue = configValue;
        }

        await context.SaveChangesAsync();
    }

    private static async Task<IActionResult> HandleWithInternalKeyAsync(
        Func<Task<IActionResult>> action)
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", InternalFixtureKey);
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous);
        }
    }


    private static T WithInternalKey<T>(Func<T> action)
    {
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", InternalFixtureKey);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous);
        }
    }
}

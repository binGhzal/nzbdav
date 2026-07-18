using System.Text.RegularExpressions;
using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class TransferV3ReservedConfigContainmentTests
{
    private const string FreshImportState = "{\"formatVersion\":3,\"state\":\"fresh\"}";
    private const string OrdinaryKey = "transfer-v3-containment-probe";
    private readonly ContentIndexDatabaseFixture _fixture;

    public TransferV3ReservedConfigContainmentTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ReservedPolicy_UsesOneExactOrdinalKey()
    {
        Assert.True(TransferV3ReservedConfigPolicy.IsReserved(
            TransferV3ReservedConfigPolicy.ImportStateKey));
        Assert.False(TransferV3ReservedConfigPolicy.IsReserved("Database.import-state"));
        Assert.False(TransferV3ReservedConfigPolicy.IsReserved("database.import-state "));
        Assert.False(TransferV3ReservedConfigPolicy.IsReserved(null));
    }

    [Theory]
    [InlineData(EntityState.Added, false)]
    [InlineData(EntityState.Added, true)]
    [InlineData(EntityState.Modified, false)]
    [InlineData(EntityState.Modified, true)]
    [InlineData(EntityState.Deleted, false)]
    [InlineData(EntityState.Deleted, true)]
    public async Task SaveChanges_RejectsTrackedReservedMutations
    (
        EntityState state,
        bool useAsync
    )
    {
        await using var context = await CreateCleanContextAsync();
        if (state == EntityState.Added)
        {
            context.ConfigItems.Add(new ConfigItem
            {
                ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
                ConfigValue = FreshImportState
            });
        }
        else
        {
            await InsertRawConfigAsync(
                context,
                TransferV3ReservedConfigPolicy.ImportStateKey,
                FreshImportState);
            context.ChangeTracker.Clear();
            var marker = await context.ConfigItems.SingleAsync(x =>
                x.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey);
            if (state == EntityState.Modified)
                marker.ConfigValue = "mutated";
            else
                context.ConfigItems.Remove(marker);
        }

        var error = useAsync
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync())
            : Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Equal(TransferV3ReservedConfigPolicy.ReservedConfigMessage, error.Message);
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var stored = await assertionContext.ConfigItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey);
        if (state == EntityState.Added)
            Assert.Null(stored);
        else
            Assert.Equal(FreshImportState, Assert.IsType<ConfigItem>(stored).ConfigValue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChanges_RejectsKeyChangesFromOrToReservedValue
    (
        bool fromReserved,
        bool useAsync
    )
    {
        await using var context = await CreateCleanContextAsync();
        var originalKey = fromReserved
            ? TransferV3ReservedConfigPolicy.ImportStateKey
            : OrdinaryKey;
        var replacementKey = fromReserved
            ? OrdinaryKey
            : TransferV3ReservedConfigPolicy.ImportStateKey;
        await InsertRawConfigAsync(context, originalKey, FreshImportState);
        context.ChangeTracker.Clear();
        var item = await context.ConfigItems.SingleAsync(x => x.ConfigName == originalKey);
        item.ConfigName = replacementKey;

        var error = useAsync
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync())
            : Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Equal(TransferV3ReservedConfigPolicy.ReservedConfigMessage, error.Message);
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            FreshImportState,
            (await assertionContext.ConfigItems.AsNoTracking().SingleAsync(x => x.ConfigName == originalKey))
            .ConfigValue);
        Assert.False(await assertionContext.ConfigItems.AsNoTracking().AnyAsync(x => x.ConfigName == replacementKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_RejectsMixedBatchAtomically(bool useAsync)
    {
        await using var context = await CreateCleanContextAsync();
        context.ConfigItems.AddRange(
            new ConfigItem { ConfigName = OrdinaryKey, ConfigValue = "ordinary" },
            new ConfigItem
            {
                ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
                ConfigValue = FreshImportState
            });

        var error = useAsync
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync())
            : Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Equal(TransferV3ReservedConfigPolicy.ReservedConfigMessage, error.Message);
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await assertionContext.ConfigItems.AsNoTracking().AnyAsync(x =>
            x.ConfigName == OrdinaryKey
            || x.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey));
    }

    [Fact]
    public async Task SaveChanges_RejectsBeforeBlobWriteOrRcloneInvalidationStaging()
    {
        await using var context = await CreateCleanContextAsync();
        var nzbBlobId = Guid.NewGuid();
        var rarBlobId = Guid.NewGuid();
        var multipartBlobId = Guid.NewGuid();
        var davId = Guid.NewGuid();
        try
        {
            context.ConfigItems.Add(new ConfigItem
            {
                ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
                ConfigValue = FreshImportState
            });
            context.BlobNzbFiles.Add(new DavNzbFile
            {
                Id = nzbBlobId,
                SegmentIds = ["must-not-be-written"]
            });
            context.BlobRarFiles.Add(new DavRarFile { Id = rarBlobId });
            context.BlobMultipartFiles.Add(new DavMultipartFile { Id = multipartBlobId });
            context.Items.Add(new DavItem
            {
                Id = davId,
                IdPrefix = davId.ToString("N")[..DavItem.IdPrefixLength],
                CreatedAt = DateTime.Now,
                ParentId = DavItem.ContentFolder.Id,
                Name = "must-not-stage",
                Type = DavItem.ItemType.Directory,
                SubType = DavItem.ItemSubType.Directory,
                Path = "/content/must-not-stage"
            });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

            Assert.Equal(TransferV3ReservedConfigPolicy.ReservedConfigMessage, error.Message);
            Assert.Null(BlobStore.ReadBlob(nzbBlobId));
            Assert.Null(BlobStore.ReadBlob(rarBlobId));
            Assert.Null(BlobStore.ReadBlob(multipartBlobId));
            Assert.Empty(context.ChangeTracker.Entries<RcloneInvalidationItem>());
            await using var assertionContext = await _fixture.CreateMigratedContextAsync();
            Assert.False(await assertionContext.Items.AsNoTracking().AnyAsync(x => x.Id == davId));
            Assert.Empty(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        }
        finally
        {
            BlobStore.Delete(nzbBlobId);
            BlobStore.Delete(rarBlobId);
            BlobStore.Delete(multipartBlobId);
        }
    }

    [Fact]
    public void GenericReadPaths_FilterBeforeMaterializationAndDefendAgainInMemory()
    {
        var getSource = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Api/Controllers/GetConfig/GetConfigController.cs"));
        var loadSource = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Config/ConfigManager.cs"));

        AssertQueryTimeThenInMemoryDefense(getSource);
        AssertQueryTimeThenInMemoryDefense(loadSource);
    }

    [Fact]
    public void RawConfigMutationCallsites_AreRestrictedToStateCasMigrationsAndFilteredV2Delete()
    {
        var backendRoot = SqliteContractTestSupport.AbsolutePath("backend");
        var forbidden = new List<string>();
        foreach (var path in Directory.EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(SqliteContractTestSupport.RepositoryRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath.Contains("/obj/", StringComparison.Ordinal)
                || relativePath.Contains("/bin/", StringComparison.Ordinal))
                continue;
            var source = File.ReadAllText(path);
            var hasRawMutation = Regex.IsMatch(
                source,
                "(?im)\\b(?:INSERT\\s+INTO|UPDATE|DELETE\\s+FROM)\\s+" +
                "(?:(?:[\\\"`\\[]?[A-Za-z0-9_]+[\\\"`\\]]?)\\.)?[\\\"`\\[]?ConfigItems\\b");
            var hasBulkMutation = Regex.IsMatch(
                source,
                "(?is)(?:ConfigItems|Set<ConfigItem>\\s*\\(\\s*\\)).{0,300}" +
                "Execute(?:Update|Delete)Async");
            if (!hasRawMutation && !hasBulkMutation) continue;

            var allowed = relativePath is
                              "backend/Database/Migrations/20251113081523_Populate-Usenet-Providers-Config.cs"
                              or "backend/Database/Migrations/20260121033824_Populate-Blocklisted-Files-Setting.cs"
                              or "backend/Database/Migrations/20260122220920_Populate-Health-Check-Categories-Setting.cs"
                              or "backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.cs"
                          || relativePath ==
                          "backend/Database/Transfer/TransferV3ImportStateStore.cs"
                          || (relativePath == "backend/Database/DatabaseTransferService.cs"
                              && hasBulkMutation
                              && !hasRawMutation);
            if (!allowed)
                forbidden.Add(relativePath);
        }

        Assert.Empty(forbidden);
        var v2Source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/DatabaseTransferService.cs"));
        Assert.Contains(
            ".Where(x => x.ConfigName != TransferV3ReservedConfigPolicy.ImportStateKey)",
            v2Source,
            StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(v2Source, "ConfigItems.{0,300}ExecuteDeleteAsync", RegexOptions.Singleline)
                .Cast<Match>());
    }

    private async Task<DavDatabaseContext> CreateCleanContextAsync()
    {
        var context = await _fixture.ResetAndCreateMigratedContextAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ConfigItems WHERE ConfigName IN ({TransferV3ReservedConfigPolicy.ImportStateKey}, {OrdinaryKey})");
        context.ChangeTracker.Clear();
        return context;
    }

    private static Task<int> InsertRawConfigAsync(DavDatabaseContext context, string name, string value) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ({name}, {value})");

    private static void AssertQueryTimeThenInMemoryDefense(string source)
    {
        var queryFilter =
            "ConfigName != TransferV3ReservedConfigPolicy.ImportStateKey";
        var inMemoryFilter =
            "!TransferV3ReservedConfigPolicy.IsReserved";
        var queryFilterIndex = source.IndexOf(queryFilter, StringComparison.Ordinal);
        var materializationIndex = source.IndexOf("ToListAsync", StringComparison.Ordinal);
        var inMemoryFilterIndex = source.IndexOf(inMemoryFilter, StringComparison.Ordinal);

        Assert.True(queryFilterIndex >= 0, "Reserved key must be excluded in the database query.");
        Assert.True(
            materializationIndex > queryFilterIndex,
            "Reserved key query filter must run before materialization.");
        Assert.True(
            inMemoryFilterIndex > materializationIndex,
            "An exact-ordinal in-memory defense must run after materialization.");
    }
}

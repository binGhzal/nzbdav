using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

[Collection(nameof(PostgreSqlSerialCollection))]
public sealed class PostgreSqlCatalogInventoryTests
{
    private const string UpdateVariable = "NZBDAV_UPDATE_POSTGRES_CATALOGS";

    [PostgreSqlFact]
    public async Task EmptyHistoryBaselineAndHeadMatchCheckedInInspectableInventories()
    {
        var inventories = new Dictionary<PostgreSqlCatalogState, string>();

        await using (var emptySchema = await PostgreSqlTestSchema.CreateAsync("catalog_empty_schema"))
        await using (var connection = await emptySchema.OpenConnectionAsync())
        {
            inventories[PostgreSqlCatalogState.EmptySchema] =
                await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        }

        await using (var emptyHistory = await PostgreSqlTestSchema.CreateAsync("catalog_empty_history"))
        {
            await emptyHistory.ExecuteAsync(
                """
                CREATE TABLE "__EFMigrationsHistory_PostgreSql" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory_PostgreSql" PRIMARY KEY ("MigrationId"));
                """);
            await using var connection = await emptyHistory.OpenConnectionAsync();
            inventories[PostgreSqlCatalogState.EmptyHistory] =
                await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        }

        await using (var baseline = await PostgreSqlTestSchema.CreateAsync("catalog_baseline"))
        {
            await using var context = new PostgreSqlDavDatabaseContext(baseline.CreateOptions());
            var migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260712000000_PostgreSqlNativeBaseline");
            await using var connection = await baseline.OpenConnectionAsync();
            inventories[PostgreSqlCatalogState.Baseline] =
                await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        }

        await using (var head = await PostgreSqlTestSchema.CreateAsync("catalog_head"))
        {
            // Direct EF is intentional here: inventory generation must not use
            // the validator whose expected bytes are under test.
            await using var context = new PostgreSqlDavDatabaseContext(head.CreateOptions());
            await context.Database.MigrateAsync();
            await using var connection = await head.OpenConnectionAsync();
            inventories[PostgreSqlCatalogState.Head] =
                await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
        }

        await using (var repeatedHead = await PostgreSqlTestSchema.CreateAsync("catalog_head_repeat"))
        {
            await using var context = new PostgreSqlDavDatabaseContext(repeatedHead.CreateOptions());
            await context.Database.MigrateAsync();
            await using var connection = await repeatedHead.OpenConnectionAsync();
            var repeatedInventory =
                await PostgreSqlPhysicalCatalogContract.CaptureCanonicalAsync(connection);
            Assert.Equal(inventories[PostgreSqlCatalogState.Head], repeatedInventory);
        }

        Assert.Empty(HistoryLines(inventories[PostgreSqlCatalogState.EmptySchema]));
        Assert.Empty(HistoryLines(inventories[PostgreSqlCatalogState.EmptyHistory]));
        Assert.Equal(
            ReviewedHistoryLines(1),
            HistoryLines(inventories[PostgreSqlCatalogState.Baseline]));
        Assert.Equal(
            ReviewedHistoryLines(PostgreSqlNativeMigrationContract.Head.Count),
            HistoryLines(inventories[PostgreSqlCatalogState.Head]));

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(CatalogDirectory());
            foreach (var (state, inventory) in inventories)
                await File.WriteAllTextAsync(CatalogPath(state), inventory);
            return;
        }

        foreach (var (state, actual) in inventories)
        {
            var expected = PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(state);
            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"{state} catalog differs: expected {PostgreSqlPhysicalCatalogContract.Sha256(expected)}, " +
                $"actual {PostgreSqlPhysicalCatalogContract.Sha256(actual)}.");
        }
    }

    private static string[] ReviewedHistoryLines(int count) =>
        PostgreSqlNativeMigrationContract.Head
            .Take(count)
            .Select(entry => $"history-row|{entry.MigrationId}|{entry.ProductVersion}")
            .ToArray();

    private static string[] HistoryLines(string inventory) =>
        inventory
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("history-row|", StringComparison.Ordinal))
            .ToArray();

    private static string CatalogDirectory() => SqliteContractTestSupport.AbsolutePath(
        "backend/Database/PostgreSqlCatalogs");

    private static string CatalogPath(PostgreSqlCatalogState state)
    {
        var name = state switch
        {
            PostgreSqlCatalogState.EmptySchema => "postgresql-native-empty-schema-catalog.txt",
            PostgreSqlCatalogState.EmptyHistory => "postgresql-native-empty-history-catalog.txt",
            PostgreSqlCatalogState.Baseline => "postgresql-native-baseline-catalog.txt",
            PostgreSqlCatalogState.Head => "postgresql-native-head-catalog.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
        return Path.Combine(CatalogDirectory(), name);
    }
}

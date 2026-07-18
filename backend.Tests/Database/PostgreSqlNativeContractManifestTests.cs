using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class PostgreSqlNativeContractManifestTests
{
    [Fact]
    public void CheckedInManifestPinsTheExactNativeModelAndOperationalObjects()
    {
        var manifestPath = SqliteContractTestSupport.AbsolutePath(
            "backend.Tests/TestData/postgresql-native-schema-contract.json");
        var manifest = JsonSerializer.Deserialize<PostgreSqlContractManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);

        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var canonicalContract = BuildCanonicalModelContract(model);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContract)));

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("PostgreSqlDavDatabaseContext", manifest.Context);
        Assert.Equal("__EFMigrationsHistory_PostgreSql", manifest.HistoryTable);
        Assert.Equal(
        [
            "20260712000000_PostgreSqlNativeBaseline",
            "20260712000100_PostgreSqlOperationalTriggers"
        ],
            manifest.Migrations);
        Assert.Equal(28, manifest.Tables);
        Assert.Equal(240, manifest.Columns);
        Assert.Equal(28, manifest.PrimaryKeys);
        Assert.Equal(8, manifest.ForeignKeys);
        Assert.Equal(56, manifest.SecondaryIndexes);
        Assert.Equal(84, manifest.TotalIndexes);
        Assert.Equal(36, manifest.Constraints);
        Assert.Equal(46, manifest.UuidColumns);
        Assert.Equal(6, manifest.BooleanColumns);
        Assert.Equal(47, manifest.BoundedVarcharColumns);
        Assert.Equal(20, manifest.CCollatedColumns);
        Assert.Equal(4, manifest.LocalWallTimestampColumns);
        Assert.Equal(9, manifest.OperationalFunctions.Length);
        Assert.Equal(9, manifest.OperationalTriggers.Length);
        Assert.Equal(PostgreSqlPhysicalCatalogContract.ExpectedSha256, manifest.CatalogSha256);
        Assert.Equal(
            PostgreSqlPhysicalCatalogContract.Sha256(
                PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.EmptySchema)),
            manifest.EmptySchemaCatalogSha256);
        Assert.Equal(
            PostgreSqlPhysicalCatalogContract.Sha256(
                PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.EmptyHistory)),
            manifest.EmptyHistoryCatalogSha256);
        Assert.Equal(
            PostgreSqlPhysicalCatalogContract.Sha256(
                PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.Baseline)),
            manifest.BaselineCatalogSha256);
        Assert.Equal(PostgreSqlPhysicalCatalogContract.ExpectedSha256, manifest.HeadCatalogSha256);
        Assert.True(
            string.Equals(actualHash, manifest.CanonicalModelSha256, StringComparison.Ordinal),
            $"Canonical PostgreSQL model hash is {actualHash}.");
        AssertFileHash(
            "backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.cs",
            manifest.BaselineMigrationSha256);
        AssertFileHash(
            "backend/Database/PostgreSqlMigrations/20260712000000_PostgreSqlNativeBaseline.Designer.cs",
            manifest.BaselineDesignerSha256);
        AssertFileHash(
            "backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.cs",
            manifest.OperationalMigrationSha256);
        AssertFileHash(
            "backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.Designer.cs",
            manifest.OperationalDesignerSha256);
        AssertFileHash(
            "backend/Database/PostgreSqlMigrations/PostgreSqlDavDatabaseContextModelSnapshot.cs",
            manifest.SnapshotSha256);
    }

    private static string BuildCanonicalModelContract(IModel model)
    {
        var lines = new List<string>();
        foreach (var entity in model.GetEntityTypes()
                     .OrderBy(entity => entity.GetTableName(), StringComparer.Ordinal))
        {
            var table = entity.GetTableName()!;
            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
            lines.Add($"table|{table}");
            foreach (var property in entity.GetProperties()
                         .OrderBy(property => property.GetColumnName(storeObject), StringComparer.Ordinal))
            {
                var defaultValue = property.FindAnnotation(RelationalAnnotationNames.DefaultValue)?.Value;
                lines.Add(string.Join('|',
                    "column",
                    table,
                    property.GetColumnName(storeObject),
                    property.GetColumnType(),
                    property.IsNullable,
                    property.GetMaxLength()?.ToString() ?? "",
                    property.GetCollation() ?? "",
                    defaultValue?.ToString() ?? "",
                    property.GetDefaultValueSql() ?? "",
                    property.GetValueConverter()?.GetType().FullName ?? "",
                    property.GetValueConverter()?.ProviderClrType.FullName ?? ""));
            }

            var primaryKey = entity.FindPrimaryKey()!;
            lines.Add($"pk|{primaryKey.GetName()}|{string.Join(',', primaryKey.Properties.Select(x => x.Name))}");
            foreach (var foreignKey in entity.GetForeignKeys().OrderBy(key => key.GetConstraintName(), StringComparer.Ordinal))
                lines.Add(string.Join('|',
                    "fk",
                    foreignKey.GetConstraintName(),
                    foreignKey.PrincipalEntityType.GetTableName(),
                    foreignKey.DeleteBehavior,
                    string.Join(',', foreignKey.Properties.Select(x => x.Name))));
            foreach (var index in entity.GetIndexes().OrderBy(index => index.GetDatabaseName(), StringComparer.Ordinal))
                lines.Add(string.Join('|',
                    "index",
                    index.GetDatabaseName(),
                    string.Join(',', index.Properties.Select(x => x.Name)),
                    index.IsUnique,
                    index.IsDescending is null ? "" : string.Join(',', index.IsDescending.Select(x => x ? '1' : '0')),
                    index.GetFilter() ?? ""));
        }

        return string.Join('\n', lines) + '\n';
    }

    private static PostgreSqlDavDatabaseContext CreateContext()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "nzbdav",
            Username = "nzbdav",
            Password = "not-used",
            Timezone = TimeZoneInfo.Local.Id,
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(DatabaseMigrationPolicy.PostgreSqlHistoryTableName))
            .Options;
        return new PostgreSqlDavDatabaseContext(options);
    }

    private static void AssertFileHash(string repositoryRelativePath, string expected)
    {
        var path = SqliteContractTestSupport.AbsolutePath(repositoryRelativePath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(
            string.Equals(actual, expected, StringComparison.Ordinal),
            $"PostgreSQL contract file hash for {repositoryRelativePath} is {actual}.");
    }

    private sealed record PostgreSqlContractManifest(
        int SchemaVersion,
        string Context,
        string HistoryTable,
        string[] Migrations,
        int Tables,
        int Columns,
        int PrimaryKeys,
        int ForeignKeys,
        int SecondaryIndexes,
        int TotalIndexes,
        int Constraints,
        int UuidColumns,
        int BooleanColumns,
        int BoundedVarcharColumns,
        int CCollatedColumns,
        int LocalWallTimestampColumns,
        string[] OperationalFunctions,
        string[] OperationalTriggers,
        string CanonicalModelSha256,
        string CatalogSha256,
        string EmptySchemaCatalogSha256,
        string EmptyHistoryCatalogSha256,
        string BaselineCatalogSha256,
        string HeadCatalogSha256,
        string BaselineMigrationSha256,
        string BaselineDesignerSha256,
        string OperationalMigrationSha256,
        string OperationalDesignerSha256,
        string SnapshotSha256);
}

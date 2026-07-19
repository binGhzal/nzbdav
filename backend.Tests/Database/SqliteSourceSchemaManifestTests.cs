using System.Text;
using Microsoft.EntityFrameworkCore;

namespace backend.Tests.Database;

public sealed class SqliteSourceSchemaManifestTests
{
    [Theory]
    [InlineData("20250529081501_InitializeDatabase")]
    [InlineData("20251105050845_Add-HealthCheckStats-Table")]
    [InlineData("20260108054841_Add-BlobCleanupItems-Table")]
    [InlineData("20260226053712_Add-NzbBlobId-And-NzbNames")]
    [InlineData("20260702183000_Add-RepairRuns-Tables")]
    [InlineData("20260711120000_Add-Worker-Lease-Coordination")]
    [InlineData("20260711133000_Add-Import-Receipts")]
    public async Task HistoricalCheckpointReopensAndUpgradesToExactReviewedHead(string checkpointMigration)
    {
        var contract = SqliteContractTestSupport.ReadFixture<SqliteMigrationContract>(
            SqliteContractTestSupport.MigrationContractRelativePath);
        var checkpointIndex = contract.Migrations
            .Select((migration, index) => (migration.Id, Index: index))
            .Single(value => value.Id == checkpointMigration)
            .Index;
        var checkpointIds = contract.Migrations.Take(checkpointIndex + 1)
            .Select(migration => migration.Id).ToArray();
        var allIds = contract.Migrations.Select(migration => migration.Id).ToArray();

        await using var database = new SqliteContractDatabase();
        await using (var checkpointContext = database.CreateContext())
        {
            await checkpointContext.Database.MigrateAsync(checkpointMigration);
            Assert.Equal(checkpointIds, await checkpointContext.Database.GetAppliedMigrationsAsync());
        }

        await using var reopenedContext = database.CreateContext();
        Assert.Equal(checkpointIds, await reopenedContext.Database.GetAppliedMigrationsAsync());
        await reopenedContext.Database.MigrateAsync();

        Assert.Equal(allIds, await reopenedContext.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await reopenedContext.Database.GetPendingMigrationsAsync());
        Assert.False(reopenedContext.Database.HasPendingModelChanges());
        await AssertIntegrityAndForeignKeysAsync(reopenedContext);

        var actualManifest = await SqliteContractTestSupport.CaptureSchemaManifestAsync(reopenedContext);
        Assert.Equal(
            File.ReadAllBytes(SqliteContractTestSupport.AbsolutePath(
                SqliteContractTestSupport.SchemaManifestRelativePath)),
            SqliteContractTestSupport.SerializeCanonical(actualManifest));
    }

    [Fact]
    public async Task TwoFreshMigrationRunsProduceTheReviewedCanonicalSchemaByteForByte()
    {
        var first = await SqliteContractTestSupport.CaptureSchemaManifestAsync();
        var second = await SqliteContractTestSupport.CaptureSchemaManifestAsync();
        var firstBytes = SqliteContractTestSupport.SerializeCanonical(first);
        var secondBytes = SqliteContractTestSupport.SerializeCanonical(second);

        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(1, first.FormatVersion);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", first.Provider);
        Assert.Equal(SqliteContractTestSupport.ContextName, first.Context);
        Assert.Equal(SqliteContractTestSupport.HistoryTableName, first.HistoryTable);
        Assert.Equal(48, first.AppliedMigrations.Count);
        Assert.Equal(SqliteContractTestSupport.LatestMigrationId, first.AppliedMigrations[^1]);
        var migrationContract = SqliteContractTestSupport.ReadFixture<SqliteMigrationContract>(
            SqliteContractTestSupport.MigrationContractRelativePath);
        Assert.Equal(
            migrationContract.Migrations.Select(migration => migration.Id),
            first.AppliedMigrations);
        Assert.True(
            first.LogicalTables.Count == 28,
            $"Expected 28 logical tables but captured {first.LogicalTables.Count}: "
            + string.Join(", ", first.LogicalTables.Select(table => table.Name)));
        Assert.Equal(240, first.LogicalTables.Sum(table => table.Columns.Count));
        Assert.Equal(28, first.LogicalTables.Count(table => table.PrimaryKey.Count > 0));
        Assert.Equal(8, first.LogicalTables.Sum(table => table.ForeignKeys.Count));
        Assert.Equal(84, first.LogicalTables.Sum(table => table.Indexes.Count));
        Assert.Equal(
            56,
            first.LogicalTables.Sum(table => table.Indexes.Count(index => index.Origin == "c")));
        Assert.Equal(9, first.Physical.TriggerNames.Count);
        Assert.DoesNotContain("TR_HistoryItems_Delete_AddHistoryCleanup", first.Physical.TriggerNames);
        Assert.Empty(first.Physical.ViewNames);
        Assert.Empty(first.Physical.UnrecognizedObjects);

        var fixturePath = SqliteContractTestSupport.AbsolutePath(
            SqliteContractTestSupport.SchemaManifestRelativePath);
        if (!File.Exists(fixturePath))
        {
            SqliteContractTestSupport.WriteMissingFixtureDiagnostic(
                "nzbdav-sqlite-source-schema-manifest.json",
                firstBytes);
        }

        Assert.Equal(File.ReadAllBytes(fixturePath), firstBytes);
    }

    [Fact]
    public async Task PhysicalManifestInventoriesEveryRequiredPragmaWithoutStorageOrDataState()
    {
        var manifest = await SqliteContractTestSupport.CaptureSchemaManifestAsync();
        var tableNames = manifest.Physical.SqliteSchema
            .Where(row => row.Type == "table")
            .Select(row => row.TableName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var indexNames = manifest.Physical.SqliteSchema
            .Where(row => row.Type == "index")
            .Select(row => row.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tableNames, manifest.Physical.TableXInfo.Select(capture => capture.Table));
        Assert.Equal(tableNames, manifest.Physical.IndexList.Select(capture => capture.Table));
        Assert.Equal(tableNames, manifest.Physical.ForeignKeyList.Select(capture => capture.Table));
        Assert.Equal(indexNames, manifest.Physical.IndexXInfo.Select(capture => capture.Index));
        Assert.Equal(
            manifest.Physical.SqliteSchema.Where(row => row.Type == "trigger")
                .Select(row => row.Name).Order(StringComparer.Ordinal),
            manifest.Physical.TriggerNames);

        var json = Encoding.UTF8.GetString(SqliteContractTestSupport.SerializeCanonical(manifest));
        Assert.DoesNotContain("rootpage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page_count", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("freelist", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journal_mode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wal_checkpoint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlTextEncodingDistinguishesDatabaseNullFromLiteralSqlNull()
    {
        var databaseNull = SqliteContractTestSupport.SerializeCanonical(new SqlText("database-null", null));
        var literalSqlNull = SqliteContractTestSupport.SerializeCanonical(new SqlText("sql", "NULL"));

        Assert.False(databaseNull.SequenceEqual(literalSqlNull));
        Assert.Contains("\"kind\": \"database-null\"", Encoding.UTF8.GetString(databaseNull),
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\"", Encoding.UTF8.GetString(databaseNull), StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"sql\"", Encoding.UTF8.GetString(literalSqlNull),
            StringComparison.Ordinal);
        Assert.Contains("\"value\": \"NULL\"", Encoding.UTF8.GetString(literalSqlNull),
            StringComparison.Ordinal);
    }

    private static async Task AssertIntegrityAndForeignKeysAsync(NzbWebDAV.Database.DavDatabaseContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using (var integrity = context.Database.GetDbConnection().CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));
        }

        await using var foreignKeys = context.Database.GetDbConnection().CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }
}

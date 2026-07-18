using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace backend.Tests.Database;

public sealed class SqliteMigrationSmokeTests
{
    private const string ImportReceiptsMigration = "20260711133000_Add-Import-Receipts";
    private const string MaintenanceRunsMigration = "20260712113000_Add-Maintenance-Runs";
    private const string RcloneRevisionMigration = "20260712120000_Add-Rclone-Invalidation-Revision";
    private const string ArrImportCommandsMigration = "20260712123000_Add-Arr-Import-Commands";

    [Fact]
    public async Task ImportReceiptsDatabaseUpgradesToLatestWithValidSchema()
    {
        await using var fixture = new SqliteFileFixture();
        await using var dbContext = fixture.CreateContext();

        await dbContext.Database.MigrateAsync(ImportReceiptsMigration);

        Assert.Equal(
            ImportReceiptsMigration,
            (await dbContext.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await TableExistsAsync(dbContext, "MaintenanceRuns"));
        Assert.False(await TableExistsAsync(dbContext, "ArrImportCommands"));
        Assert.DoesNotContain(
            "Revision",
            (await ReadColumnsAsync(dbContext, "RcloneInvalidationItems")).Keys);
        Assert.Equal(
            [MaintenanceRunsMigration, RcloneRevisionMigration, ArrImportCommandsMigration],
            await dbContext.Database.GetPendingMigrationsAsync());

        await dbContext.Database.MigrateAsync();

        await AssertLatestSchemaIsHealthyAsync(dbContext);
    }

    [Fact]
    public async Task EmptyDatabaseMigratesToLatestWithValidSchema()
    {
        await using var fixture = new SqliteFileFixture();
        await using var dbContext = fixture.CreateContext();

        Assert.Empty(await dbContext.Database.GetAppliedMigrationsAsync());

        await dbContext.Database.MigrateAsync();

        await AssertLatestSchemaIsHealthyAsync(dbContext);
    }

    private static async Task AssertLatestSchemaIsHealthyAsync(DavDatabaseContext dbContext)
    {
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
        Assert.False(dbContext.Database.HasPendingModelChanges());
        Assert.Equal(
            ArrImportCommandsMigration,
            (await dbContext.Database.GetAppliedMigrationsAsync()).Last());

        Assert.Equal("ok", await ExecuteScalarStringAsync(dbContext, "PRAGMA integrity_check;"));
        Assert.Empty(await ReadFirstColumnAsync(dbContext, "PRAGMA foreign_key_check;"));

        AssertColumns(
            await ReadColumnsAsync(dbContext, "MaintenanceRuns"),
            new Dictionary<string, ColumnSpec>(StringComparer.Ordinal)
            {
                ["Id"] = new("TEXT", IsNullable: false, IsPrimaryKey: true),
                ["Kind"] = new("INTEGER", IsNullable: false),
                ["Status"] = new("INTEGER", IsNullable: false),
                ["ActiveSlot"] = new("INTEGER", IsNullable: true),
                ["RequestedBy"] = new("TEXT", IsNullable: false),
                ["CreatedAt"] = new("INTEGER", IsNullable: false),
                ["StartedAt"] = new("INTEGER", IsNullable: true),
                ["UpdatedAt"] = new("INTEGER", IsNullable: false),
                ["CompletedAt"] = new("INTEGER", IsNullable: true),
                ["CancellationRequestedAt"] = new("INTEGER", IsNullable: true),
                ["ProgressCurrent"] = new("INTEGER", IsNullable: false),
                ["ProgressTotal"] = new("INTEGER", IsNullable: true),
                ["Message"] = new("TEXT", IsNullable: true),
                ["Error"] = new("TEXT", IsNullable: true)
            });
        await AssertIndexAsync(dbContext, "MaintenanceRuns", "IX_MaintenanceRuns_ActiveSlot", isUnique: true);
        await AssertIndexAsync(dbContext, "MaintenanceRuns", "IX_MaintenanceRuns_Kind_CreatedAt", isUnique: false);
        await AssertIndexAsync(dbContext, "MaintenanceRuns", "IX_MaintenanceRuns_Status_CreatedAt", isUnique: false);

        var rcloneColumns = await ReadColumnsAsync(dbContext, "RcloneInvalidationItems");
        Assert.True(rcloneColumns.TryGetValue("Revision", out var revision));
        Assert.Equal(new ColumnSpec("INTEGER", IsNullable: false, DefaultValue: "1"), revision);

        AssertColumns(
            await ReadColumnsAsync(dbContext, "ArrImportCommands"),
            new Dictionary<string, ColumnSpec>(StringComparer.Ordinal)
            {
                ["Id"] = new("TEXT", IsNullable: false, IsPrimaryKey: true),
                ["HistoryItemId"] = new("TEXT", IsNullable: false),
                ["Category"] = new("TEXT", IsNullable: false),
                ["RequiredInvalidationPathsJson"] = new("TEXT", IsNullable: false),
                ["Status"] = new("INTEGER", IsNullable: false),
                ["Attempts"] = new("INTEGER", IsNullable: false),
                ["CreatedAt"] = new("INTEGER", IsNullable: false),
                ["UpdatedAt"] = new("INTEGER", IsNullable: false),
                ["NextAttemptAt"] = new("INTEGER", IsNullable: false),
                ["LastAttemptAt"] = new("INTEGER", IsNullable: true),
                ["LeaseExpiresAt"] = new("INTEGER", IsNullable: true),
                ["LeaseToken"] = new("TEXT", IsNullable: true),
                ["VisibleAt"] = new("INTEGER", IsNullable: true),
                ["CompletedAt"] = new("INTEGER", IsNullable: true),
                ["ResultsJson"] = new("TEXT", IsNullable: false),
                ["LastError"] = new("TEXT", IsNullable: true)
            });
        await AssertIndexAsync(dbContext, "ArrImportCommands", "IX_ArrImportCommands_HistoryItemId", isUnique: true);
        await AssertIndexAsync(
            dbContext,
            "ArrImportCommands",
            "IX_ArrImportCommands_Status_LeaseExpiresAt",
            isUnique: false);
        await AssertIndexAsync(
            dbContext,
            "ArrImportCommands",
            "IX_ArrImportCommands_Status_NextAttemptAt_CreatedAt",
            isUnique: false);
        await AssertForeignKeyAsync(
            dbContext,
            "ArrImportCommands",
            fromColumn: "HistoryItemId",
            principalTable: "HistoryItems",
            principalColumn: "Id",
            onDelete: "CASCADE");
    }

    private static void AssertColumns(
        IReadOnlyDictionary<string, ColumnSpec> actual,
        IReadOnlyDictionary<string, ColumnSpec> expected)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, expectedColumn) in expected)
        {
            Assert.True(actual.TryGetValue(name, out var actualColumn));
            Assert.Equal(expectedColumn, actualColumn);
        }
    }

    private static async Task<bool> TableExistsAsync(DavDatabaseContext dbContext, string tableName)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<Dictionary<string, ColumnSpec>> ReadColumnsAsync(
        DavDatabaseContext dbContext,
        string tableName)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{EscapeIdentifier(tableName)}\");";

        var columns = new Dictionary<string, ColumnSpec>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var defaultOrdinal = reader.GetOrdinal("dflt_value");
            columns.Add(
                reader.GetString(reader.GetOrdinal("name")),
                new ColumnSpec(
                    reader.GetString(reader.GetOrdinal("type")),
                    IsNullable: reader.GetInt32(reader.GetOrdinal("notnull")) == 0,
                    DefaultValue: reader.IsDBNull(defaultOrdinal) ? null : reader.GetString(defaultOrdinal),
                    IsPrimaryKey: reader.GetInt32(reader.GetOrdinal("pk")) > 0));
        }

        return columns;
    }

    private static async Task AssertIndexAsync(
        DavDatabaseContext dbContext,
        string tableName,
        string indexName,
        bool isUnique)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{EscapeIdentifier(tableName)}\");";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!string.Equals(reader.GetString(reader.GetOrdinal("name")), indexName, StringComparison.Ordinal))
                continue;

            Assert.Equal(isUnique, reader.GetInt32(reader.GetOrdinal("unique")) == 1);
            return;
        }

        Assert.Fail($"Expected index '{indexName}' on table '{tableName}'.");
    }

    private static async Task AssertForeignKeyAsync(
        DavDatabaseContext dbContext,
        string tableName,
        string fromColumn,
        string principalTable,
        string principalColumn,
        string onDelete)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{EscapeIdentifier(tableName)}\");";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!string.Equals(reader.GetString(reader.GetOrdinal("from")), fromColumn, StringComparison.Ordinal))
                continue;

            Assert.Equal(principalTable, reader.GetString(reader.GetOrdinal("table")));
            Assert.Equal(principalColumn, reader.GetString(reader.GetOrdinal("to")));
            Assert.Equal(onDelete, reader.GetString(reader.GetOrdinal("on_delete")));
            return;
        }

        Assert.Fail($"Expected foreign key from '{tableName}.{fromColumn}'.");
    }

    private static async Task<string> ExecuteScalarStringAsync(DavDatabaseContext dbContext, string sql)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> ReadFirstColumnAsync(
        DavDatabaseContext dbContext,
        string sql)
    {
        await EnsureOpenAsync(dbContext);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return values;
    }

    private static async Task EnsureOpenAsync(DavDatabaseContext dbContext)
    {
        if (dbContext.Database.GetDbConnection().State != ConnectionState.Open)
            await dbContext.Database.OpenConnectionAsync();
    }

    private static string EscapeIdentifier(string identifier) => identifier.Replace("\"", "\"\"");

    private sealed record ColumnSpec(
        string Type,
        bool IsNullable,
        string? DefaultValue = null,
        bool IsPrimaryKey = false);

    private sealed class SqliteFileFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"nzbdav-sqlite-migration-{Guid.NewGuid():N}");

        public SqliteFileFixture()
        {
            Directory.CreateDirectory(_directory);
        }

        public DavDatabaseContext CreateContext()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "migration.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 30
            }.ToString();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>()
                .Options;
            return new DavDatabaseContext(options);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}

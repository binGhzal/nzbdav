using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

public sealed class PostgreSqlNativeModelTests
{
    private static readonly string[] ExpectedMigrationIds =
    [
        "20260712000000_PostgreSqlNativeBaseline",
        "20260712000100_PostgreSqlOperationalTriggers"
    ];

    [Fact]
    public void PostgreSqlOwnerHasExactlyTheNativeBaselineAndOperationalMigrations()
    {
        using var context = CreateContext();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();

        Assert.Equal(ExpectedMigrationIds, migrationsAssembly.Migrations.Keys);
        Assert.NotNull(migrationsAssembly.ModelSnapshot);
        Assert.All(
            migrationsAssembly.Migrations.Values,
            migration => Assert.Equal(
                typeof(PostgreSqlDavDatabaseContext),
                migration.GetCustomAttributes(typeof(DbContextAttribute), inherit: false)
                    .Cast<DbContextAttribute>()
                    .Single()
                    .ContextType));
    }

    [Fact]
    public void PostgreSqlMigrationsSeparateTheNativeSchemaFromOperationalTriggers()
    {
        using var context = CreateContext();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var provider = context.Database.ProviderName!;

        var baseline = migrationsAssembly.CreateMigration(
            migrationsAssembly.Migrations[ExpectedMigrationIds[0]], provider);
        Assert.Equal(28, baseline.UpOperations.OfType<CreateTableOperation>().Count());
        Assert.Equal(56, baseline.UpOperations.OfType<CreateIndexOperation>().Count());
        Assert.Single(baseline.UpOperations.OfType<InsertDataOperation>(), operation =>
            operation.Table == "DavItems" && operation.Values.GetLength(0) == 5);
        var baselineSql = string.Join('\n', baseline.UpOperations.OfType<SqlOperation>().Select(x => x.Sql));
        Assert.Contains("native baseline requires exact PostgreSQL 16.14", baselineSql, StringComparison.Ordinal);
        Assert.Contains("requires exact EF-created empty migration history", baselineSql, StringComparison.Ordinal);
        Assert.Contains("database.import-state", baselineSql, StringComparison.Ordinal);
        Assert.Contains("gen_random_uuid()", baselineSql, StringComparison.Ordinal);

        var operational = migrationsAssembly.CreateMigration(
            migrationsAssembly.Migrations[ExpectedMigrationIds[1]], provider);
        Assert.DoesNotContain(operational.UpOperations, operation => operation is CreateTableOperation);
        Assert.DoesNotContain(operational.UpOperations, operation => operation is CreateIndexOperation);
        var operationalSql = Assert.Single(operational.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Equal(9, CountOccurrences(operationalSql, "CREATE OR REPLACE FUNCTION"));
        Assert.Equal(9, CountOccurrences(operationalSql, "CREATE TRIGGER"));
        Assert.Equal(9, CountOccurrences(operationalSql, "REVOKE ALL ON FUNCTION"));
        Assert.Contains("OLD.\"CreatedAt\" IS DISTINCT FROM NEW.\"CreatedAt\"", operationalSql,
            StringComparison.Ordinal);

        var downSql = Assert.Single(operational.DownOperations.OfType<SqlOperation>()).Sql;
        Assert.Equal(9, CountOccurrences(downSql, "DROP TRIGGER"));
        Assert.Equal(9, CountOccurrences(downSql, "DROP FUNCTION"));
    }

    [Fact]
    public void PostgreSqlModelHasTheFrozenNativeShapeAndTypes()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entityTypes = model.GetEntityTypes().ToArray();
        var properties = entityTypes.SelectMany(entity => entity.GetProperties()).ToArray();

        Assert.Equal(28, entityTypes.Select(entity => entity.GetTableName()).Distinct().Count());
        Assert.Equal(240, properties.Length);
        Assert.Equal(28, entityTypes.Count(entity => entity.FindPrimaryKey() is not null));
        Assert.Equal(8, entityTypes.Sum(entity => entity.GetForeignKeys().Count()));
        Assert.Equal(56, entityTypes.Sum(entity => entity.GetIndexes().Count()));

        Assert.Equal(46, properties.Count(property => UnwrapNullable(property.ClrType) == typeof(Guid)));
        Assert.Equal(6, properties.Count(property => UnwrapNullable(property.ClrType) == typeof(bool)));
        Assert.Equal(47, properties.Count(property => property.GetMaxLength() is not null));

        Assert.Equal(
            46,
            properties.Count(property => UnwrapNullable(property.ClrType) == typeof(Guid)
                                         && property.GetColumnType() == "uuid"));
        Assert.Equal(
            6,
            properties.Count(property => UnwrapNullable(property.ClrType) == typeof(bool)
                                         && property.GetColumnType() == "boolean"));

        var defaults = properties
            .Where(property => property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is not null
                               || property.FindAnnotation(RelationalAnnotationNames.DefaultValueSql) is not null)
            .Select(property => $"{property.DeclaringType.ClrType.Name}.{property.Name}")
            .ToArray();
        Assert.Equal(["WorkerJob.LeaseGeneration"], defaults);

        var filteredIndex = entityTypes
            .SelectMany(entity => entity.GetIndexes())
            .Single(index => index.GetFilter() is not null);
        Assert.Equal("\"RepairStatus\" = 3", filteredIndex.GetFilter());
    }

    [Fact]
    public void PostgreSqlModelKeepsOnlyFourLocalWallTimestampsAndAllScalarUtcConverters()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var properties = model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).ToArray();

        var localWallProperties = properties
            .Where(property => UnwrapNullable(property.ClrType) == typeof(DateTime))
            .Select(property => $"{property.DeclaringType.ClrType.Name}.{property.Name}:{property.GetColumnType()}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "DavItem.CreatedAt:timestamp without time zone",
            "HistoryItem.CreatedAt:timestamp without time zone",
            "QueueItem.CreatedAt:timestamp without time zone",
            "QueueItem.PauseUntil:timestamp without time zone"
        ],
            localWallProperties);

        var dateTimeOffsetProperties = properties
            .Where(property => UnwrapNullable(property.ClrType) == typeof(DateTimeOffset))
            .ToArray();
        Assert.Equal(51, dateTimeOffsetProperties.Length);
        Assert.All(dateTimeOffsetProperties, property =>
        {
            var converter = property.GetValueConverter();
            Assert.NotNull(converter);
            Assert.Equal(typeof(long), UnwrapNullable(converter.ProviderClrType));
        });
    }

    [Fact]
    public void PostgreSqlModelUsesCOnlyForTheTwentyIndexedOrKeyStrings()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var collated = model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.GetCollation() is not null)
            .Select(property => $"{property.DeclaringType.ClrType.Name}.{property.Name}:{property.GetCollation()}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "Account.Username:C",
            "ArrDownloadCorrelation.ArrApp:C",
            "ArrDownloadCorrelation.DownloadId:C",
            "ArrDownloadCorrelation.InstanceKey:C",
            "ArrDownloadCorrelation.MediaKey:C",
            "ArrDownloadCorrelation.Source:C",
            "ArrDownloadLifecycleEvent.ArrApp:C",
            "ArrDownloadLifecycleEvent.InstanceKey:C",
            "ArrDownloadLifecycleEvent.State:C",
            "ArrSearchNudgeCommand.ArrApp:C",
            "ArrSearchNudgeCommand.CooldownKey:C",
            "ArrSearchNudgeCommand.InstanceKey:C",
            "ArrSearchNudgeCommand.Status:C",
            "ConfigItem.ConfigName:C",
            "DavItem.IdPrefix:C",
            "DavItem.Name:C",
            "HistoryItem.Category:C",
            "QueueItem.Category:C",
            "QueueItem.FileName:C",
            "RcloneInvalidationItem.Path:C"
        ],
            collated);
    }

    [Fact]
    public void PostgreSqlIndexNamesAreExplicitlyBounded()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var indexes = model.GetEntityTypes().SelectMany(entity => entity.GetIndexes()).ToArray();

        Assert.All(indexes, index => Assert.InRange(Encoding.UTF8.GetByteCount(index.GetDatabaseName()!), 1, 63));
        Assert.Contains(indexes, index => index.GetDatabaseName() == "IX_ArrLifecycle_Instance_State_CreatedAt");
        Assert.Contains(indexes, index => index.GetDatabaseName() == "IX_WorkerJobs_ClaimOrder");
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

    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        for (var offset = 0;;)
        {
            offset = value.IndexOf(expected, offset, StringComparison.Ordinal);
            if (offset < 0) return count;
            count++;
            offset += expected.Length;
        }
    }
}

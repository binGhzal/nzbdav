using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class ProviderContextBoundaryTests
{
    [Fact]
    public void DisabledPostgreSqlEntryPointsAreAssemblyInternal()
    {
        Assert.True(typeof(DavDatabaseContextRuntimeFactory).IsNotPublic);
        Assert.True(typeof(PostgreSqlDavDatabaseContext).IsNotPublic);
        Assert.True(typeof(PostgreSqlDavDatabaseContextFactory).IsNotPublic);
        Assert.True(typeof(PostgreSqlNativeMigrator).IsNotPublic);

        var constructors = typeof(PostgreSqlDavDatabaseContext).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsAssembly));
    }

    [Fact]
    public void RuntimeFactoryReturnsTheExactProviderOwner()
    {
        using (var environment = DatabaseProviderEnvironment.Create(null, null))
        using (var sqlite = DavDatabaseContextRuntimeFactory.Create())
            Assert.IsType<DavDatabaseContext>(sqlite);

        using (var environment = DatabaseProviderEnvironment.Create("sqlite", null))
        using (var sqlite = DavDatabaseContextRuntimeFactory.Create())
        {
            Assert.IsType<DavDatabaseContext>(sqlite);
            Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", sqlite.Database.ProviderName);
        }

        const string connectionString =
            "Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1";
        using (var environment = DatabaseProviderEnvironment.Create("postgres", connectionString))
        using (var postgres = DavDatabaseContextRuntimeFactory.Create())
        {
            Assert.IsType<PostgreSqlDavDatabaseContext>(postgres);
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", postgres.Database.ProviderName);
        }

        using (var environment = DatabaseProviderEnvironment.Create("postgresql", connectionString))
        using (var postgres = DavDatabaseContextRuntimeFactory.Create())
            Assert.IsType<PostgreSqlDavDatabaseContext>(postgres);
    }

    [Fact]
    public void SharedContextRetainsPublicTestOptionsAndProtectedSubtypeOptionsConstructors()
    {
        var publicTestConstructor = typeof(DavDatabaseContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(DbContextOptions<DavDatabaseContext>)],
            modifiers: null);
        var protectedSubtypeConstructor = typeof(DavDatabaseContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(DbContextOptions)],
            modifiers: null);

        Assert.NotNull(publicTestConstructor);
        Assert.NotNull(protectedSubtypeConstructor);
        Assert.True(protectedSubtypeConstructor.IsFamilyAndAssembly);
    }

    [Fact]
    public void ParameterlessSqliteOwnerFailsClosedForPostgreSqlWithoutReadingItsSecret()
    {
        using var environment = DatabaseProviderEnvironment.Create("postgres", null);

        var error = Assert.Throws<InvalidOperationException>(() => new DavDatabaseContext());

        Assert.Equal(DavDatabaseContext.SqliteOwnerProviderMismatchMessage, error.Message);
        Assert.DoesNotContain("NZBDAV_DATABASE_CONNECTION_STRING", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFactoryRejectsUnsupportedProvider()
    {
        using var environment = DatabaseProviderEnvironment.Create("cockroachdb", null);

        var error = Assert.Throws<InvalidOperationException>(DavDatabaseContextRuntimeFactory.Create);

        Assert.Equal(DavDatabaseContextRuntimeFactory.UnsupportedProviderMessage, error.Message);
    }

    [Fact]
    public void DesignFactoriesHaveIndependentProviderOwnership()
    {
        using var environment = DatabaseProviderEnvironment.Create(
            "postgres",
            "Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1");

        using var sqlite = new DavDatabaseContextFactory().CreateDbContext([]);
        using var postgres = new PostgreSqlDavDatabaseContextFactory().CreateDbContext([]);

        Assert.IsType<DavDatabaseContext>(sqlite);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", sqlite.Database.ProviderName);
        Assert.IsType<PostgreSqlDavDatabaseContext>(postgres);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", postgres.Database.ProviderName);
    }

    [Fact]
    public void ProviderOwnersHaveIndependentMigrationAndHistoryIdentities()
    {
        using var sqliteConnection = new SqliteConnection("Data Source=:memory:");
        var sqliteOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(sqliteConnection)
            .Options;
        using var sqlite = new DavDatabaseContext(sqliteOptions);

        var postgresOptions = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1",
                options => options.MigrationsHistoryTable(DatabaseMigrationPolicy.PostgreSqlHistoryTableName))
            .Options;
        using var postgres = new PostgreSqlDavDatabaseContext(postgresOptions);

        var sqliteAssembly = sqlite.GetService<IMigrationsAssembly>();
        var postgresAssembly = postgres.GetService<IMigrationsAssembly>();

        Assert.Equal(49, sqliteAssembly.Migrations.Count);
        Assert.Equal(SqliteContractTestSupport.LatestMigrationId, sqliteAssembly.Migrations.Keys.Last());
        Assert.NotNull(sqliteAssembly.ModelSnapshot);
        Assert.Equal(
        [
            "20260712000000_PostgreSqlNativeBaseline",
            "20260712000100_PostgreSqlOperationalTriggers"
        ],
            postgresAssembly.Migrations.Keys);
        Assert.NotNull(postgresAssembly.ModelSnapshot);

        Assert.Contains(
            DatabaseMigrationPolicy.SqliteHistoryTableName,
            sqlite.GetService<IHistoryRepository>().GetCreateScript(),
            StringComparison.Ordinal);
        Assert.Contains(
            DatabaseMigrationPolicy.PostgreSqlHistoryTableName,
            postgres.GetService<IHistoryRepository>().GetCreateScript(),
            StringComparison.Ordinal);
        Assert.NotSame(sqlite.Model, postgres.Model);
    }

    [Fact]
    public void ProviderOptionsKeepTheirInterceptorsAndMigrationGeneratorsSeparate()
    {
        using var environment = DatabaseProviderEnvironment.Create(
            "postgres",
            "Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1");
        using var sqlite = new DavDatabaseContextFactory().CreateDbContext([]);
        using var postgres = new PostgreSqlDavDatabaseContextFactory().CreateDbContext([]);

        var sqliteInterceptors = GetInterceptors(sqlite);
        Assert.Contains(sqliteInterceptors, interceptor => interceptor is SqliteForeignKeyEnabler);
        Assert.Contains(sqliteInterceptors, interceptor => interceptor is ContentIndexSnapshotInterceptor);
        Assert.Contains(sqliteInterceptors, interceptor => interceptor is DatabaseCommandTelemetryInterceptor);
        Assert.Contains(sqliteInterceptors, interceptor => interceptor is DatabaseTransactionTelemetryInterceptor);
        Assert.IsType<
            SqliteMigrationsSqlGenerator<Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>(
            sqlite.GetService<IMigrationsSqlGenerator>());

        var postgresInterceptors = GetInterceptors(postgres);
        Assert.DoesNotContain(postgresInterceptors, interceptor => interceptor is SqliteForeignKeyEnabler);
        Assert.Contains(postgresInterceptors, interceptor => interceptor is ContentIndexSnapshotInterceptor);
        Assert.Contains(postgresInterceptors, interceptor => interceptor is DatabaseCommandTelemetryInterceptor);
        Assert.Contains(postgresInterceptors, interceptor => interceptor is DatabaseTransactionTelemetryInterceptor);
        Assert.DoesNotContain(
            "SqliteMigrationsSqlGenerator",
            postgres.GetService<IMigrationsSqlGenerator>().GetType().FullName,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlModelOverridesOnlyTheFourLegacyLocalWallFields()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .Options;
        using var context = new PostgreSqlDavDatabaseContext(options);

        var legacyFields = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime)
                               || property.ClrType == typeof(DateTime?))
            .Select(property => new
            {
                Entity = property.DeclaringType.ClrType,
                property.Name,
                property.ClrType,
                ColumnType = property.GetColumnType()
            })
            .OrderBy(field => field.Entity.Name, StringComparer.Ordinal)
            .ThenBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Collection(
            legacyFields,
            field => AssertLegacyField(field.Entity, field.Name, field.ClrType, field.ColumnType,
                typeof(DavItem), nameof(DavItem.CreatedAt), typeof(DateTime)),
            field => AssertLegacyField(field.Entity, field.Name, field.ClrType, field.ColumnType,
                typeof(HistoryItem), nameof(HistoryItem.CreatedAt), typeof(DateTime)),
            field => AssertLegacyField(field.Entity, field.Name, field.ClrType, field.ColumnType,
                typeof(QueueItem), nameof(QueueItem.CreatedAt), typeof(DateTime)),
            field => AssertLegacyField(field.Entity, field.Name, field.ClrType, field.ColumnType,
                typeof(QueueItem), nameof(QueueItem.PauseUntil), typeof(DateTime?)));

        var dateTimeOffsetConverters = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTimeOffset)
                               || property.ClrType == typeof(DateTimeOffset?))
            .Count(property => property.GetValueConverter() is not null);
        Assert.Equal(51, dateTimeOffsetConverters);
    }

    [Fact]
    public void NpgsqlInfinityConversionSwitchIsSetByModuleInitialization()
    {
        Assert.True(AppContext.TryGetSwitch("Npgsql.DisableDateTimeInfinityConversions", out var disabled));
        Assert.True(disabled);
    }

    [Fact]
    public void ProductionConstructionAndMigrationCallsAreCentrallyRouted()
    {
        var backendRoot = SqliteContractTestSupport.AbsolutePath("backend");
        var approvedConstructorFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "DavDatabaseContextFactory.cs",
            "DavDatabaseContextRuntimeFactory.cs"
        };
        var approvedMigrationFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "DatabaseMigrator.cs",
            "PostgreSqlNativeMigrator.cs"
        };

        var constructorViolations = FindSourceViolations(
            backendRoot,
            approvedConstructorFiles,
            source => System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"new\s+(?:DavDatabaseContext|PostgreSqlDavDatabaseContext)\s*\(\s*\)"));
        var migrationViolations = FindSourceViolations(
            backendRoot,
            approvedMigrationFiles,
            source => System.Text.RegularExpressions.Regex.IsMatch(
                source,
                @"\.Database\s*\.\s*Migrate(?:Async)?\s*\("));

        Assert.Empty(constructorViolations);
        Assert.Empty(migrationViolations);

        var program = File.ReadAllText(Path.Combine(backendRoot, "Program.cs"));
        Assert.Contains(
            ".AddScoped<DavDatabaseContext>(_ => DavDatabaseContextRuntimeFactory.Create())",
            program,
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<IInterceptor> GetInterceptors(DbContext context)
    {
        var options = context.GetService<IDbContextOptions>();
        return (Assert.Single(options.Extensions.OfType<CoreOptionsExtension>()).Interceptors ?? []).ToArray();
    }

    private static void AssertLegacyField(
        Type actualEntity,
        string actualName,
        Type actualClrType,
        string? actualColumnType,
        Type expectedEntity,
        string expectedName,
        Type expectedClrType)
    {
        Assert.Equal(expectedEntity, actualEntity);
        Assert.Equal(expectedName, actualName);
        Assert.Equal(expectedClrType, actualClrType);
        Assert.Equal("timestamp without time zone", actualColumnType);
    }

    private static string[] FindSourceViolations(
        string backendRoot,
        IReadOnlySet<string> approvedFiles,
        Func<string, bool> isViolation)
    {
        return Directory.EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Database{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !approvedFiles.Contains(Path.GetFileName(path)))
            .Where(path => isViolation(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(backendRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class DatabaseProviderEnvironment : IDisposable
    {
        private readonly string? _provider;
        private readonly string? _connectionString;
        private readonly string? _legacyTimezone;

        private DatabaseProviderEnvironment(
            string? provider,
            string? connectionString,
            string? legacyTimezone)
        {
            _provider = provider;
            _connectionString = connectionString;
            _legacyTimezone = legacyTimezone;
        }

        internal static DatabaseProviderEnvironment Create(string? provider, string? connectionString)
        {
            var environment = new DatabaseProviderEnvironment(
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER"),
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING"),
                Environment.GetEnvironmentVariable("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE"));
            if (connectionString is not null
                && (string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase)))
            {
                connectionString = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timezone = TimeZoneInfo.Local.Id,
                    SearchPath = "nzbdav",
                    GssEncryptionMode = GssEncryptionMode.Disable
                }.ConnectionString;
            }
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", provider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", connectionString);
            Environment.SetEnvironmentVariable(
                "NZBDAV_LEGACY_TIMESTAMP_TIMEZONE",
                provider is "postgres" or "postgresql" ? TimeZoneInfo.Local.Id : null);
            return environment;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", _provider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", _connectionString);
            Environment.SetEnvironmentVariable("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE", _legacyTimezone);
        }
    }
}

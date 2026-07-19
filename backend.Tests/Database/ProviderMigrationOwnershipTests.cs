using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace backend.Tests.Database;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class ProviderMigrationOwnershipTests
{
    [Fact]
    public void ReviewedMigrationContractMatchesEveryWorkingTreeSourceAndHash()
    {
        var actual = SqliteContractTestSupport.CaptureMigrationContract();

        Assert.Equal(1, actual.FormatVersion);
        Assert.Equal(SqliteContractTestSupport.ContextName, actual.Context);
        Assert.Equal(SqliteContractTestSupport.HistoryTableName, actual.HistoryTable);
        Assert.Equal(SqliteContractTestSupport.SnapshotRelativePath, actual.Snapshot.Path);
        Assert.Equal(SqliteContractTestSupport.SnapshotTypeName, actual.Snapshot.Type);
        Assert.Equal(SqliteContractTestSupport.ContextName, actual.Snapshot.Context);
        Assert.Equal(48, actual.Migrations.Count);
        Assert.Equal(33, actual.Migrations.Count(migration => migration.MetadataLayout == "designer"));
        Assert.Equal(15, actual.Migrations.Count(migration => migration.MetadataLayout == "inline"));
        Assert.Equal(
            SqliteContractTestSupport.LatestMigrationId,
            actual.Migrations[^1].Id);
        Assert.Equal(
            actual.Migrations.Select(migration => migration.Id).Order(StringComparer.Ordinal),
            actual.Migrations.Select(migration => migration.Id));
        Assert.All(actual.Migrations, migration =>
        {
            Assert.True(File.Exists(SqliteContractTestSupport.AbsolutePath(migration.SourcePath)));
            Assert.True(File.Exists(SqliteContractTestSupport.AbsolutePath(migration.MetadataPath)));
            Assert.Matches("^[0-9a-f]{64}$", migration.UpThroughEofSha256);
            if (migration.MetadataLayout == "designer")
                Assert.Matches("^[0-9a-f]{64}$", Assert.IsType<string>(migration.BuildTargetModelSha256));
            else
                Assert.Null(migration.BuildTargetModelSha256);
        });

        var fixturePath = SqliteContractTestSupport.AbsolutePath(
            SqliteContractTestSupport.MigrationContractRelativePath);
        var actualBytes = SqliteContractTestSupport.SerializeCanonical(actual);
        if (!File.Exists(fixturePath))
        {
            SqliteContractTestSupport.WriteMissingFixtureDiagnostic(
                "nzbdav-sqlite-migration-contract.json",
                actualBytes);
        }

        Assert.Equal(File.ReadAllBytes(fixturePath), actualBytes);
    }

    [Fact]
    public void MigrationAssemblyOwnsTheExactReviewedChainAndSnapshot()
    {
        var contract = SqliteContractTestSupport.ReadFixture<SqliteMigrationContract>(
            SqliteContractTestSupport.MigrationContractRelativePath);
        using var database = new SqliteContractDatabase();
        using var context = database.CreateContext();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var assemblyMigrations = migrationsAssembly.Migrations
            .OrderBy(migration => migration.Key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            contract.Migrations.Select(migration => migration.Id),
            assemblyMigrations.Select(migration => migration.Key));
        Assert.Equal(48, assemblyMigrations.Length);
        Assert.Equal(SqliteContractTestSupport.LatestMigrationId, assemblyMigrations[^1].Key);

        foreach (var (expected, actual) in contract.Migrations.Zip(assemblyMigrations))
        {
            var migrationType = actual.Value.AsType();
            Assert.Equal(expected.ClassName, migrationType.Name);
            Assert.Equal(
                expected.Id,
                migrationType.GetCustomAttribute<MigrationAttribute>()?.Id);
            Assert.Equal(
                typeof(DavDatabaseContext),
                migrationType.GetCustomAttribute<DbContextAttribute>()?.ContextType);
        }

        var snapshot = Assert.IsAssignableFrom<ModelSnapshot>(migrationsAssembly.ModelSnapshot);
        Assert.Equal(contract.Snapshot.Type, snapshot.GetType().FullName);
        Assert.Equal(
            typeof(DavDatabaseContext),
            snapshot.GetType().GetCustomAttribute<DbContextAttribute>()?.ContextType);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void MetadataLayoutHasNoMissingOrUnexpectedMigrationCarrierFiles()
    {
        var contract = SqliteContractTestSupport.ReadFixture<SqliteMigrationContract>(
            SqliteContractTestSupport.MigrationContractRelativePath);
        var migrationsDirectory = SqliteContractTestSupport.AbsolutePath("backend/Database/Migrations");

        var actualPrimarySources = Directory.EnumerateFiles(migrationsDirectory, "*.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null
                           && name.Length > 18
                           && name[..14].All(char.IsDigit)
                           && !name.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(name => $"backend/Database/Migrations/{name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualDesignerSources = Directory.EnumerateFiles(migrationsDirectory, "*.Designer.cs")
            .Select(path => $"backend/Database/Migrations/{Path.GetFileName(path)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            contract.Migrations.Select(migration => migration.SourcePath).Order(StringComparer.Ordinal),
            actualPrimarySources);
        Assert.Equal(
            contract.Migrations.Where(migration => migration.MetadataLayout == "designer")
                .Select(migration => migration.MetadataPath).Order(StringComparer.Ordinal),
            actualDesignerSources);
        Assert.Equal(33, actualDesignerSources.Length);
        Assert.Equal(15, contract.Migrations.Count(migration => migration.MetadataLayout == "inline"));

        foreach (var migration in contract.Migrations)
        {
            var metadataSource = File.ReadAllText(
                SqliteContractTestSupport.AbsolutePath(migration.MetadataPath));
            Assert.Contains($"[Migration(\"{migration.Id}\")]", metadataSource, StringComparison.Ordinal);
            Assert.Contains("[DbContext(typeof(DavDatabaseContext))]", metadataSource, StringComparison.Ordinal);

            if (migration.MetadataLayout == "designer")
                Assert.NotEqual(migration.SourcePath, migration.MetadataPath);
            else
                Assert.Equal(migration.SourcePath, migration.MetadataPath);
        }

        var snapshotSource = File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(contract.Snapshot.Path));
        Assert.Contains("[DbContext(typeof(DavDatabaseContext))]", snapshotSource, StringComparison.Ordinal);
        Assert.Contains("partial class DavDatabaseContextModelSnapshot : ModelSnapshot", snapshotSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSqliteOptionsRetainProviderInterceptorHistoryAndMigrationGenerator()
    {
        using var environment = SqliteEnvironmentScope.Create();
        using var context = new DavDatabaseContext();

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
        var connection = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal(Path.Combine(environment.ConfigPath, "db.sqlite"), connection.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, connection.Mode);
        Assert.Equal(SqliteCacheMode.Private, connection.Cache);
        Assert.True(connection.Pooling);
        Assert.Equal(30, connection.DefaultTimeout);

        var options = context.GetService<IDbContextOptions>();
        var coreOptions = Assert.Single(options.Extensions.OfType<CoreOptionsExtension>());
        var interceptors = coreOptions.Interceptors ?? [];
        Assert.Contains(interceptors, interceptor => interceptor is SqliteForeignKeyEnabler);
        Assert.Contains(interceptors, interceptor => interceptor is ContentIndexSnapshotInterceptor);
        Assert.Contains(interceptors, interceptor => interceptor is DatabaseCommandTelemetryInterceptor);
        Assert.Contains(interceptors, interceptor => interceptor is DatabaseTransactionTelemetryInterceptor);
        Assert.IsType<
            SqliteMigrationsSqlGenerator<Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>(
            context.GetService<IMigrationsSqlGenerator>());

        var historyScript = context.GetService<IHistoryRepository>().GetCreateScript();
        Assert.Contains(SqliteContractTestSupport.HistoryTableName, historyScript, StringComparison.Ordinal);
    }

    private sealed class SqliteEnvironmentScope : IDisposable
    {
        private readonly string? _configPath;
        private readonly string? _provider;
        private readonly string? _connectionString;
        private readonly string _temporaryConfigPath;

        private SqliteEnvironmentScope(
            string? configPath,
            string? provider,
            string? connectionString,
            string temporaryConfigPath)
        {
            _configPath = configPath;
            _provider = provider;
            _connectionString = connectionString;
            _temporaryConfigPath = temporaryConfigPath;
        }

        internal static SqliteEnvironmentScope Create()
        {
            var temporaryConfigPath = Path.Combine(
                Path.GetTempPath(),
                $"nzbdav-sqlite-options-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryConfigPath);
            var scope = new SqliteEnvironmentScope(
                Environment.GetEnvironmentVariable("CONFIG_PATH"),
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER"),
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING"),
                temporaryConfigPath);
            Environment.SetEnvironmentVariable("CONFIG_PATH", temporaryConfigPath);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", "sqlite");
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", null);
            return scope;
        }

        internal string ConfigPath => _temporaryConfigPath;

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", _provider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", _connectionString);
            Directory.Delete(_temporaryConfigPath, recursive: true);
        }
    }
}

[CollectionDefinition(nameof(SqliteMigrationContractEnvironmentCollection), DisableParallelization = true)]
public sealed class SqliteMigrationContractEnvironmentCollection;

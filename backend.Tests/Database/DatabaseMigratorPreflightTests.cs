using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.MigrationHelpers;

namespace backend.Tests.Database;

public sealed class DatabaseMigratorPreflightTests
{
    [Fact]
    public void NativePostgreSqlMigratorExistsOnlyAsAnAssemblyInternalEntryPoint()
    {
        var migratorType = typeof(DatabaseMigrator).Assembly.GetType(
            "NzbWebDAV.Database.PostgreSqlNativeMigrator",
            throwOnError: false);

        Assert.NotNull(migratorType);
        Assert.False(migratorType.IsPublic);
        Assert.DoesNotContain(
            migratorType.GetMethods(System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.Static
                                    | System.Reflection.BindingFlags.Instance),
            method => method.DeclaringType == migratorType);
    }

    [Fact]
    public async Task NativePostgreSqlCleanupFailureCannotMaskPrimaryFailure()
    {
        var primary = new InvalidOperationException("primary migration failure");
        var cleanup = new InvalidOperationException("advisory unlock failure");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlNativeMigrator.RunWithCleanupAsync(
                () => Task.FromException(primary),
                () => Task.FromException(cleanup)));

        Assert.Same(primary, actual);
        Assert.Same(cleanup, actual.Data[PostgreSqlNativeMigrator.CleanupFailureDataKey]);
    }

    [Fact]
    public async Task NativePostgreSqlCleanupFailureSurfacesAfterSuccessfulOperation()
    {
        var cleanup = new InvalidOperationException("advisory unlock failure");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlNativeMigrator.RunWithCleanupAsync(
                () => Task.CompletedTask,
                () => Task.FromException(cleanup)));

        Assert.Same(cleanup, actual);
    }

    [Fact]
    public async Task ExactSqliteOwnerCanApplyTheReviewedMigrationChain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>()
            .Options;
        await using var context = new DavDatabaseContext(options);

        await DatabaseMigrator.MigrateAsync(context, cancellationToken: CancellationToken.None);

        Assert.Equal(48, (await context.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task PostgreSqlOwnerRefusesBeforeConnectionAccess()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .AddInterceptors(connectionGuard)
            .Options;
        await using var context = new PostgreSqlDavDatabaseContext(options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseMigrator.MigrateAsync(
                context,
                cancellationToken: CancellationToken.None));

        Assert.Equal(DatabaseMigrator.PostgreSqlRefusalMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
    }

    [Fact]
    public void NpgsqlConfiguredSqliteOwnerRefusesDuringConstructionBeforeConnectionAccess()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .AddInterceptors(connectionGuard)
            .Options;
        var error = Assert.Throws<InvalidOperationException>(() => new DavDatabaseContext(options));

        Assert.Equal(DavDatabaseContext.SqliteOwnerProviderMismatchMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
    }

    [Fact]
    public void PostgreSqlOwnerRejectsSqliteOptionsDuringConstructionBeforeConnectionAccess()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .AddInterceptors(connectionGuard)
            .Options;

        var error = Assert.Throws<InvalidOperationException>(
            () => new PostgreSqlDavDatabaseContext(options));

        Assert.Equal(PostgreSqlDavDatabaseContext.ProviderMismatchMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
    }

    [Fact]
    public void PostgreSqlOwnerRejectsMissingProviderOptionsDuringConstruction()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>().Options;

        var error = Assert.Throws<InvalidOperationException>(
            () => new PostgreSqlDavDatabaseContext(options));

        Assert.Equal(PostgreSqlDavDatabaseContext.ProviderMismatchMessage, error.Message);
    }

    [Fact]
    public void PostgreSqlOwnerRejectsMixedProviderOptionsDuringConstructionBeforeConnectionAccess()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .AddInterceptors(connectionGuard)
            .Options;

        var error = Assert.Throws<InvalidOperationException>(
            () => new PostgreSqlDavDatabaseContext(options));

        Assert.Equal(PostgreSqlDavDatabaseContext.ProviderMismatchMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
    }

    [Fact]
    public async Task PostgreSqlLegacyTransferV2ImportRefusesBeforeOpeningInputOrDatabase()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .AddInterceptors(connectionGuard)
            .Options;
        await using var context = new PostgreSqlDavDatabaseContext(options);
        var missingInput = Path.Combine(Path.GetTempPath(), $"missing-transfer-{Guid.NewGuid():N}.json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseTransferService.ImportJsonAsync(
                context,
                missingInput,
                replace: true,
                CancellationToken.None));

        Assert.Equal(DatabaseTransferService.PostgreSqlLegacyImportRefusalMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
    }

    [Fact]
    public async Task PostgreSqlLegacyTransferV2ExportRefusesBeforePathOrDatabaseAccess()
    {
        var connectionGuard = new ConnectionOpeningGuard();
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;Password=not-used;Timeout=1")
            .AddInterceptors(connectionGuard)
            .Options;
        await using var context = new PostgreSqlDavDatabaseContext(options);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"missing-transfer-output-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(outputDirectory, "snapshot.json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseTransferService.ExportJsonAsync(
                context,
                outputPath,
                CancellationToken.None));

        Assert.Equal(DatabaseTransferService.PostgreSqlLegacyExportRefusalMessage, error.Message);
        Assert.Equal(0, connectionGuard.OpeningCount);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(outputPath));
    }

    private sealed class ConnectionOpeningGuard : DbConnectionInterceptor
    {
        public int OpeningCount { get; private set; }

        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            OpeningCount++;
            throw new InvalidOperationException("A refused migration attempted to open a database connection.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            OpeningCount++;
            throw new InvalidOperationException("A refused migration attempted to open a database connection.");
        }
    }
}

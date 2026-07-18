using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

internal sealed class PostgreSqlDavDatabaseContext : DavDatabaseContext
{
    internal const string ProviderMismatchMessage =
        "PostgreSqlDavDatabaseContext requires exactly one Npgsql database-provider extension.";

    internal PostgreSqlDavDatabaseContext() : base(CreatePostgreSqlOptions())
    {
    }

    internal PostgreSqlDavDatabaseContext(DbContextOptions<PostgreSqlDavDatabaseContext> options)
        : base(ValidatePostgreSqlOwnerOptions(options))
    {
    }

    internal static DbContextOptions<PostgreSqlDavDatabaseContext> CreatePostgreSqlOptions()
    {
        var connectionString = PostgreSqlConnectionPolicy.ValidateConnectionString(
            EnvironmentUtil.GetRequiredVariable("NZBDAV_DATABASE_CONNECTION_STRING"));
        var targetSchema = PostgreSqlEnvironmentContract.GetRequiredTargetSchema(connectionString);
        return new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(
                    DatabaseMigrationPolicy.PostgreSqlHistoryTableName,
                    targetSchema))
            .AddInterceptors(
                new ContentIndexSnapshotInterceptor(),
                new DatabaseCommandTelemetryInterceptor(DatabaseTelemetry.Shared),
                new DatabaseTransactionTelemetryInterceptor(DatabaseTelemetry.Shared))
            .Options;
    }

    private static DbContextOptions<PostgreSqlDavDatabaseContext> ValidatePostgreSqlOwnerOptions(
        DbContextOptions<PostgreSqlDavDatabaseContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var databaseProviderExtensions = options.Extensions
            .Where(extension => extension.Info.IsDatabaseProvider)
            .ToArray();
        if (databaseProviderExtensions.Length != 1
            || !string.Equals(
                databaseProviderExtensions[0].GetType().Assembly.GetName().Name,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
            throw new InvalidOperationException(ProviderMismatchMessage);

        return options;
    }

    protected override void ConfigureProviderModel(ModelBuilder modelBuilder)
    {
        PostgreSqlModelConfiguration.Configure(modelBuilder);
    }
}

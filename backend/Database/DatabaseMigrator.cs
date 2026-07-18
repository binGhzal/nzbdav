using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

public static class DatabaseMigrator
{
    public const string PostgreSqlRefusalMessage =
        "PostgreSQL provider-native migrations are unavailable through the public migrator " +
        "until the transfer and promotion gates pass.";

    public const string SqliteOwnerProviderMismatchMessage =
        "Database migration refused: DavDatabaseContext owns only the SQLite migration chain and " +
        "cannot migrate a different provider.";

    public static Task MigrateAsync(
        DavDatabaseContext context,
        string? targetMigration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetType() != typeof(DavDatabaseContext))
            throw new InvalidOperationException(PostgreSqlRefusalMessage);

        if (!string.Equals(
                context.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
            throw new InvalidOperationException(SqliteOwnerProviderMismatchMessage);

        return context.Database.MigrateAsync(targetMigration, cancellationToken);
    }
}

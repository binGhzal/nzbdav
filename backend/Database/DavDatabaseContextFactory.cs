using Microsoft.EntityFrameworkCore.Design;

namespace NzbWebDAV.Database;

/// <summary>
/// Allows EF Core design-time tools (dotnet-ef) to create a DavDatabaseContext
/// without bootstrapping the full application host (Program.cs).
/// </summary>
public class DavDatabaseContextFactory : IDesignTimeDbContextFactory<DavDatabaseContext>
{
    public DavDatabaseContext CreateDbContext(string[] args)
    {
        return new DavDatabaseContext(
            DavDatabaseContext.CreateSqliteOptions(enforceProviderSelection: false));
    }
}

/// <summary>
/// Creates the PostgreSQL migration owner for explicit design-time commands.
/// Normal runtime startup remains refused until its native baseline is installed.
/// </summary>
internal sealed class PostgreSqlDavDatabaseContextFactory
    : IDesignTimeDbContextFactory<PostgreSqlDavDatabaseContext>
{
    public PostgreSqlDavDatabaseContext CreateDbContext(string[] args)
    {
        return new PostgreSqlDavDatabaseContext(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions());
    }
}

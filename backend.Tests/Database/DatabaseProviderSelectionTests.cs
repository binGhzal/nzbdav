using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DatabaseProviderSelectionTests
{
    [Fact]
    public void DavDatabaseContext_UsesSqliteByDefault()
    {
        using var env = DatabaseEnvironmentScope.Create(provider: null, connectionString: null);
        using var dbContext = new DavDatabaseContext();

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", dbContext.Database.ProviderName);
        Assert.EndsWith("db.sqlite", DavDatabaseContext.DatabaseFilePath);
    }

    [Fact]
    public void DavDatabaseContext_UsesPrivateSqliteCache()
    {
        using var env = DatabaseEnvironmentScope.Create(provider: null, connectionString: null);
        using var dbContext = new DavDatabaseContext();

        var builder = new SqliteConnectionStringBuilder(dbContext.Database.GetConnectionString());

        Assert.Equal(SqliteCacheMode.Private, builder.Cache);
        Assert.Equal(30, builder.DefaultTimeout);
    }

    [Fact]
    public void SqliteMigrationSnapshotMatchesTheRuntimeModel()
    {
        using var env = DatabaseEnvironmentScope.Create(provider: null, connectionString: null);
        using var dbContext = new DavDatabaseContext();

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public void DavDatabaseContext_RefusesPostgresBecauseItOwnsOnlySqliteMigrations()
    {
        const string connectionString = "Host=localhost;Port=5432;Database=nzbdav;Username=nzbdav;Password=secret";
        using var env = DatabaseEnvironmentScope.Create("postgres", connectionString);
        var error = Assert.Throws<InvalidOperationException>(() => new DavDatabaseContext());

        Assert.Equal(DavDatabaseContext.SqliteOwnerProviderMismatchMessage, error.Message);
    }

    private sealed class DatabaseEnvironmentScope : IDisposable
    {
        private readonly string? _databaseProvider;
        private readonly string? _databaseConnectionString;

        private DatabaseEnvironmentScope(string? databaseProvider, string? databaseConnectionString)
        {
            _databaseProvider = databaseProvider;
            _databaseConnectionString = databaseConnectionString;
        }

        public static DatabaseEnvironmentScope Create(string? provider, string? connectionString)
        {
            var scope = new DatabaseEnvironmentScope(
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER"),
                Environment.GetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING"));
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", provider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", connectionString);
            return scope;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", _databaseProvider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", _databaseConnectionString);
        }
    }
}

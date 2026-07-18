using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Interceptors;

namespace backend.Tests.Database;

public sealed class SqliteConnectionPolicyTests
{
    [Fact]
    public void SynchronousOpenEnforcesWalFullAndSafetyPragmas()
    {
        using var fixture = new PolicyContextFixture();
        using var context = fixture.CreateContext();

        context.Database.OpenConnection();

        AssertPolicy(context.Database.GetDbConnection());
    }

    [Fact]
    public async Task AsynchronousOpenEnforcesWalFullAndSafetyPragmas()
    {
        using var fixture = new PolicyContextFixture();
        await using var context = fixture.CreateContext();

        await context.Database.OpenConnectionAsync();

        AssertPolicy(context.Database.GetDbConnection());
    }

    private static void AssertPolicy(System.Data.Common.DbConnection connection)
    {
        Assert.Equal("wal", ReadPragma(connection, "journal_mode"));
        Assert.Equal("2", ReadPragma(connection, "synchronous"));
        Assert.Equal("1", ReadPragma(connection, "foreign_keys"));
        Assert.Equal("30000", ReadPragma(connection, "busy_timeout"));
    }

    private static string ReadPragma(System.Data.Common.DbConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options);

    private sealed class PolicyContextFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"nzbdav-sqlite-policy-{Guid.NewGuid():N}");

        public PolicyContextFixture()
        {
            Directory.CreateDirectory(_directory);
        }

        public PolicyContext CreateContext()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "policy.sqlite"),
                Pooling = false,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 30
            }.ToString();
            var options = new DbContextOptionsBuilder<PolicyContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .Options;
            return new PolicyContext(options);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

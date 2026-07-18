using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

internal sealed class PostgreSqlTestSchema : IAsyncDisposable
{
    private readonly NpgsqlConnection _adminConnection;

    private PostgreSqlTestSchema(
        NpgsqlConnection adminConnection,
        string schemaName,
        string connectionString)
    {
        _adminConnection = adminConnection;
        SchemaName = schemaName;
        ConnectionString = connectionString;
    }

    internal string SchemaName { get; }
    internal string ConnectionString { get; }

    internal static async Task<PostgreSqlTestSchema> CreateAsync(string prefix)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.TestConnectionStringVariable);
        Assert.False(string.IsNullOrWhiteSpace(baseConnectionString));

        var adminBuilder = BuildCompliantConnectionString(baseConnectionString);
        adminBuilder.SearchPath = null;
        var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        var schemaName = $"{prefix}_{Guid.NewGuid():N}";
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schemaName}\"");
        var schemaBuilder = BuildCompliantConnectionString(baseConnectionString);
        schemaBuilder.SearchPath = schemaName;
        return new PostgreSqlTestSchema(adminConnection, schemaName, schemaBuilder.ConnectionString);
    }

    internal static NpgsqlConnectionStringBuilder BuildCompliantConnectionString(string connectionString)
    {
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timezone = TimeZoneInfo.Local.Id,
            GssEncryptionMode = GssEncryptionMode.Disable
        };
    }

    internal async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    internal DbContextOptions<PostgreSqlDavDatabaseContext> CreateOptions(
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                ConnectionString,
                postgres => postgres.MigrationsHistoryTable(
                    DatabaseMigrationPolicy.PostgreSqlHistoryTableName,
                    SchemaName));
        if (interceptors.Length != 0) builder.AddInterceptors(interceptors);
        return builder.Options;
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteAsync(connection, sql);
    }

    internal async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecuteAsync(
                _adminConnection,
                $"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE");
        }
        finally
        {
            // Every test schema has a distinct Search Path and therefore a
            // distinct Npgsql pool key. Evict its idle physical connections so
            // a long PG-first run cannot exhaust max_connections with one idle
            // pool per completed schema.
            using var poolKey = new NpgsqlConnection(ConnectionString);
            NpgsqlConnection.ClearPool(poolKey);
            await _adminConnection.DisposeAsync();
        }
    }
}

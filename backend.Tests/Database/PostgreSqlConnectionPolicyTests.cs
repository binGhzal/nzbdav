using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class PostgreSqlConnectionPolicyTests
{
    [Fact]
    public void FactoryRefusesMissingLegacyTimezoneContract()
    {
        using var environment = PostgreSqlEnvironment.Create(
            ConnectionString(timezone: TimeZoneInfo.Local.Id, disableGss: true),
            legacyTimezone: null);

        var error = Assert.Throws<InvalidOperationException>(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions);

        Assert.Contains("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryRefusesLegacyTimezoneThatIsNotTheProcessLocalId()
    {
        var mismatch = TimeZoneInfo.Local.Id == "Etc/UTC" ? "Asia/Dubai" : "Etc/UTC";
        using var environment = PostgreSqlEnvironment.Create(
            ConnectionString(timezone: mismatch, disableGss: true),
            legacyTimezone: mismatch);

        var error = Assert.Throws<InvalidOperationException>(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions);

        Assert.Contains(TimeZoneInfo.Local.Id, error.Message, StringComparison.Ordinal);
        Assert.Contains(mismatch, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryRefusesConnectionWithoutAnExplicitMatchingTimezone()
    {
        using var environment = PostgreSqlEnvironment.Create(
            ConnectionString(timezone: null, disableGss: true),
            legacyTimezone: TimeZoneInfo.Local.Id);

        var error = Assert.Throws<InvalidOperationException>(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions);

        Assert.Contains("Timezone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryRefusesDisposablePasswordConnectionUnlessGssIsExplicitlyDisabled()
    {
        using var environment = PostgreSqlEnvironment.Create(
            ConnectionString(timezone: TimeZoneInfo.Local.Id, disableGss: false),
            legacyTimezone: TimeZoneInfo.Local.Id);

        var error = Assert.Throws<InvalidOperationException>(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions);

        Assert.Contains("Gss Encryption Mode=Disable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryRefusesConnectionWithoutOneExplicitTargetSchema()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            ConnectionString(timezone: TimeZoneInfo.Local.Id, disableGss: true))
        {
            SearchPath = null
        };
        using var environment = PostgreSqlEnvironment.Create(
            builder.ConnectionString,
            legacyTimezone: TimeZoneInfo.Local.Id);

        var error = Assert.Throws<InvalidOperationException>(
            PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions);

        Assert.Contains("single target schema", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryPreservesACompliantConnectionContractWithoutSilentlyRewritingIt()
    {
        var connectionString = ConnectionString(
            timezone: TimeZoneInfo.Local.Id,
            disableGss: true);
        using var environment = PostgreSqlEnvironment.Create(
            connectionString,
            legacyTimezone: TimeZoneInfo.Local.Id);

        var options = PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions();
        using var context = new PostgreSqlDavDatabaseContext(options);
        var actual = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());

        Assert.Equal(TimeZoneInfo.Local.Id, actual.Timezone);
        Assert.Equal(GssEncryptionMode.Disable, actual.GssEncryptionMode);
    }

    [Fact]
    public void FactoryDoesNotSilentlyDisableAnExplicitFuturePasswordlessGssContract()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "nzbdav",
            Username = "nzbdav",
            Timeout = 1,
            Timezone = TimeZoneInfo.Local.Id,
            SearchPath = "nzbdav",
            GssEncryptionMode = GssEncryptionMode.Require
        };
        using var environment = PostgreSqlEnvironment.Create(
            builder.ConnectionString,
            legacyTimezone: TimeZoneInfo.Local.Id);

        var options = PostgreSqlDavDatabaseContext.CreatePostgreSqlOptions();
        using var context = new PostgreSqlDavDatabaseContext(options);
        var actual = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());

        Assert.Equal(GssEncryptionMode.Require, actual.GssEncryptionMode);
        Assert.True(string.IsNullOrEmpty(actual.Password));
    }

    private static string ConnectionString(string? timezone, bool disableGss)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "nzbdav",
            Username = "nzbdav",
            Password = "not-used",
            SearchPath = "nzbdav",
            Timeout = 1
        };
        if (timezone is not null) builder.Timezone = timezone;
        if (disableGss) builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    private sealed class PostgreSqlEnvironment : IDisposable
    {
        private readonly string? _oldProvider;
        private readonly string? _oldConnectionString;
        private readonly string? _oldLegacyTimezone;

        private PostgreSqlEnvironment(string connectionString, string? legacyTimezone)
        {
            _oldProvider = Environment.GetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER");
            _oldConnectionString = Environment.GetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING");
            _oldLegacyTimezone = Environment.GetEnvironmentVariable("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE");
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", "postgres");
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", connectionString);
            Environment.SetEnvironmentVariable("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE", legacyTimezone);
        }

        internal static PostgreSqlEnvironment Create(string connectionString, string? legacyTimezone) =>
            new(connectionString, legacyTimezone);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", _oldProvider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", _oldConnectionString);
            Environment.SetEnvironmentVariable("NZBDAV_LEGACY_TIMESTAMP_TIMEZONE", _oldLegacyTimezone);
        }
    }
}

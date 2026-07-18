using Npgsql;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

internal static class PostgreSqlConnectionPolicy
{
    internal const string LegacyTimezoneVariable = "NZBDAV_LEGACY_TIMESTAMP_TIMEZONE";

    internal static string ValidateConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var configuredTimezone = EnvironmentUtil.GetVariable(LegacyTimezoneVariable);
        if (string.IsNullOrWhiteSpace(configuredTimezone))
            throw new InvalidOperationException(
                $"PostgreSQL is disabled until {LegacyTimezoneVariable} is explicitly set to the process-local timezone '{TimeZoneInfo.Local.Id}'.");

        var localTimezone = TimeZoneInfo.Local.Id;
        if (!string.Equals(configuredTimezone, localTimezone, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{LegacyTimezoneVariable} '{configuredTimezone}' must exactly equal the process-local timezone '{localTimezone}'. Timezone aliases are not accepted.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey("Timezone")
            || !string.Equals(builder.Timezone, localTimezone, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PostgreSQL connection Timezone must be explicitly set to '{localTimezone}'.");

        var passwordAuthenticationConfigured =
            builder.ContainsKey("Password") && !string.IsNullOrEmpty(builder.Password);
        if (passwordAuthenticationConfigured)
            ValidateDisposablePasswordContract(builder);

        return builder.ConnectionString;
    }

    internal static void ValidateDisposableTestConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ValidateDisposablePasswordContract(new NpgsqlConnectionStringBuilder(connectionString));
    }

    internal static async Task ValidateSessionTimezoneAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("PostgreSQL timezone validation requires an open connection.");

        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW TimeZone";
        var sessionTimezone = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(sessionTimezone, TimeZoneInfo.Local.Id, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PostgreSQL session TimeZone '{sessionTimezone}' must exactly equal the process-local timezone '{TimeZoneInfo.Local.Id}'.");
    }

    private static void ValidateDisposablePasswordContract(NpgsqlConnectionStringBuilder builder)
    {
        if (!builder.ContainsKey("GSS Encryption Mode")
            || builder.GssEncryptionMode != GssEncryptionMode.Disable)
            throw new InvalidOperationException(
                "Disposable password/SCRAM PostgreSQL connections must explicitly set Gss Encryption Mode=Disable; the application never rewrites this setting.");
    }
}

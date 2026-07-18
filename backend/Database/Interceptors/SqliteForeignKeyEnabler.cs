using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

public class SqliteForeignKeyEnabler : DbConnectionInterceptor
{
    private static readonly string[] ConnectionPragmas =
    [
        "PRAGMA foreign_keys = ON;",
        "PRAGMA synchronous = FULL;"
    ];

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 30000;");
        SetAndValidateWalMode(connection);
        foreach (var pragma in ConnectionPragmas)
            ExecuteNonQuery(connection, pragma);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 30000;", cancellationToken)
            .ConfigureAwait(false);
        await SetAndValidateWalModeAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var pragma in ConnectionPragmas)
            await ExecuteNonQueryAsync(connection, pragma, cancellationToken).ConfigureAwait(false);
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void SetAndValidateWalMode(DbConnection connection)
    {
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "PRAGMA journal_mode;";
            if (IsWalMode(query.ExecuteScalar())) return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        ValidateJournalMode(command.ExecuteScalar());
    }

    private static async Task SetAndValidateWalModeAsync(DbConnection connection, CancellationToken ct)
    {
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "PRAGMA journal_mode;";
            if (IsWalMode(await query.ExecuteScalarAsync(ct).ConfigureAwait(false))) return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        ValidateJournalMode(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static bool IsWalMode(object? value) =>
        string.Equals(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            "wal",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateJournalMode(object? value)
    {
        var mode = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!IsWalMode(value))
        {
            throw new InvalidOperationException(
                $"SQLite refused WAL journal mode and returned '{mode ?? "<null>"}'.");
        }
    }
}

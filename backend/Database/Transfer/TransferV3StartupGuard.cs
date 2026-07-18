using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3StartupGuard
{
    internal const string RefusalMessage =
        "NZBDav refuses normal startup and legacy maintenance while a transfer-v3 import-state marker is present.";

    internal const string ValidationFailureMessage =
        "NZBDav could not safely validate the SQLite transfer-v3 startup boundary.";

    internal static readonly TimeSpan MaximumElapsed = TimeSpan.FromSeconds(5);

    private const int SqliteTimeoutSeconds = 1;
    private static readonly TimeSpan InternalOperationBudget = TimeSpan.FromSeconds(4);
    private static readonly byte[] ImportStateKeyBytes = Encoding.UTF8.GetBytes(
        TransferV3ReservedConfigPolicy.ImportStateKey);

    internal static async Task EnsureAllowedAsync(string databasePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databasePath))
            return;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(InternalOperationBudget);

        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = SqliteTimeoutSeconds,
            }.ToString());
            await connection.OpenAsync(budget.Token).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, "PRAGMA query_only=ON;", budget.Token)
                .ConfigureAwait(false);

            var schemaType = await ExecuteScalarAsync(
                    connection,
                    "SELECT type FROM sqlite_schema "
                    + "WHERE CAST(name AS BLOB) = CAST($tableName AS BLOB) LIMIT 1;",
                    budget.Token,
                    ("$tableName", "ConfigItems"))
                .ConfigureAwait(false);
            if (schemaType is null)
                return;
            if (!string.Equals(Convert.ToString(schemaType), "table", StringComparison.Ordinal))
                throw new SqliteGuardValidationException();

            var marker = await ExecuteScalarAsync(
                    connection,
                    "SELECT 1 FROM \"ConfigItems\" "
                    + "WHERE length(CAST(\"ConfigName\" AS BLOB)) = $keyLength "
                    + "AND CAST(\"ConfigName\" AS BLOB) = $keyBytes LIMIT 1;",
                    budget.Token,
                    ("$keyLength", ImportStateKeyBytes.Length),
                    ("$keyBytes", ImportStateKeyBytes))
                .ConfigureAwait(false);
            if (marker is not null)
                throw new TransferV3MarkerPresentException();
        }
        catch (TransferV3MarkerPresentException)
        {
            throw new InvalidOperationException(RefusalMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(ValidationFailureMessage);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = SqliteTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = SqliteTimeoutSeconds;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class TransferV3MarkerPresentException : Exception
    {
    }

    private sealed class SqliteGuardValidationException : Exception
    {
    }
}

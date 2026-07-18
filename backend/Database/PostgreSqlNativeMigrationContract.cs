using System.Collections.ObjectModel;
using System.Data;
using Npgsql;

namespace NzbWebDAV.Database;

internal sealed record PostgreSqlMigrationHistoryEntry(
    string MigrationId,
    string ProductVersion);

internal static class PostgreSqlNativeMigrationContract
{
    private static readonly ReadOnlyCollection<PostgreSqlMigrationHistoryEntry> ReviewedHead =
        Array.AsReadOnly(
        [
            new PostgreSqlMigrationHistoryEntry(
                "20260712000000_PostgreSqlNativeBaseline",
                "10.0.9"),
            new PostgreSqlMigrationHistoryEntry(
                "20260712000100_PostgreSqlOperationalTriggers",
                "10.0.9")
        ]);

    internal static IReadOnlyList<PostgreSqlMigrationHistoryEntry> Head => ReviewedHead;

    internal static void ValidatePrefix(
        IReadOnlyList<PostgreSqlMigrationHistoryEntry> capturedRows)
    {
        ArgumentNullException.ThrowIfNull(capturedRows);
        if (capturedRows.Count > ReviewedHead.Count)
            throw PrefixFailure();

        for (var index = 0; index < capturedRows.Count; index++)
        {
            var captured = capturedRows[index];
            var expected = ReviewedHead[index];
            if (captured is null
                || !string.Equals(
                    captured.MigrationId,
                    expected.MigrationId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    captured.ProductVersion,
                    expected.ProductVersion,
                    StringComparison.Ordinal))
            {
                throw PrefixFailure();
            }
        }
    }

    internal static void ValidateHead(
        IReadOnlyList<PostgreSqlMigrationHistoryEntry> capturedRows)
    {
        ArgumentNullException.ThrowIfNull(capturedRows);
        ValidatePrefix(capturedRows);
        if (capturedRows.Count != ReviewedHead.Count)
            throw new InvalidOperationException(
                "PostgreSQL migration history is not the exact reviewed native head.");
    }

    internal static async Task<IReadOnlyList<PostgreSqlMigrationHistoryEntry>> CaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ValidateTransactionContext(connection, transaction, commandTimeoutSeconds);

        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        var capturedRows = new List<PostgreSqlMigrationHistoryEntry>(ReviewedHead.Count);
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText =
                $"""
                SELECT "MigrationId", "ProductVersion"
                FROM "{DatabaseMigrationPolicy.PostgreSqlHistoryTableName}"
                ORDER BY "MigrationId" COLLATE "C", "ProductVersion" COLLATE "C"
                """;
            reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (capturedRows.Count == Head.Count)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL migration history contains more rows than the reviewed native head.");
                }

                capturedRows.Add(new PostgreSqlMigrationHistoryEntry(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                reader,
                command,
                primaryFailure)
            .ConfigureAwait(false);
        return capturedRows.AsReadOnly();
    }

    internal static async Task ValidatePrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var capturedRows = await CaptureAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            cancellationToken);
        ValidatePrefix(capturedRows);
    }

    internal static async Task ValidateHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var capturedRows = await CaptureAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            cancellationToken);
        ValidateHead(capturedRows);
    }

    private static void ValidateTransactionContext(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "PostgreSQL migration-history capture requires an open connection.");
        NpgsqlConnection? transactionConnection;
        try
        {
            transactionConnection = transaction.Connection;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }

        if (!ReferenceEquals(transactionConnection, connection))
            throw TransactionFailure();

        try
        {
            // Npgsql 10.0.3 keeps Connection available after commit/rollback;
            // the public IsolationLevel getter invokes its internal CheckReady.
            _ = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }
    }

    private static InvalidOperationException PrefixFailure() =>
        new("PostgreSQL migration history is not an exact reviewed native prefix.");

    private static InvalidOperationException TransactionFailure(Exception? inner = null) =>
        new(
            "PostgreSQL migration-history capture requires an active transaction owned by the supplied connection.",
            inner);
}

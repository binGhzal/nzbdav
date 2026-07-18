using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3PostgreSqlAdmissionLockSet
{
    internal const long AdvisoryNamespaceSeed = 0x4E5A425456335034;

    private const string AdvisorySql =
        """
        SELECT pg_catalog.pg_try_advisory_xact_lock(
            pg_catalog.hashtextextended(
                @targetSchema,
                pg_catalog.hashtextextended(
                    pg_catalog.current_database(),
                    @namespaceSeed)))
        """;

    private static readonly IReadOnlyList<string> FrozenRelationNames =
        BuildRelationNames();

    internal static IReadOnlyList<string> RelationNames => FrozenRelationNames;

    internal static string BuildRelationLockSql(string targetSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSchema);
        var quotedSchema = QuoteIdentifier(targetSchema);
        var relations = RelationNames.Select(name =>
            $"  {quotedSchema}.{QuoteIdentifier(name)}");
        return "LOCK TABLE\n"
               + string.Join(",\n", relations)
               + "\nIN EXCLUSIVE MODE NOWAIT";
    }

    internal static async Task<bool> TryAcquireAdvisoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateReadCommittedTransaction(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        var targetSchema = PostgreSqlEnvironmentContract.GetRequiredTargetSchema(
            connection.ConnectionString);

        NpgsqlCommand? command = null;
        object? scalar = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = AdvisorySql;
            AddNpgsqlParameter(
                command,
                "@targetSchema",
                NpgsqlDbType.Text,
                targetSchema);
            AddNpgsqlParameter(
                command,
                "@namespaceSeed",
                NpgsqlDbType.Bigint,
                AdvisoryNamespaceSeed);

            scalar = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception raw)
        {
            primaryFailure = TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                null,
                command,
                primaryFailure)
            .ConfigureAwait(false);
        if (scalar is not bool acquired)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                new InvalidOperationException(),
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }

        return acquired;
    }

    internal static async Task AcquireRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateReadCommittedTransaction(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        var targetSchema = PostgreSqlEnvironmentContract.GetRequiredTargetSchema(
            connection.ConnectionString);

        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = BuildRelationLockSql(targetSchema);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception raw)
        {
            primaryFailure = TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                null,
                command,
                primaryFailure)
            .ConfigureAwait(false);
    }

    private static void ValidateReadCommittedTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        if (connection.State != ConnectionState.Open)
            throw TransactionFailure();

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

        IsolationLevel isolationLevel;
        try
        {
            isolationLevel = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }

        if (isolationLevel != IsolationLevel.ReadCommitted)
            throw TransactionFailure();
    }

    private static void AddNpgsqlParameter(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value;
    }

    private static IReadOnlyList<string> BuildRelationNames()
    {
        var target = TransferV3PostgreSqlTargetContract.LoadEmbedded();
        var relationNames = target.Tables.Select(table => table.Name)
            .Append(target.DerivedHealthCheckStats.Name)
            .Append(DatabaseMigrationPolicy.PostgreSqlHistoryTableName)
            .ToArray();
        if (relationNames.Length != 29
            || relationNames.Distinct(StringComparer.Ordinal).Count() != relationNames.Length)
        {
            throw new InvalidDataException(
                "The PostgreSQL admission relation lock set is invalid.");
        }

        return Array.AsReadOnly(relationNames);
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static TransferV3Phase4Exception TransactionFailure(Exception? inner = null) =>
        TransferV3Phase4Exception.Create(
            inner ?? new InvalidOperationException(),
            TransferV3Phase4Boundary.PostgreSqlCommand);
}

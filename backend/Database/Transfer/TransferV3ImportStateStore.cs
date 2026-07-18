using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3ImportStateStore
{
    internal const string UnsafeCallerStateMessage =
        "The transfer-v3 import-state store requires a closed, untracked context outside every transaction.";
    internal const string UnsupportedProviderMessage =
        "The transfer-v3 import-state store supports only SQLite and PostgreSQL.";
    internal const string PostgreSqlTransactionFailureMessage =
        "The transfer-v3 PostgreSQL import-state operation requires an open connection and an active transaction owned by that connection.";
    internal const string PostgreSqlReadFailureMessage =
        "The transfer-v3 PostgreSQL import-state row is not exactly one canonical text value.";
    internal const int SqliteCommandTimeoutSeconds = 1;
    internal const int PostgreSqlCommandTimeoutSeconds = 1;
    internal const int PostgreSqlConnectionTimeoutSeconds = 2;
    internal static readonly TimeSpan SqliteFullInvocationTimeoutUpperBound = TimeSpan.FromSeconds(5);

    internal const string SqliteCasSql =
        """
        UPDATE "ConfigItems"
        SET "ConfigValue" = CAST(@next AS TEXT)
        WHERE "ConfigName" = CAST(@key AS TEXT)
          AND typeof("ConfigName") = 'text'
          AND length(CAST("ConfigName" AS BLOB)) = @keyLength
          AND CAST("ConfigName" AS BLOB) = @key
          AND typeof("ConfigValue") = 'text'
          AND length(CAST("ConfigValue" AS BLOB)) = @expectedLength
          AND CAST("ConfigValue" AS BLOB) = @expected
        """;

    internal const string PostgreSqlCasSql =
        """
        UPDATE "ConfigItems"
        SET "ConfigValue" = convert_from(@next, 'UTF8')
        WHERE pg_typeof("ConfigName") = 'text'::regtype
          AND "ConfigName" = @keyText
          AND CASE
                WHEN octet_length("ConfigName") = @keyLength
                THEN convert_to("ConfigName", 'UTF8') = @key
                ELSE false
              END
          AND pg_typeof("ConfigValue") = 'text'::regtype
          AND CASE
                WHEN octet_length("ConfigValue") = @expectedLength
                THEN convert_to("ConfigValue", 'UTF8') = @expected
                ELSE false
              END
        """;

    internal const string PostgreSqlReadForShareSql =
        """
        SELECT
          pg_typeof(config."ConfigValue") = 'text'::regtype,
          CASE
            WHEN pg_typeof(config."ConfigValue") = 'text'::regtype THEN
              CASE
                WHEN octet_length(config."ConfigValue"::text) <= @maxCanonicalUtf8Bytes THEN
                  CASE
                    WHEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8')) <= @maxCanonicalUtf8Bytes
                    THEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8'))
                    ELSE @maxCanonicalUtf8Bytes + 1
                  END
                ELSE @maxCanonicalUtf8Bytes + 1
              END
            ELSE @maxCanonicalUtf8Bytes + 1
          END,
          CASE
            WHEN pg_typeof(config."ConfigValue") = 'text'::regtype THEN
              CASE
                WHEN octet_length(config."ConfigValue"::text) <= @maxCanonicalUtf8Bytes THEN
                  CASE
                    WHEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8')) <= @maxCanonicalUtf8Bytes
                    THEN convert_to(config."ConfigValue"::text, 'UTF8')
                    ELSE ''::bytea
                  END
                ELSE ''::bytea
              END
            ELSE ''::bytea
          END
        FROM "ConfigItems" AS config
        WHERE CASE
          WHEN pg_typeof(config."ConfigName") = 'text'::regtype THEN
            CASE
              WHEN config."ConfigName"::text = @keyText THEN
                CASE
                  WHEN octet_length(config."ConfigName"::text) = @keyLength
                  THEN convert_to(config."ConfigName"::text, 'UTF8') = @key
                  ELSE false
                END
              ELSE false
            END
          ELSE false
        END
        LIMIT 2
        FOR SHARE OF config
        """;

    private const int SqliteMaxAttempts = 2;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private static readonly byte[] ImportStateKeyUtf8 =
        Encoding.UTF8.GetBytes(TransferV3ReservedConfigPolicy.ImportStateKey);

    private readonly DavDatabaseContext _callerContext;
    private readonly ProviderKind _provider;
    private readonly string _ownedConnectionString;

    internal TransferV3ImportStateStore(DavDatabaseContext context)
    {
        _callerContext = context ?? throw new ArgumentNullException(nameof(context));
        ValidateCallerState();

        var callerConnection = context.Database.GetDbConnection();
        _provider = callerConnection switch
        {
            SqliteConnection => ProviderKind.Sqlite,
            NpgsqlConnection => ProviderKind.PostgreSql,
            _ => throw new InvalidOperationException(UnsupportedProviderMessage),
        };

        if (string.IsNullOrWhiteSpace(callerConnection.ConnectionString))
            throw new InvalidOperationException(UnsupportedProviderMessage);

        _ownedConnectionString = _provider switch
        {
            ProviderKind.Sqlite => BuildSqliteConnectionString(callerConnection.ConnectionString),
            ProviderKind.PostgreSql => BuildPostgreSqlConnectionString(callerConnection.ConnectionString),
            _ => throw new InvalidOperationException(UnsupportedProviderMessage),
        };
    }

    internal async Task<int> TryTransitionAsync(
        TransferV3ImportState expected,
        TransferV3ImportState next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        if (!IsLegalTransition(expected, next))
            return 0;

        ValidateCallerState();
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? expectedUtf8 = null;
        byte[]? nextUtf8 = null;
        try
        {
            expectedUtf8 = TransferV3ImportStateCodec.Serialize(expected);
            nextUtf8 = TransferV3ImportStateCodec.Serialize(next);
            return _provider switch
            {
                ProviderKind.Sqlite => await ExecuteSqliteCasAsync(
                    expectedUtf8,
                    nextUtf8,
                    cancellationToken).ConfigureAwait(false),
                ProviderKind.PostgreSql => await ExecutePostgreSqlCasAsync(
                    expectedUtf8,
                    nextUtf8,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(UnsupportedProviderMessage),
            };
        }
        finally
        {
            if (expectedUtf8 is not null)
                CryptographicOperations.ZeroMemory(expectedUtf8);
            if (nextUtf8 is not null)
                CryptographicOperations.ZeroMemory(nextUtf8);
        }
    }

    internal static async Task<int> TryTransitionInPostgreSqlTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransferV3ImportState expected,
        TransferV3ImportState next,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        if (!IsLegalTransition(expected, next))
            return 0;

        ValidatePostgreSqlTransactionContext(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? expectedUtf8 = null;
        byte[]? nextUtf8 = null;
        try
        {
            expectedUtf8 = TransferV3ImportStateCodec.Serialize(expected);
            nextUtf8 = TransferV3ImportStateCodec.Serialize(next);
            return await ExecutePostgreSqlCasCommandAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    expectedUtf8,
                    nextUtf8,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (expectedUtf8 is not null)
                CryptographicOperations.ZeroMemory(expectedUtf8);
            if (nextUtf8 is not null)
                CryptographicOperations.ZeroMemory(nextUtf8);
        }
    }

    internal static async Task<int> TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        byte[] expectedCanonicalUtf8,
        byte[] nextCanonicalUtf8,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(expectedCanonicalUtf8);
        ArgumentNullException.ThrowIfNull(nextCanonicalUtf8);
        if (!TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
                expectedCanonicalUtf8,
                nextCanonicalUtf8))
            return 0;

        ValidatePostgreSqlTransactionContext(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return await ExecutePostgreSqlCasCommandAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                expectedCanonicalUtf8,
                nextCanonicalUtf8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<TransferV3ImportState> ReadForShareInPostgreSqlTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidatePostgreSqlTransactionContext(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();

        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        byte[]? canonicalUtf8 = null;
        TransferV3ImportState? result = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = PostgreSqlReadForShareSql;
            AddNpgsqlParameter(
                command,
                "@keyText",
                NpgsqlDbType.Text,
                TransferV3ReservedConfigPolicy.ImportStateKey);
            AddNpgsqlParameter(command, "@key", NpgsqlDbType.Bytea, ImportStateKeyUtf8);
            AddNpgsqlParameter(
                command,
                "@keyLength",
                NpgsqlDbType.Integer,
                ImportStateKeyUtf8.Length);
            AddNpgsqlParameter(
                command,
                "@maxCanonicalUtf8Bytes",
                NpgsqlDbType.Integer,
                TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes);

            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hasFirstRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var nativeText = false;
            var canonicalLength = 0;
            if (hasFirstRow
                && reader.FieldCount == 3
                && !reader.IsDBNull(0)
                && !reader.IsDBNull(1)
                && !reader.IsDBNull(2))
            {
                nativeText = reader.GetBoolean(0);
                canonicalLength = reader.GetInt32(1);
                canonicalUtf8 = reader.GetFieldValue<byte[]>(2);
            }

            var hasSecondRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!hasFirstRow
                || hasSecondRow
                || !nativeText
                || canonicalUtf8 is null
                || canonicalLength < 1
                || canonicalLength > TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes
                || canonicalUtf8.Length != canonicalLength)
            {
                throw PostgreSqlReadFailure();
            }

            try
            {
                result = TransferV3ImportStateCodec.ParseCanonical(canonicalUtf8);
            }
            catch (FormatException)
            {
                throw PostgreSqlReadFailure();
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                    reader,
                    command,
                    primaryFailure)
                .ConfigureAwait(false);
        }
        finally
        {
            if (canonicalUtf8 is not null)
                CryptographicOperations.ZeroMemory(canonicalUtf8);
        }

        return result!;
    }

    private static bool IsLegalTransition(
        TransferV3ImportState expected,
        TransferV3ImportState next)
    {
        if (expected.Kind == TransferV3ImportStateKind.Fresh)
            return next.Kind == TransferV3ImportStateKind.Importing;

        return expected.Kind == TransferV3ImportStateKind.Importing
               && string.Equals(
                   expected.ManifestSha256,
                   next.ManifestSha256,
                   StringComparison.Ordinal)
               && next.Kind is TransferV3ImportStateKind.DatabaseVerified
                   or TransferV3ImportStateKind.Failed;
    }

    private async Task<int> ExecuteSqliteCasAsync(
        byte[] expectedUtf8,
        byte[] nextUtf8,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqliteConnection(_ownedConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = SqliteCasSql;
                command.CommandTimeout = SqliteCommandTimeoutSeconds;
                AddSqliteBlobParameter(command, "@key", ImportStateKeyUtf8);
                command.Parameters.AddWithValue("@keyLength", ImportStateKeyUtf8.Length);
                AddSqliteBlobParameter(command, "@expected", expectedUtf8);
                command.Parameters.AddWithValue("@expectedLength", expectedUtf8.Length);
                AddSqliteBlobParameter(command, "@next", nextUtf8);
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (
                IsBusyOrLocked(exception) && attempt < SqliteMaxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ExecutePostgreSqlCasAsync(
        byte[] expectedUtf8,
        byte[] nextUtf8,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_ownedConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ExecutePostgreSqlCasCommandAsync(
                connection,
                transaction: null,
                PostgreSqlCommandTimeoutSeconds,
                expectedUtf8,
                nextUtf8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> ExecutePostgreSqlCasCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        byte[] expectedUtf8,
        byte[] nextUtf8,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        var affectedRows = 0;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = PostgreSqlCasSql;
            AddNpgsqlParameter(
                command,
                "@keyText",
                NpgsqlDbType.Text,
                TransferV3ReservedConfigPolicy.ImportStateKey);
            AddNpgsqlParameter(command, "@key", NpgsqlDbType.Bytea, ImportStateKeyUtf8);
            AddNpgsqlParameter(
                command,
                "@keyLength",
                NpgsqlDbType.Integer,
                ImportStateKeyUtf8.Length);
            AddNpgsqlParameter(command, "@expected", NpgsqlDbType.Bytea, expectedUtf8);
            AddNpgsqlParameter(
                command,
                "@expectedLength",
                NpgsqlDbType.Integer,
                expectedUtf8.Length);
            AddNpgsqlParameter(command, "@next", NpgsqlDbType.Bytea, nextUtf8);
            affectedRows = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                reader: null,
                command,
                primaryFailure)
            .ConfigureAwait(false);
        return affectedRows;
    }

    private static void ValidatePostgreSqlTransactionContext(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        if (connection.State != ConnectionState.Open)
            throw PostgreSqlTransactionFailure();

        NpgsqlConnection? transactionConnection;
        try
        {
            transactionConnection = transaction.Connection;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw PostgreSqlTransactionFailure();
        }

        if (!ReferenceEquals(transactionConnection, connection))
            throw PostgreSqlTransactionFailure();

        try
        {
            // Npgsql 10.0.3 keeps Connection after completion; this getter runs CheckReady.
            _ = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw PostgreSqlTransactionFailure();
        }
    }

    private static InvalidOperationException PostgreSqlTransactionFailure() =>
        new(PostgreSqlTransactionFailureMessage);

    private static FormatException PostgreSqlReadFailure() =>
        new(PostgreSqlReadFailureMessage);

    private void ValidateCallerState()
    {
        var connection = _callerContext.Database.GetDbConnection();
        if (Transaction.Current is not null
            || _callerContext.Database.CurrentTransaction is not null
            || connection.State != ConnectionState.Closed
            || HasTrackedReservedConfig())
        {
            throw new InvalidOperationException(UnsafeCallerStateMessage);
        }
    }

    private bool HasTrackedReservedConfig()
    {
        foreach (var entry in _callerContext.ChangeTracker.Entries<ConfigItem>())
        {
            var property = entry.Property(item => item.ConfigName);
            if (TransferV3ReservedConfigPolicy.IsReserved(property.CurrentValue)
                || TransferV3ReservedConfigPolicy.IsReserved(property.OriginalValue))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildSqliteConnectionString(string callerConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(callerConnectionString)
        {
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = SqliteCommandTimeoutSeconds,
        };
        return builder.ToString();
    }

    private static string BuildPostgreSqlConnectionString(string callerConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(callerConnectionString)
        {
            Pooling = false,
            Enlist = false,
            Timeout = PostgreSqlConnectionTimeoutSeconds,
            CommandTimeout = PostgreSqlCommandTimeoutSeconds,
        };
        return builder.ConnectionString;
    }

    private static void AddSqliteBlobParameter(
        SqliteCommand command,
        string name,
        byte[] value)
    {
        var parameter = command.Parameters.Add(name, SqliteType.Blob);
        parameter.Value = value;
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

    private static bool IsBusyOrLocked(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteBusy or SqliteLocked;

    private enum ProviderKind
    {
        Sqlite,
        PostgreSql,
    }
}

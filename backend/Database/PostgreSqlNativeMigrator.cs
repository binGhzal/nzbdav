using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace NzbWebDAV.Database;

internal static class PostgreSqlNativeMigrator
{
    internal const string CleanupFailureDataKey = "PostgreSqlAdvisoryUnlockFailure";
    private const string TransactionRollbackFailureDataKey =
        "PostgreSqlTransactionRollbackFailure";
    private const string TransactionDisposeFailureDataKey =
        "PostgreSqlTransactionDisposeFailure";
    private const string AdvisoryAcquireCleanupFailureDataKey =
        "PostgreSqlAdvisoryAcquireCleanupFailure";
    private const string AdvisoryReleaseCleanupFailureDataKey =
        "PostgreSqlAdvisoryReleaseCleanupFailure";
    internal const int AdvisoryUnlockCommandTimeoutSeconds = 5;
    internal const int ValidationCommandTimeoutSeconds = 30;
    private const long AdvisoryLockSeed = 0x4E5A424450474D49;

    private static readonly string[] OperationalFunctions =
    [
        "FN_TR_DavItems_Delete_AddBlobCleanup",
        "FN_TR_DavItems_Delete_AddNzbBlobCleanup",
        "FN_TR_DavItems_DeleteDirectory",
        "FN_TR_DavItems_Update_AddBlobCleanup",
        "FN_TR_HealthCheckResults_DecrementStats",
        "FN_TR_HealthCheckResults_IncrementStats",
        "FN_TR_HealthCheckResults_UpdateStats",
        "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup",
        "FN_TR_QueueItems_Delete_AddNzbBlobCleanup"
    ];

    private static readonly string[] OperationalTriggers =
    [
        "TR_DavItems_Delete_AddBlobCleanup",
        "TR_DavItems_Delete_AddNzbBlobCleanup",
        "TR_DavItems_DeleteDirectory",
        "TR_DavItems_Update_AddBlobCleanup",
        "TR_HealthCheckResults_DecrementStats",
        "TR_HealthCheckResults_IncrementStats",
        "TR_HealthCheckResults_UpdateStats",
        "TR_HistoryItems_Delete_AddNzbBlobCleanup",
        "TR_QueueItems_Delete_AddNzbBlobCleanup"
    ];

    internal static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var validatedConnectionString =
            PostgreSqlConnectionPolicy.ValidateConnectionString(connectionString);
        PostgreSqlConnectionPolicy.ValidateDisposableTestConnectionString(validatedConnectionString);
        await using var connection = new NpgsqlConnection(validatedConnectionString);
        await connection.OpenAsync(cancellationToken);
        await MigrateOpenConnectionAsync(connection, cancellationToken);
    }

    internal static async Task MigrateOpenConnectionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Native PostgreSQL migration requires an explicitly opened connection.");

        _ = PostgreSqlConnectionPolicy.ValidateConnectionString(connection.ConnectionString);
        PostgreSqlConnectionPolicy.ValidateDisposableTestConnectionString(connection.ConnectionString);
        await PostgreSqlConnectionPolicy.ValidateSessionTimezoneAsync(connection, cancellationToken);
        var targetSchema = await PostgreSqlEnvironmentContract.ValidateAsync(
            connection,
            cancellationToken);
        var databaseName = await ScalarAsync<string>(
            connection,
            transaction: null,
            ValidationCommandTimeoutSeconds,
            "SELECT current_database()",
            cancellationToken);
        var scope = $"{databaseName}:{targetSchema}";
        cancellationToken.ThrowIfCancellationRequested();
        await AcquireAdvisoryLockOwnedAsync(
            connection,
            scope,
            ValidationCommandTimeoutSeconds,
            cancellationToken,
            async () =>
            {
                var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
                    .UseNpgsql(
                        connection,
                        postgres => postgres.MigrationsHistoryTable(
                            DatabaseMigrationPolicy.PostgreSqlHistoryTableName,
                            targetSchema))
                    .Options;
                await using var context = new PostgreSqlDavDatabaseContext(options);

                var historyExists = await HistoryTableExistsAsync(
                    connection,
                    targetSchema,
                    ValidationCommandTimeoutSeconds,
                    cancellationToken);
                var appliedPrefixLength = await RunReadValidationAsync(
                    connection,
                    ValidationCommandTimeoutSeconds,
                    (transaction, token) => PreflightAsync(
                        connection,
                        transaction,
                        context,
                        targetSchema,
                        historyExists,
                        ValidationCommandTimeoutSeconds,
                        token),
                    cancellationToken);
                await context.Database.MigrateAsync(cancellationToken);
                await RunReadValidationAsync(
                    connection,
                    ValidationCommandTimeoutSeconds,
                    async (transaction, token) =>
                    {
                        await LockHistoryTableAsync(
                            connection,
                            transaction,
                            ValidationCommandTimeoutSeconds,
                            token);
                        await ValidateHistoryTableShapeAsync(
                            connection,
                            transaction,
                            targetSchema,
                            ValidationCommandTimeoutSeconds,
                            token);
                        await PostgreSqlNativeMigrationContract.ValidateHeadAsync(
                            connection,
                            transaction,
                            ValidationCommandTimeoutSeconds,
                            token);
                        await ValidatePhysicalSchemaAsync(
                            connection,
                            transaction,
                            context,
                            expectedOperationalObjects: true,
                            ValidationCommandTimeoutSeconds,
                            token);

                        if (appliedPrefixLength < PostgreSqlNativeMigrationContract.Head.Count)
                        {
                            await PostgreSqlFreshBootstrapContract.ValidateAsync(
                                connection,
                                transaction,
                                ValidationCommandTimeoutSeconds,
                                token);
                        }

                        return true;
                    },
                    cancellationToken);
            });
    }

    private static async Task<T> RunReadValidationAsync<T>(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        Func<NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operation);

        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        Exception? primaryFailure = null;
        T? result = default;
        try
        {
            await SetTransactionReadOnlyAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken);
            result = await operation(transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (primaryFailure is not null)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                RecordCleanupFailure(
                    primaryFailure!,
                    TransactionRollbackFailureDataKey,
                    cleanupFailure);
            }
        }

        try
        {
            await transaction.DisposeAsync();
        }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            RecordCleanupFailure(
                primaryFailure!,
                TransactionDisposeFailureDataKey,
                cleanupFailure);
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        return result!;
    }

    private static async Task SetTransactionReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = "SET TRANSACTION READ ONLY";
            await command.ExecuteNonQueryAsync(cancellationToken);
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
    }

    internal static async Task RunWithCleanupAsync(
        Func<Task> operation,
        Func<Task> cleanup)
    {
        Exception? primaryFailure = null;
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            if (primaryFailure is null)
            {
                await cleanup().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await cleanup().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    // Preserve the migration/preflight failure as causal while
                    // retaining the cleanup failure for diagnostics and tests.
                    RecordCleanupFailure(
                        primaryFailure,
                        CleanupFailureDataKey,
                        cleanupFailure);
                }
            }
        }
    }

    private static async Task<int> PreflightAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDavDatabaseContext context,
        string targetSchema,
        bool historyExists,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (historyExists)
        {
            await LockHistoryTableAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken);
            await ValidateHistoryTableShapeAsync(
                connection,
                transaction,
                targetSchema,
                commandTimeoutSeconds,
                cancellationToken);
        }

        var history = historyExists
            ? await PostgreSqlNativeMigrationContract.CaptureAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken)
            : Array.Empty<PostgreSqlMigrationHistoryEntry>();
        PostgreSqlNativeMigrationContract.ValidatePrefix(history);

        if (history.Count == 0)
        {
            try
            {
                await PostgreSqlPhysicalCatalogContract.ValidateAsync(
                    connection,
                    transaction,
                    historyExists
                        ? PostgreSqlCatalogState.EmptyHistory
                        : PostgreSqlCatalogState.EmptySchema,
                    ValidationCommandTimeoutSeconds,
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    "PostgreSQL migration refused: the native baseline requires an exact empty application schema " +
                    "or exact EF-created empty history catalog.",
                    exception);
            }
            return 0;
        }

        await ValidatePhysicalSchemaAsync(
            connection,
            transaction,
            context,
            expectedOperationalObjects:
                history.Count == PostgreSqlNativeMigrationContract.Head.Count,
            commandTimeoutSeconds,
            cancellationToken);
        if (history.Count == 1)
        {
            await PostgreSqlFreshBootstrapContract.ValidateAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                cancellationToken);
        }

        return history.Count;
    }

    private static Task<bool> HistoryTableExistsAsync(
        NpgsqlConnection connection,
        string targetSchema,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction: null,
            commandTimeoutSeconds,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class AS c
                JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.nspname = @target_schema
                  AND c.relname = @history_table)
            """,
            cancellationToken,
            ("target_schema", targetSchema),
            ("history_table", DatabaseMigrationPolicy.PostgreSqlHistoryTableName));

    private static async Task LockHistoryTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText =
                "LOCK TABLE \"__EFMigrationsHistory_PostgreSql\" IN SHARE MODE";
            await command.ExecuteNonQueryAsync(cancellationToken);
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
    }

    private static async Task ValidateHistoryTableShapeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string targetSchema,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var historyTableName = DatabaseMigrationPolicy.PostgreSqlHistoryTableName;
        var isExactShape = await ScalarAsync<bool>(
            connection,
            transaction,
            commandTimeoutSeconds,
            $$"""
            SELECT
                (SELECT count(*) = 1
                 FROM pg_class AS c
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = @target_schema
                   AND c.relname = '{{historyTableName}}'
                   AND c.relkind = 'r'
                   AND c.relpersistence = 'p'
                   AND NOT c.relrowsecurity
                   AND NOT c.relforcerowsecurity
                   AND c.relreplident = 'd'
                   AND c.reloptions IS NULL)
              AND
                (SELECT count(*) = 2
                     AND count(*) FILTER (
                         WHERE a.attnotnull
                           AND NOT a.atthasdef
                           AND a.attidentity = ''
                           AND a.attgenerated = ''
                           AND a.attcollation = (
                               SELECT col.oid
                               FROM pg_collation AS col
                               JOIN pg_namespace AS col_ns ON col_ns.oid = col.collnamespace
                               WHERE col_ns.nspname = 'pg_catalog' AND col.collname = 'default')
                           AND ((a.attnum = 1
                                 AND a.attname = 'MigrationId'
                                 AND pg_catalog.format_type(a.atttypid, a.atttypmod) = 'character varying(150)')
                                OR (a.attnum = 2
                                    AND a.attname = 'ProductVersion'
                                    AND pg_catalog.format_type(a.atttypid, a.atttypmod) = 'character varying(32)'))) = 2
                 FROM pg_attribute AS a
                 JOIN pg_class AS c ON c.oid = a.attrelid
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = @target_schema
                   AND c.relname = '{{historyTableName}}'
                   AND a.attnum > 0
                   AND NOT a.attisdropped)
              AND
                (SELECT count(*) = 1
                     AND count(*) FILTER (
                         WHERE con.conname = 'PK___EFMigrationsHistory_PostgreSql'
                           AND con.contype = 'p'
                           AND con.conkey = ARRAY[1]::smallint[]
                           AND NOT con.condeferrable
                           AND NOT con.condeferred
                           AND con.convalidated) = 1
                 FROM pg_constraint AS con
                 JOIN pg_class AS c ON c.oid = con.conrelid
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = @target_schema
                   AND c.relname = '{{historyTableName}}')
              AND
                (SELECT count(*) = 1
                     AND count(*) FILTER (
                         WHERE i.relname = 'PK___EFMigrationsHistory_PostgreSql'
                           AND x.indisprimary
                           AND x.indisunique
                           AND x.indisvalid
                           AND x.indisready
                           AND x.indislive
                           AND NOT x.indisreplident
                           AND x.indnkeyatts = 1
                           AND x.indnatts = 1
                           AND x.indkey::text = '1'
                           AND x.indexprs IS NULL
                           AND x.indpred IS NULL) = 1
                 FROM pg_index AS x
                 JOIN pg_class AS i ON i.oid = x.indexrelid
                 JOIN pg_class AS c ON c.oid = x.indrelid
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = @target_schema
                   AND c.relname = '{{historyTableName}}')
              AND
                (SELECT count(*) = 0
                 FROM pg_trigger AS tr
                 JOIN pg_class AS c ON c.oid = tr.tgrelid
                 JOIN pg_namespace AS n ON n.oid = c.relnamespace
                 WHERE n.nspname = @target_schema
                   AND c.relname = '{{historyTableName}}'
                   AND NOT tr.tgisinternal)
            """,
            cancellationToken,
            ("target_schema", targetSchema));

        if (!isExactShape)
            throw new InvalidOperationException(
                "PostgreSQL migration refused: the existing EF migration history table has an unexpected shape or primary key.");
    }

    private static async Task ValidatePhysicalSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgreSqlDavDatabaseContext context,
        bool expectedOperationalObjects,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var entityTypes = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .ToArray();
        var expectedTables = entityTypes
            .Select(entity => entity.GetTableName()!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedColumns = entityTypes
            .SelectMany(entity =>
            {
                var storeObject = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                return entity.GetProperties()
                    .Select(property => $"{entity.GetTableName()}.{property.GetColumnName(storeObject)}");
            })
            .ToHashSet(StringComparer.Ordinal);
        var expectedIndexes = entityTypes
            .SelectMany(entity => entity.GetIndexes())
            .Select(index => index.GetDatabaseName()!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedPrimaryKeys = entityTypes
            .Select(entity => entity.FindPrimaryKey()!.GetName()!)
            .ToHashSet(StringComparer.Ordinal);
        var expectedForeignKeys = entityTypes
            .SelectMany(entity => entity.GetForeignKeys())
            .Select(foreignKey => foreignKey.GetConstraintName()!)
            .ToHashSet(StringComparer.Ordinal);

        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = current_schema()
              AND table_type = 'BASE TABLE'
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
            """,
            expectedTables,
            "tables",
            cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
            """,
            expectedColumns,
            "columns",
            cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT i.relname
            FROM pg_index AS x
            JOIN pg_class AS i ON i.oid = x.indexrelid
            JOIN pg_class AS t ON t.oid = x.indrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema()
              AND t.relname <> '__EFMigrationsHistory_PostgreSql'
              AND NOT x.indisprimary
            """,
            expectedIndexes,
            "secondary indexes",
            cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT c.conname
            FROM pg_constraint AS c
            JOIN pg_namespace AS n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.contype = 'p'
              AND c.conname <> 'PK___EFMigrationsHistory_PostgreSql'
            """,
            expectedPrimaryKeys,
            "primary keys",
            cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT c.conname
            FROM pg_constraint AS c
            JOIN pg_namespace AS n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.contype = 'f'
            """,
            expectedForeignKeys,
            "foreign keys",
            cancellationToken);

        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND data_type = 'uuid'
            """, 46, "uuid columns", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND data_type = 'boolean'
            """, 6, "boolean columns", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND data_type = 'character varying'
            """, 47, "bounded varchar columns", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND data_type = 'timestamp without time zone'
            """, 4, "local-wall timestamp columns", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND collation_name = 'C'
            """, 20, "C-collated columns", cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name <> '__EFMigrationsHistory_PostgreSql'
              AND column_default IS NOT NULL
            """,
            new HashSet<string>(["WorkerJobs.LeaseGeneration"], StringComparer.Ordinal),
            "database defaults",
            cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relkind IN ('S', 'v', 'm', 'f', 'c')
            """, 0, "sequences/views/foreign tables/composite types", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*)
            FROM pg_type AS t
            JOIN pg_namespace AS n ON n.oid = t.typnamespace
            WHERE n.nspname = current_schema() AND t.typtype IN ('d', 'e', 'r', 'm')
            """, 0, "custom domain/enum/range/multirange types", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*)
            FROM pg_collation AS c
            JOIN pg_namespace AS n ON n.oid = c.collnamespace
            WHERE n.nspname = current_schema()
            """, 0, "schema-owned collations", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*)
            FROM pg_policy AS p
            JOIN pg_class AS c ON c.oid = p.polrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
            """, 0, "row-security policies", cancellationToken);
        await RequireCountAsync(connection, transaction, commandTimeoutSeconds,
            """
            SELECT count(*) FROM pg_extension AS e
            JOIN pg_namespace AS n ON n.oid = e.extnamespace
            WHERE n.nspname = current_schema()
            """, 0, "schema-local extensions", cancellationToken);

        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT p.proname
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            WHERE n.nspname = current_schema()
            """,
            expectedOperationalObjects
                ? OperationalFunctions.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            "operational functions",
            cancellationToken);
        await RequireSetAsync(
            connection,
            transaction,
            commandTimeoutSeconds,
            """
            SELECT tr.tgname
            FROM pg_trigger AS tr
            JOIN pg_class AS t ON t.oid = tr.tgrelid
            JOIN pg_namespace AS n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema() AND NOT tr.tgisinternal
            """,
            expectedOperationalObjects
                ? OperationalTriggers.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            "operational triggers",
            cancellationToken);

        await PostgreSqlPhysicalCatalogContract.ValidateAsync(
            connection,
            transaction,
            expectedOperationalObjects
                ? PostgreSqlCatalogState.Head
                : PostgreSqlCatalogState.Baseline,
            ValidationCommandTimeoutSeconds,
            cancellationToken);
    }

    private static async Task RequireCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        string sql,
        long expected,
        string contract,
        CancellationToken cancellationToken)
    {
        var actual = await ScalarAsync<long>(
            connection,
            transaction,
            commandTimeoutSeconds,
            sql,
            cancellationToken);
        if (actual != expected)
            throw new InvalidOperationException(
                $"PostgreSQL physical validation failed for {contract}: expected {expected}, found {actual}.");
    }

    private static async Task RequireSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        string sql,
        HashSet<string> expected,
        string contract,
        CancellationToken cancellationToken)
    {
        var actual = (await ReadStringsAsync(
                connection,
                transaction,
                commandTimeoutSeconds,
                sql,
                cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected)) throw PhysicalMismatch(contract, expected, actual);
    }

    private static InvalidOperationException PhysicalMismatch(
        string contract,
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).Order().ToArray();
        var unexpected = actualSet.Except(expectedSet, StringComparer.Ordinal).Order().ToArray();
        return new InvalidOperationException(
            $"PostgreSQL physical validation failed for {contract}; " +
            $"missing=[{string.Join(',', missing)}], unexpected=[{string.Join(',', unexpected)}].");
    }

    private static async Task<string[]> ReadStringsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        string sql,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        Exception? primaryFailure = null;
        var values = new List<string>();
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = sql;
            reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
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
        return values.ToArray();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        T? result = default;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            result = (T)(await command.ExecuteScalarAsync(cancellationToken))!;
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
        return result!;
    }

    private static async Task AcquireAdvisoryLockOwnedAsync(
        NpgsqlConnection connection,
        string scope,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Func<Task> cleanup = () => ReleaseAdvisoryLockAsync(connection, scope);
        try
        {
            await AcquireAdvisoryLockAsync(
                connection,
                scope,
                commandTimeoutSeconds,
                cancellationToken);
        }
        catch (Exception primaryFailure)
        {
            await QuarantineConnectionAsync(
                connection,
                primaryFailure,
                AdvisoryAcquireCleanupFailureDataKey);
            throw;
        }

        await RunWithCleanupAsync(operation, cleanup);
    }

    private static async Task AcquireAdvisoryLockAsync(
        NpgsqlConnection connection,
        string scope,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand? command = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@scope, @seed))";
            command.Parameters.AddWithValue("scope", scope);
            command.Parameters.AddWithValue("seed", AdvisoryLockSeed);
            await command.ExecuteNonQueryAsync(cancellationToken);
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
    }

    internal static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection, string scope)
    {
        try
        {
            var released = await ScalarAsync<bool?>(
                connection,
                transaction: null,
                AdvisoryUnlockCommandTimeoutSeconds,
                "SELECT pg_advisory_unlock(hashtextextended(@scope, @seed))",
                CancellationToken.None,
                ("scope", scope),
                ("seed", AdvisoryLockSeed));
            if (released is not true)
                throw new InvalidOperationException(
                    "PostgreSQL migration advisory unlock reported that this session did not own the lock.");
        }
        catch (Exception primaryFailure)
        {
            await QuarantineConnectionAsync(
                connection,
                primaryFailure,
                AdvisoryReleaseCleanupFailureDataKey);
            throw;
        }
    }

    private static async Task QuarantineConnectionAsync(
        NpgsqlConnection connection,
        Exception primaryFailure,
        string cleanupFailureDataKey)
    {
        Exception? firstCleanupFailure = null;
        try
        {
            // Closing the session releases any session-scoped advisory lock,
            // including an acquisition whose server acknowledgement was lost.
            connection.Close();
        }
        catch (Exception cleanupFailure)
        {
            firstCleanupFailure = cleanupFailure;
        }

        try
        {
            NpgsqlConnection.ClearPool(connection);
        }
        catch (Exception cleanupFailure)
        {
            firstCleanupFailure ??= cleanupFailure;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            firstCleanupFailure ??= cleanupFailure;
        }

        if (firstCleanupFailure is not null)
            RecordCleanupFailure(
                primaryFailure,
                cleanupFailureDataKey,
                firstCleanupFailure);
    }

    private static void RecordCleanupFailure(
        Exception primaryFailure,
        string dataKey,
        Exception cleanupFailure)
    {
        if (!primaryFailure.Data.Contains(dataKey))
            primaryFailure.Data[dataKey] = cleanupFailure;
    }
}

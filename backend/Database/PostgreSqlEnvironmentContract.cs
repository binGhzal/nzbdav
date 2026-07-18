using System.Data;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Npgsql;
using NzbWebDAV.Database.Transfer;

namespace NzbWebDAV.Database;

/// <summary>
/// Read-only, fail-closed checks for the exact PostgreSQL environment in which
/// NZBDav's provider-native migrations were generated and verified.
/// </summary>
internal static partial class PostgreSqlEnvironmentContract
{
    internal const string RequiredServerVersion = "16.14";
    internal const int RequiredServerVersionNumber = 160014;

    internal static Task<string> ValidateAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ValidateAsync(
            connection,
            connection.CommandTimeout,
            cancellationToken);
    }

    internal static Task<string> ValidateAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        return ValidateAsync(
            connection,
            RequiredServerVersion,
            RequiredServerVersionNumber,
            commandTimeoutSeconds,
            cancellationToken);
    }

    internal static Task<string> ValidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateTransactionContext(connection, transaction);
        return ValidateCoreAsync(
            connection,
            transaction,
            RequiredServerVersion,
            RequiredServerVersionNumber,
            commandTimeoutSeconds,
            cancellationToken);
    }

    // The explicit expected-version overload exists so the version gate can be
    // proved without provisioning a second, deliberately wrong server image.
    internal static Task<string> ValidateAsync(
        NpgsqlConnection connection,
        string requiredServerVersion,
        int requiredServerVersionNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ValidateAsync(
            connection,
            requiredServerVersion,
            requiredServerVersionNumber,
            connection.CommandTimeout,
            cancellationToken);
    }

    internal static Task<string> ValidateAsync(
        NpgsqlConnection connection,
        string requiredServerVersion,
        int requiredServerVersionNumber,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        return ValidateCoreAsync(
            connection,
            transaction: null,
            requiredServerVersion,
            requiredServerVersionNumber,
            commandTimeoutSeconds,
            cancellationToken);
    }

    private static async Task<string> ValidateCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string requiredServerVersion,
        int requiredServerVersionNumber,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredServerVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredServerVersionNumber, 1);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "PostgreSQL environment validation requires an open connection.");

        var configuredSearchPath = GetRequiredTargetSchema(connection.ConnectionString);

        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        string? validatedSchema = null;
        Exception? primaryFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText =
                """
            SELECT
                current_setting('server_version'),
                current_setting('server_version_num')::integer,
                current_schema(),
                current_schemas(false),
                current_schemas(true),
                pg_my_temp_schema(),
                current_user,
                current_user::regrole::oid,
                d.datdba,
                n.nspowner,
                owner_role.rolname,
                has_schema_privilege(current_user, n.oid, 'USAGE'),
                has_schema_privilege(current_user, n.oid, 'CREATE'),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(coalesce(n.nspacl, acldefault('n', n.nspowner))) AS acl
                    WHERE acl.privilege_type = 'CREATE'
                      AND acl.grantee <> n.nspowner),
                NOT EXISTS (
                    SELECT 1
                    FROM aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) AS acl
                    WHERE acl.privilege_type = 'CREATE'
                      AND acl.grantee <> d.datdba),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_default_acl AS da
                    WHERE da.defaclrole = current_user::regrole::oid
                      AND (da.defaclnamespace = 0 OR da.defaclnamespace = n.oid)),
                NOT EXISTS (SELECT 1 FROM pg_event_trigger),
                NOT EXISTS (SELECT 1 FROM pg_publication),
                NOT EXISTS (SELECT 1 FROM pg_publication_namespace),
                NOT EXISTS (
                    SELECT 1
                    FROM pg_subscription AS s
                    WHERE s.subdbid = d.oid)
            FROM pg_database AS d
            JOIN pg_namespace AS n ON n.nspname = @target_schema
            JOIN pg_roles AS owner_role ON owner_role.oid = n.nspowner
            WHERE d.datname = current_database()
            """;
            command.Parameters.AddWithValue("target_schema", configuredSearchPath);

            reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    $"PostgreSQL migration target schema '{configuredSearchPath}' does not exist.");

            var actualVersion = reader.GetString(0);
            var actualVersionNumber = reader.GetInt32(1);
            var effectiveSchema = reader.IsDBNull(2) ? null : reader.GetString(2);
            var explicitSchemas = reader.GetFieldValue<string[]>(3);
            var implicitSchemas = reader.GetFieldValue<string[]>(4);
            var temporarySchemaOid = reader.GetFieldValue<uint>(5);
            var currentRoleName = reader.GetString(6);
            var currentRoleOid = reader.GetFieldValue<uint>(7);
            var databaseOwnerOid = reader.GetFieldValue<uint>(8);
            var schemaOwnerOid = reader.GetFieldValue<uint>(9);
            var schemaOwnerName = reader.GetString(10);
            var hasSchemaUsage = reader.GetBoolean(11);
            var hasSchemaCreate = reader.GetBoolean(12);
            var hasExclusiveSchemaCreate = reader.GetBoolean(13);
            var hasExclusiveDatabaseCreate = reader.GetBoolean(14);
            var hasSafeDefaultAcl = reader.GetBoolean(15);
            var hasNoEventTriggers = reader.GetBoolean(16);
            var hasNoPublications = reader.GetBoolean(17);
            var hasNoPublicationNamespaces = reader.GetBoolean(18);
            var hasNoSubscriptions = reader.GetBoolean(19);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    $"PostgreSQL migration target schema '{configuredSearchPath}' is ambiguous.");

            if (!string.Equals(actualVersion, requiredServerVersion, StringComparison.Ordinal)
                || actualVersionNumber != requiredServerVersionNumber)
                throw new InvalidOperationException(
                    $"PostgreSQL migration requires exact server version {requiredServerVersion} " +
                    $"(server_version_num={requiredServerVersionNumber}); found {actualVersion} " +
                    $"(server_version_num={actualVersionNumber}).");

            if (!string.Equals(effectiveSchema, configuredSearchPath, StringComparison.Ordinal)
                || explicitSchemas.Length != 1
                || !string.Equals(explicitSchemas[0], configuredSearchPath, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"PostgreSQL migration requires the configured single target schema '{configuredSearchPath}' " +
                    "to be the complete effective non-system search path.");

            if (temporarySchemaOid != 0
                || implicitSchemas.Length != 2
                || !string.Equals(implicitSchemas[0], "pg_catalog", StringComparison.Ordinal)
                || !string.Equals(implicitSchemas[1], configuredSearchPath, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "PostgreSQL migration refuses a session with a temporary schema or shadow search-path namespace.");

            if (currentRoleOid != databaseOwnerOid)
                throw new InvalidOperationException(
                    $"PostgreSQL migration role '{currentRoleName}' must exactly own the current database.");

            if (schemaOwnerOid != currentRoleOid)
                throw new InvalidOperationException(
                    $"PostgreSQL migration role '{currentRoleName}' must own target schema '{configuredSearchPath}'; " +
                    $"the current schema owner is '{schemaOwnerName}'.");

            if (!hasSchemaUsage || !hasSchemaCreate || !hasExclusiveSchemaCreate
                || !hasExclusiveDatabaseCreate)
                throw new InvalidOperationException(
                    "PostgreSQL migration requires owner-only exclusive CREATE privileges on the target schema and database.");

            if (!hasSafeDefaultAcl)
                throw new InvalidOperationException(
                    "PostgreSQL migration refuses non-default owner default ACL entries for the target schema.");
            if (!hasNoEventTriggers)
                throw new InvalidOperationException(
                    "PostgreSQL migration refuses databases with an event trigger.");
            if (!hasNoPublications || !hasNoPublicationNamespaces)
                throw new InvalidOperationException(
                    "PostgreSQL migration refuses databases with a logical-replication publication.");
            if (!hasNoSubscriptions)
                throw new InvalidOperationException(
                    "PostgreSQL migration refuses databases with a logical-replication subscription.");

            validatedSchema = configuredSearchPath;
        }
        catch (Exception raw)
        {
            primaryFailure = raw;
        }

        await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                reader,
                command,
                primaryFailure)
            .ConfigureAwait(false);
        return validatedSchema!;
    }

    private static void ValidateTransactionContext(
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

        try
        {
            _ = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw TransactionFailure(exception);
        }
    }

    private static InvalidOperationException TransactionFailure(Exception? inner = null) =>
        new(
            "PostgreSQL environment validation requires an active transaction owned by the supplied connection.",
            inner);

    internal static string GetRequiredTargetSchema(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var configuredSearchPath = builder.SearchPath?.Trim();
        if (string.IsNullOrWhiteSpace(configuredSearchPath)
            || configuredSearchPath.Contains(',', StringComparison.Ordinal)
            || !SafeUnquotedIdentifierRegex().IsMatch(configuredSearchPath))
            throw new InvalidOperationException(
                "PostgreSQL migration requires one explicit, unquoted single target schema in Search Path.");
        return configuredSearchPath;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeUnquotedIdentifierRegex();
}

internal static class PostgreSqlPrimaryPreservingAsyncDisposal
{
    internal static async ValueTask DisposeReaderThenCommandAsync(
        IAsyncDisposable? reader,
        IAsyncDisposable? command,
        Exception? primaryFailure)
    {
        Exception? firstCleanupFailure = null;
        if (reader is not null)
        {
            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception raw)
            {
                firstCleanupFailure = raw;
            }
        }

        if (command is not null)
        {
            try
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception raw)
            {
                firstCleanupFailure ??= raw;
            }
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (firstCleanupFailure is not null)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                firstCleanupFailure,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                default);
        }
    }
}

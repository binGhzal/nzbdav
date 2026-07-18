using System.Data;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3PostgreSqlServerContract
{
    private const string MaximumSystemIdentifier = "18446744073709551615";

    private const string SettingsIdentitySql =
        """
        SELECT
            current_setting('log_min_messages'),
            current_setting('log_min_error_statement'),
            current_setting('log_error_verbosity'),
            current_setting('log_statement'),
            current_setting('log_duration'),
            current_setting('log_min_duration_statement'),
            current_setting('log_min_duration_sample'),
            current_setting('log_transaction_sample_rate'),
            current_setting('log_parameter_max_length'),
            current_setting('log_parameter_max_length_on_error'),
            current_setting('debug_print_parse'),
            current_setting('debug_print_rewritten'),
            current_setting('debug_print_plan'),
            current_setting('shared_preload_libraries'),
            current_setting('session_preload_libraries'),
            current_setting('local_preload_libraries'),
            current_setting('log_destination'),
            current_setting('logging_collector'),
            current_setting('fsync'),
            current_setting('full_page_writes'),
            current_setting('synchronous_commit'),
            current_setting('client_encoding'),
            current_setting('DateStyle'),
            current_setting('session_replication_role'),
            current_setting('default_transaction_read_only'),
            current_setting('transaction_read_only'),
            current_setting('TimeZone'),
            pg_my_temp_schema(),
            role_info.rolsuper,
            pg_has_role(current_user, 'pg_read_all_settings', 'USAGE'),
            CASE
                WHEN control.system_identifier < 0 THEN
                    (control.system_identifier::numeric + 18446744073709551616)::text
                ELSE control.system_identifier::text
            END,
            pg_postmaster_start_time(),
            current_database(),
            database_info.oid,
            current_schema(),
            schema_info.oid,
            current_user,
            role_info.oid,
            current_setting('server_version'),
            current_setting('server_version_num')::integer,
            pg_is_in_recovery(),
            inet_server_addr(),
            inet_server_port()
        FROM pg_control_system() AS control
        JOIN pg_database AS database_info
          ON database_info.datname = current_database()
        JOIN pg_namespace AS schema_info
          ON schema_info.nspname = current_schema()
        JOIN pg_roles AS role_info
          ON role_info.rolname = current_user
        """;

    internal static Task<TransferV3PostgreSqlTargetIdentity> ValidateAndCaptureAsync(
        NpgsqlConnection connection,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        return ValidateAndCaptureCoreAsync(
            connection,
            transaction: null,
            expectedTimeZoneId,
            commandTimeoutSeconds,
            cancellationToken);
    }

    internal static Task<TransferV3PostgreSqlTargetIdentity> ValidateAndCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (commandTimeoutSeconds <= 0)
            throw ArgumentFailure();
        if (string.IsNullOrWhiteSpace(expectedTimeZoneId))
            throw ArgumentFailure();
        if (connection is null || transaction is null)
            throw ArgumentFailure();

        ValidateTransactionContext(connection, transaction);
        return ValidateAndCaptureCoreAsync(
            connection,
            transaction,
            expectedTimeZoneId,
            commandTimeoutSeconds,
            cancellationToken);
    }

    private static async Task<TransferV3PostgreSqlTargetIdentity> ValidateAndCaptureCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (commandTimeoutSeconds <= 0)
            throw ArgumentFailure();
        if (string.IsNullOrWhiteSpace(expectedTimeZoneId))
            throw ArgumentFailure();
        if (connection is null)
            throw ArgumentFailure();

        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        TransferV3PostgreSqlTargetIdentity? validatedIdentity = null;
        Exception? primaryFailure = null;
        try
        {
            if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException();

            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SettingsIdentitySql;
            command.CommandTimeout = commandTimeoutSeconds;

            reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reader.FieldCount != 43)
                throw new InvalidOperationException();
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException();

            var settings = new TransferV3PostgreSqlServerSettingsProjection(
                logMinMessages: ReadNullableString(reader, 0),
                logMinErrorStatement: ReadNullableString(reader, 1),
                logErrorVerbosity: ReadNullableString(reader, 2),
                logStatement: ReadNullableString(reader, 3),
                logDuration: ReadNullableString(reader, 4),
                logMinDurationStatement: ReadNullableString(reader, 5),
                logMinDurationSample: ReadNullableString(reader, 6),
                logTransactionSampleRate: ReadNullableString(reader, 7),
                logParameterMaxLength: ReadNullableString(reader, 8),
                logParameterMaxLengthOnError: ReadNullableString(reader, 9),
                debugPrintParse: ReadNullableString(reader, 10),
                debugPrintRewritten: ReadNullableString(reader, 11),
                debugPrintPlan: ReadNullableString(reader, 12),
                sharedPreloadLibraries: ReadNullableString(reader, 13),
                sessionPreloadLibraries: ReadNullableString(reader, 14),
                localPreloadLibraries: ReadNullableString(reader, 15),
                logDestination: ReadNullableString(reader, 16),
                loggingCollector: ReadNullableString(reader, 17),
                fsync: ReadNullableString(reader, 18),
                fullPageWrites: ReadNullableString(reader, 19),
                synchronousCommit: ReadNullableString(reader, 20),
                clientEncoding: ReadNullableString(reader, 21),
                dateStyle: ReadNullableString(reader, 22),
                sessionReplicationRole: ReadNullableString(reader, 23),
                temporarySchemaOid: ReadNullableValue<uint>(reader, 27),
                defaultTransactionReadOnly: ReadNullableString(reader, 24),
                transactionReadOnly: ReadNullableString(reader, 25),
                timeZone: ReadNullableString(reader, 26),
                roleIsSuperuser: ReadNullableValue<bool>(reader, 28),
                hasReadAllSettings: ReadNullableValue<bool>(reader, 29));
            var identity = new TransferV3PostgreSqlIdentityProjection(
                systemIdentifier: ReadNullableString(reader, 30),
                postmasterStartTimeUtc: ReadNullableValue<DateTimeOffset>(reader, 31),
                databaseName: ReadNullableString(reader, 32),
                databaseOid: ReadNullableValue<uint>(reader, 33),
                schemaName: ReadNullableString(reader, 34),
                schemaOid: ReadNullableValue<uint>(reader, 35),
                roleName: ReadNullableString(reader, 36),
                roleOid: ReadNullableValue<uint>(reader, 37),
                serverVersion: ReadNullableString(reader, 38),
                serverVersionNumber: ReadNullableValue<int>(reader, 39),
                isInRecovery: ReadNullableValue<bool>(reader, 40),
                serverAddress: ReadNullableAddress(reader, 41),
                serverPort: ReadNullableValue<int>(reader, 42));

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException();

            validatedIdentity = ValidateProjection(
                in settings,
                in identity,
                expectedTimeZoneId);
        }
        catch (Exception raw)
        {
            primaryFailure = TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }

        try
        {
            await PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                    reader,
                    command,
                    primaryFailure)
                .ConfigureAwait(false);
        }
        catch (Exception raw) when (primaryFailure is null)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                default);
        }

        return validatedIdentity!;
    }

    internal static TransferV3PostgreSqlTargetIdentity ValidateProjection(
        in TransferV3PostgreSqlServerSettingsProjection settings,
        in TransferV3PostgreSqlIdentityProjection identity,
        string expectedTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(expectedTimeZoneId))
            throw ArgumentFailure();

        try
        {
            if (!HasExactSettings(in settings, expectedTimeZoneId)
                || !IsCanonicalSystemIdentifier(identity.SystemIdentifier)
                || identity.PostmasterStartTimeUtc is not { } postmasterStartTimeUtc
                || postmasterStartTimeUtc == DateTimeOffset.MinValue
                || postmasterStartTimeUtc == DateTimeOffset.MaxValue
                || postmasterStartTimeUtc.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(identity.DatabaseName)
                || identity.DatabaseOid is null or 0
                || string.IsNullOrWhiteSpace(identity.SchemaName)
                || identity.SchemaOid is null or 0
                || string.IsNullOrWhiteSpace(identity.RoleName)
                || identity.RoleOid is null or 0
                || !string.Equals(identity.ServerVersion, "16.14", StringComparison.Ordinal)
                || identity.ServerVersionNumber != 160014
                || identity.IsInRecovery is not false)
            {
                throw new InvalidOperationException();
            }

            var canonicalAddress = ValidateAndCanonicalizeEndpoint(
                identity.ServerAddress,
                identity.ServerPort);
            return new TransferV3PostgreSqlTargetIdentity(
                identity.SystemIdentifier!,
                postmasterStartTimeUtc,
                identity.DatabaseName!,
                identity.DatabaseOid.Value,
                identity.SchemaName!,
                identity.SchemaOid.Value,
                identity.RoleName!,
                identity.RoleOid.Value,
                identity.ServerVersion!,
                identity.ServerVersionNumber.Value,
                IsInRecovery: false,
                DefaultTransactionReadOnly: false,
                TransactionReadOnly: false,
                canonicalAddress,
                identity.ServerPort);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                default);
        }
    }

    private static bool HasExactSettings(
        in TransferV3PostgreSqlServerSettingsProjection settings,
        string expectedTimeZoneId)
    {
        return string.Equals(settings.LogMinMessages, "panic", StringComparison.Ordinal)
            && string.Equals(settings.LogMinErrorStatement, "panic", StringComparison.Ordinal)
            && string.Equals(settings.LogErrorVerbosity, "terse", StringComparison.Ordinal)
            && string.Equals(settings.LogStatement, "none", StringComparison.Ordinal)
            && string.Equals(settings.LogDuration, "off", StringComparison.Ordinal)
            && string.Equals(settings.LogMinDurationStatement, "-1", StringComparison.Ordinal)
            && string.Equals(settings.LogMinDurationSample, "-1", StringComparison.Ordinal)
            && string.Equals(settings.LogTransactionSampleRate, "0", StringComparison.Ordinal)
            && string.Equals(settings.LogParameterMaxLength, "0", StringComparison.Ordinal)
            && string.Equals(settings.LogParameterMaxLengthOnError, "0", StringComparison.Ordinal)
            && string.Equals(settings.DebugPrintParse, "off", StringComparison.Ordinal)
            && string.Equals(settings.DebugPrintRewritten, "off", StringComparison.Ordinal)
            && string.Equals(settings.DebugPrintPlan, "off", StringComparison.Ordinal)
            && string.Equals(settings.SharedPreloadLibraries, string.Empty, StringComparison.Ordinal)
            && string.Equals(settings.SessionPreloadLibraries, string.Empty, StringComparison.Ordinal)
            && string.Equals(settings.LocalPreloadLibraries, string.Empty, StringComparison.Ordinal)
            && string.Equals(settings.LogDestination, "stderr", StringComparison.Ordinal)
            && string.Equals(settings.LoggingCollector, "off", StringComparison.Ordinal)
            && string.Equals(settings.Fsync, "on", StringComparison.Ordinal)
            && string.Equals(settings.FullPageWrites, "on", StringComparison.Ordinal)
            && string.Equals(settings.SynchronousCommit, "on", StringComparison.Ordinal)
            && string.Equals(settings.ClientEncoding, "UTF8", StringComparison.Ordinal)
            && settings.DateStyle is "ISO, MDY" or "ISO, DMY" or "ISO, YMD"
            && string.Equals(settings.SessionReplicationRole, "origin", StringComparison.Ordinal)
            && settings.TemporarySchemaOid is 0
            && string.Equals(settings.DefaultTransactionReadOnly, "off", StringComparison.Ordinal)
            && string.Equals(settings.TransactionReadOnly, "off", StringComparison.Ordinal)
            && string.Equals(settings.TimeZone, expectedTimeZoneId, StringComparison.Ordinal)
            && settings.RoleIsSuperuser is false
            && settings.HasReadAllSettings is true;
    }

    private static bool IsCanonicalSystemIdentifier(string? value)
    {
        if (value is null or { Length: 0 or > 20 }
            || value[0] is < '1' or > '9')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return value.Length < MaximumSystemIdentifier.Length
            || string.CompareOrdinal(value, MaximumSystemIdentifier) <= 0;
    }

    private static string? ValidateAndCanonicalizeEndpoint(
        IPAddress? address,
        int? port)
    {
        if (address is null && port is null)
            return null;
        if (address is null
            || port is not (>= 1 and <= 65535)
            || address.AddressFamily is not (
                AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || (address.AddressFamily == AddressFamily.InterNetworkV6
                && address.ScopeId != 0))
        {
            throw new InvalidOperationException();
        }

        return address.ToString();
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T? ReadNullableValue<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static IPAddress? ReadNullableAddress(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<IPAddress>(ordinal);

    private static void ValidateTransactionContext(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        if (connection.State != ConnectionState.Open)
            throw CommandFailure();

        NpgsqlConnection? transactionConnection;
        try
        {
            transactionConnection = transaction.Connection;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw CommandFailure(exception);
        }

        if (!ReferenceEquals(transactionConnection, connection))
            throw CommandFailure();

        try
        {
            _ = transaction.IsolationLevel;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            throw CommandFailure(exception);
        }
    }

    private static TransferV3Phase4Exception CommandFailure(Exception? inner = null) =>
        TransferV3Phase4Exception.Create(
            inner ?? new InvalidOperationException(),
            TransferV3Phase4Boundary.PostgreSqlCommand);

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);

}

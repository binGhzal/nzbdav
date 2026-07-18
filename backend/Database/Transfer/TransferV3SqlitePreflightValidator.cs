using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3SqlitePreflightValidator
{
    private readonly TransferV3SourceContract _contract;

    internal TransferV3SqlitePreflightValidator()
        : this(TransferV3SourceContract.LoadEmbedded())
    {
    }

    internal TransferV3SqlitePreflightValidator(TransferV3SourceContract contract)
    {
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
    }

    internal async Task<TransferV3SqliteExportSession> OpenValidatedExportSessionAsync(
        string sqlitePath,
        TransferV3SqliteValidationOptions options,
        TransferV3SourceProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        var resources = await OpenValidationCoreAsync(
                sqlitePath, options, provenance, cancellationToken)
            .ConfigureAwait(false);
        return new TransferV3SqliteExportSession(
            _contract,
            resources.Validation,
            provenance,
            resources.SourceGuard,
            resources.BlobGuard,
            resources.Connection,
            resources.Transaction,
            options.Progress,
            options.SessionHooks);
    }

    internal async Task<TransferV3ValidatedSource> ValidateAsync(
        string sqlitePath,
        TransferV3SqliteValidationOptions options,
        CancellationToken cancellationToken = default)
    {
        var resources = await OpenValidationCoreAsync(
                sqlitePath, options, provenance: null, cancellationToken)
            .ConfigureAwait(false);
        Exception? primary = null;
        var cleanup = new List<Exception>();
        var cleanupHooksInvoked = false;
        try
        {
            resources.BlobGuard.VerifyUnchanged();
            resources.SourceGuard.VerifyUnchanged();
            await TransferV3BlobInventoryScanner.VerifyRetainedAsync(
                    resources.BlobGuard,
                    resources.Connection,
                    resources.Transaction,
                    options.Progress,
                    cancellationToken)
                .ConfigureAwait(false);
            resources.SourceGuard.VerifyUnchanged();
            resources.BlobGuard.VerifyUnchanged();
            options.Progress?.Report(new TransferV3ValidationProgress(
                "validation-precommit-verified", null, 0));

            await resources.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            options.Progress?.Report(new TransferV3ValidationProgress(
                "validation-transaction-committed", null, 0));

            cleanup.AddRange(InvokeCleanupHooks(resources, options.SessionHooks));
            cleanupHooksInvoked = true;
            resources.BlobGuard.VerifyUnchanged();
            resources.SourceGuard.VerifyUnchanged();
            await TransferV3BlobInventoryScanner.VerifyRetainedAsync(
                    resources.BlobGuard,
                    resources.Connection,
                    transaction: null,
                    progress: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            resources.SourceGuard.VerifyUnchanged();
            resources.BlobGuard.VerifyUnchanged();
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        cleanup.AddRange(await CleanupResourcesAsync(
                resources,
                cleanupHooksInvoked ? null : options.SessionHooks)
            .ConfigureAwait(false));
        if (primary is not null)
        {
            if (cleanup.Count != 0)
                primary.Data[TransferV3SqliteExportSession.CleanupFailuresDataKey] = cleanup.AsReadOnly();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0)
            throw new AggregateException("Transfer-v3 ordered validation cleanup failed.", cleanup);
        return resources.Validation;
    }

    internal static async Task<SqliteConnection> OpenPrivateIndexAsync(CancellationToken cancellationToken)
        => await OpenPrivateIndexAsync(cancellationToken, retainExportIndexes: false).ConfigureAwait(false);

    private static async Task<SqliteConnection> OpenPrivateIndexAsync(
        CancellationToken cancellationToken,
        bool retainExportIndexes)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
            PRAGMA temp_store = FILE;
            ATTACH DATABASE '' AS scratch;
            PRAGMA scratch.cache_size = -8192;
            PRAGMA scratch.cache_spill = ON;
            PRAGMA scratch.mmap_size = 0;
            CREATE TABLE scratch.normalized_keysets (
                table_name TEXT NOT NULL,
                normalized_key BLOB NOT NULL,
                source_rowid INTEGER NOT NULL,
                PRIMARY KEY (table_name, normalized_key),
                UNIQUE (table_name, source_rowid)
            );
            CREATE TABLE scratch.uuid_values (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                source_rowid INTEGER NOT NULL,
                normalized_uuid BLOB NOT NULL,
                PRIMARY KEY (table_name, column_name, source_rowid)
            );
            CREATE INDEX scratch.IX_uuid_values_lookup
                ON uuid_values(table_name, column_name, normalized_uuid);
            CREATE TABLE scratch.unique_values (
                rule_name TEXT NOT NULL,
                normalized_key BLOB NOT NULL,
                source_rowid INTEGER NOT NULL,
                PRIMARY KEY (rule_name, normalized_key)
            );
            CREATE TABLE scratch.scan_ordinals (
                table_name TEXT NOT NULL,
                source_rowid INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY (table_name, source_rowid),
                UNIQUE (table_name, ordinal)
            );
            """
                + (retainExportIndexes
                    ?
                    """
            CREATE TABLE scratch.validated_fields (
                table_name BLOB NOT NULL CHECK(typeof(table_name) = 'blob' AND length(table_name) > 0),
                source_rowid INTEGER NOT NULL CHECK(typeof(source_rowid) = 'integer'),
                column_name BLOB NOT NULL CHECK(typeof(column_name) = 'blob' AND length(column_name) > 0),
                length_bytes INTEGER NOT NULL CHECK(typeof(length_bytes) = 'integer' AND length_bytes >= 0),
                content_sha256 BLOB NOT NULL CHECK(typeof(content_sha256) = 'blob' AND length(content_sha256) = 32),
                PRIMARY KEY (table_name, source_rowid, column_name)
            ) WITHOUT ROWID;
            CREATE TABLE scratch.blob_first_shards (
                first_name BLOB NOT NULL CHECK(typeof(first_name) = 'blob' AND length(first_name) = 2),
                fingerprint BLOB NOT NULL CHECK(typeof(fingerprint) = 'blob' AND length(fingerprint) = 56),
                PRIMARY KEY (first_name)
            ) WITHOUT ROWID;
            CREATE TABLE scratch.blob_second_shards (
                first_name BLOB NOT NULL CHECK(typeof(first_name) = 'blob' AND length(first_name) = 2),
                second_name BLOB NOT NULL CHECK(typeof(second_name) = 'blob' AND length(second_name) = 2),
                fingerprint BLOB NOT NULL CHECK(typeof(fingerprint) = 'blob' AND length(fingerprint) = 56),
                PRIMARY KEY (first_name, second_name)
            ) WITHOUT ROWID;
            CREATE TABLE scratch.blob_inventory (
                normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
                first_name BLOB NOT NULL CHECK(typeof(first_name) = 'blob' AND length(first_name) = 2),
                second_name BLOB NOT NULL CHECK(typeof(second_name) = 'blob' AND length(second_name) = 2),
                length_bytes INTEGER NOT NULL CHECK(typeof(length_bytes) = 'integer' AND length_bytes >= 0),
                content_sha256 BLOB NOT NULL CHECK(typeof(content_sha256) = 'blob' AND length(content_sha256) = 32),
                file_fingerprint BLOB NOT NULL CHECK(typeof(file_fingerprint) = 'blob' AND length(file_fingerprint) = 56),
                PRIMARY KEY (normalized_uuid)
            ) WITHOUT ROWID;
            """
                    :
                    """
            CREATE TABLE scratch.blob_inventory (
                normalized_uuid BLOB NOT NULL PRIMARY KEY,
                length_bytes INTEGER NOT NULL,
                content_sha256 BLOB NOT NULL
            );
            """);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var invariant = connection.CreateCommand();
            invariant.CommandText =
                "SELECT file FROM pragma_database_list WHERE name = 'scratch';";
            var scratchFile = await invariant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            invariant.CommandText = "PRAGMA temp_store;";
            var tempStore = Convert.ToInt64(
                await invariant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (scratchFile is not string { Length: 0 }
                || raw.sqlite3_db_readonly(connection.Handle, "scratch") != 0
                || tempStore != 1)
                throw new InvalidOperationException("The Transfer-v3 private index is not an unnamed writable SQLite temp database.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task AttachReadOnlySourceAsync(
        SqliteConnection connection,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        using var sourceGuard = TransferV3SqliteSourceGuard.Open(sourcePath);
        await AttachReadOnlySourceAsync(connection, sourceGuard, cancellationToken).ConfigureAwait(false);
        sourceGuard.VerifyUnchanged();
    }

    private static async Task AttachReadOnlySourceAsync(
        SqliteConnection connection,
        TransferV3SqliteSourceGuard sourceGuard,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS source;";
        command.Parameters.AddWithValue("$path", sourceGuard.ProbeSqliteDescriptorUri());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (raw.sqlite3_db_readonly(connection.Handle, "source") != 1)
            throw new InvalidOperationException("The Transfer-v3 source attachment is not read-only.");
    }

    private async Task<OpenValidationResources> OpenValidationCoreAsync(
        string sqlitePath,
        TransferV3SqliteValidationOptions options,
        TransferV3SourceProvenance? provenance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BlobRootPath);
        if (options.MaxRowsPerBatch <= 0 || options.MaxRowsPerBatch > 4096)
            throw new ArgumentOutOfRangeException(
                nameof(options), "MaxRowsPerBatch must be between 1 and 4096.");
        if (options.MaxBytesPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBytesPerBatch must be positive.");

        TransferV3SqliteSourceGuard? sourceGuard = null;
        TransferV3BlobSourceGuard? blobGuard = null;
        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        try
        {
            sourceGuard = TransferV3SqliteSourceGuard.Open(Path.GetFullPath(sqlitePath));
            blobGuard = TransferV3BlobSourceGuard.Open(Path.GetFullPath(options.BlobRootPath));
            if (provenance is { } supplied)
                ValidateProvenance(supplied, sourceGuard, blobGuard);
            options.Progress?.Report(new TransferV3ValidationProgress(
                "source-guards-pinned", null, 0));
            cancellationToken.ThrowIfCancellationRequested();

            connection = await OpenPrivateIndexAsync(
                    cancellationToken, retainExportIndexes: true)
                .ConfigureAwait(false);
            options.Progress?.Report(new TransferV3ValidationProgress(
                "private-index-ready", null, 0));
            cancellationToken.ThrowIfCancellationRequested();
            await AttachReadOnlySourceAsync(connection, sourceGuard, cancellationToken).ConfigureAwait(false);
            sourceGuard.VerifyUnchanged();
            options.Progress?.Report(new TransferV3ValidationProgress(
                "source-attached", null, 0));

            transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await ValidateHistoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await TransferV3SqliteSchemaManifest.ValidateAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var scan = await TransferV3SqliteRawScanner.ScanAsync(
                connection, transaction, _contract, options, cancellationToken).ConfigureAwait(false);
            await ValidateForeignKeysAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateDerivedHealthStatsAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateBootstrapAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var blobs = await TransferV3BlobInventoryScanner.ScanAsync(
                    blobGuard, connection, transaction, options.Progress, cancellationToken)
                .ConfigureAwait(false);
            var informationalReferences = await TransferV3ReferenceValidator.ValidateAsync(
                    connection, transaction, _contract, cancellationToken)
                .ConfigureAwait(false);
            sourceGuard.VerifyUnchanged();
            blobGuard.VerifyUnchanged();

            var validation = new TransferV3ValidatedSource(
                _contract.ComputeSha256(),
                scan.Tables,
                informationalReferences,
                blobs,
                scan.MaxRowsPerBatch,
                scan.MaxBytesPerBatch,
                scan.MaxIoBufferBytes);
            return new OpenValidationResources(
                sourceGuard, blobGuard, connection, transaction, validation);
        }
        catch (Exception primary)
        {
            var cleanup = await CleanupPartialResourcesAsync(
                    transaction, connection, blobGuard, sourceGuard, options.SessionHooks)
                .ConfigureAwait(false);
            if (cleanup.Count != 0)
                primary.Data[TransferV3SqliteExportSession.CleanupFailuresDataKey] = cleanup.AsReadOnly();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            throw new InvalidOperationException("Unreachable validation cleanup path.");
        }
    }

    private static void ValidateProvenance(
        TransferV3SourceProvenance provenance,
        TransferV3SqliteSourceGuard sourceGuard,
        TransferV3BlobSourceGuard blobGuard)
    {
        if (provenance.DatabaseIdentity == default
            || provenance.BlobRootIdentity == default
            || provenance.DatabaseIdentity != sourceGuard.Identity
            || provenance.BlobRootIdentity != blobGuard.Identity)
        {
            throw ProvenanceFailure("identity");
        }

        var timeZoneId = provenance.SourceTimeZoneId;
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || timeZoneId.Length > 255
            || timeZoneId.Any(char.IsControl))
        {
            throw ProvenanceFailure("time-zone-shape");
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw ProvenanceFailure("time-zone-unknown");
        }
        catch (InvalidTimeZoneException)
        {
            throw ProvenanceFailure("time-zone-invalid");
        }
    }

    private static TransferV3SourceValidationException ProvenanceFailure(string reason) =>
        TransferV3SourceValidationException.Create(
            "source-provenance",
            "<source>",
            "identity",
            0,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reason)))
                .ToLowerInvariant()[..12]);

    private static async Task<List<Exception>> CleanupResourcesAsync(
        OpenValidationResources resources,
        TransferV3SqliteExportSessionHooks? hooks) =>
        await CleanupPartialResourcesAsync(
                resources.Transaction,
                resources.Connection,
                resources.BlobGuard,
                resources.SourceGuard,
                hooks)
            .ConfigureAwait(false);

    private static List<Exception> InvokeCleanupHooks(
        OpenValidationResources resources,
        TransferV3SqliteExportSessionHooks? hooks)
    {
        var failures = new List<Exception>();
        InvokeCleanupHook("transaction", resources.Transaction is not null, hooks, failures);
        InvokeCleanupHook("connection", resources.Connection is not null, hooks, failures);
        InvokeCleanupHook("blob-guard", resources.BlobGuard is not null, hooks, failures);
        InvokeCleanupHook("source-guard", resources.SourceGuard is not null, hooks, failures);
        return failures;
    }

    private static void InvokeCleanupHook(
        string step,
        bool resourceIsPresent,
        TransferV3SqliteExportSessionHooks? hooks,
        List<Exception> failures)
    {
        if (!resourceIsPresent) return;
        try
        {
            hooks?.BeforeCleanupStep?.Invoke(step);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task<List<Exception>> CleanupPartialResourcesAsync(
        SqliteTransaction? transaction,
        SqliteConnection? connection,
        TransferV3BlobSourceGuard? blobGuard,
        TransferV3SqliteSourceGuard? sourceGuard,
        TransferV3SqliteExportSessionHooks? hooks)
    {
        var failures = new List<Exception>();
        if (transaction is not null)
            await CleanupStepAsync(
                "transaction", () => transaction.DisposeAsync(), hooks, failures).ConfigureAwait(false);
        if (connection is not null)
            await CleanupStepAsync(
                "connection", () => connection.DisposeAsync(), hooks, failures).ConfigureAwait(false);
        if (blobGuard is not null)
            await CleanupStepAsync(
                "blob-guard",
                () =>
                {
                    blobGuard.Dispose();
                    return ValueTask.CompletedTask;
                },
                hooks,
                failures).ConfigureAwait(false);
        if (sourceGuard is not null)
            await CleanupStepAsync(
                "source-guard",
                () =>
                {
                    sourceGuard.Dispose();
                    return ValueTask.CompletedTask;
                },
                hooks,
                failures).ConfigureAwait(false);
        return failures;
    }

    private static async ValueTask CleanupStepAsync(
        string step,
        Func<ValueTask> cleanup,
        TransferV3SqliteExportSessionHooks? hooks,
        List<Exception> failures)
    {
        try
        {
            hooks?.BeforeCleanupStep?.Invoke(step);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private sealed record OpenValidationResources(
        TransferV3SqliteSourceGuard SourceGuard,
        TransferV3BlobSourceGuard BlobGuard,
        SqliteConnection Connection,
        SqliteTransaction Transaction,
        TransferV3ValidatedSource Validation);

    private async Task ValidateHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT typeof(MigrationId), length(CAST(MigrationId AS BLOB)), MigrationId, "
            + "typeof(ProductVersion), length(CAST(ProductVersion AS BLOB)), ProductVersion "
            + "FROM source.__EFMigrationsHistory "
            + "ORDER BY MigrationId COLLATE BINARY LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", _contract.Migrations.Count + 1);
        const int maxMigrationIdBytes = 256;
        const int maxProductVersionBytes = 64;
        var ordinal = 0L;
        string? previousId = null;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ordinal++;
                if (reader.GetString(0) != "text"
                    || reader.GetInt64(1) is < 1 or > maxMigrationIdBytes
                    || reader.GetString(3) != "text"
                    || reader.GetInt64(4) is < 1 or > maxProductVersionBytes)
                {
                    throw ContractFailure(
                        "history-shape", _contract.HistoryTable, "MigrationId", rowOrdinal: ordinal);
                }

                var id = reader.GetString(2);
                var version = reader.GetString(5);
                if (string.Equals(previousId, id, StringComparison.Ordinal))
                    throw ContractFailure(
                        "history-duplicate", _contract.HistoryTable, "MigrationId", id, ordinal);
                previousId = id;

                if (ordinal > _contract.Migrations.Count)
                    throw ContractFailure(
                        "history-future", _contract.HistoryTable, "MigrationId", id, ordinal);
                var expected = _contract.Migrations[(int)ordinal - 1];
                if (!string.Equals(id, expected.Id, StringComparison.Ordinal))
                {
                    var actualContractIndex = _contract.Migrations
                        .Select((migration, index) => (migration.Id, Index: index))
                        .FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal));
                    if (actualContractIndex.Id is null)
                        throw ContractFailure(
                            "history-future", _contract.HistoryTable, "MigrationId", id, ordinal);
                    if (actualContractIndex.Index >= ordinal)
                        throw ContractFailure(
                            "history-missing", _contract.HistoryTable, "MigrationId", expected.Id, ordinal);
                    throw ContractFailure(
                        "history-order", _contract.HistoryTable, "MigrationId", id, ordinal);
                }

                if (string.IsNullOrWhiteSpace(version)
                    || !expected.AllowedProductVersions.Contains(version, StringComparer.Ordinal))
                {
                    throw ContractFailure(
                        "history-product-version", _contract.HistoryTable, "ProductVersion",
                        id + "\0" + version, ordinal);
                }
            }
        }
        catch (TransferV3SourceValidationException)
        {
            throw;
        }
        catch (SqliteException)
        {
            throw ContractFailure("history-shape", _contract.HistoryTable, "MigrationId");
        }
        if (ordinal < _contract.Migrations.Count)
            throw ContractFailure(
                "history-missing", _contract.HistoryTable, "MigrationId",
                _contract.Migrations[(int)ordinal].Id, ordinal + 1);
    }

    private static async Task ValidateForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT violation.\"table\", violation.rowid, violation.parent, violation.fkid, "
            + "ordinal.ordinal FROM pragma_foreign_key_check(NULL, 'source') AS violation "
            + "LEFT JOIN scratch.scan_ordinals AS ordinal "
            + "ON ordinal.table_name = violation.\"table\" "
            + "AND ordinal.source_rowid = violation.rowid "
            + "ORDER BY violation.\"table\" COLLATE BINARY, ordinal.ordinal, "
            + "violation.parent COLLATE BINARY, violation.fkid LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
        var table = reader.IsDBNull(0) ? "" : reader.GetString(0);
        var sourceRowId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        if (sourceRowId is null || reader.IsDBNull(4))
            throw new InvalidDataException("A foreign-key violation was not covered by the canonical raw scan.");
        var ordinal = reader.GetInt64(4);
        var canonical = string.Join(
            "|",
            table,
            ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture));
        throw TransferV3SourceValidationException.Create(
            "foreign-key", table, "<foreign-key>", ordinal, DigestPrefix(canonical));
    }

    private static async Task ValidateDerivedHealthStatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH expected AS (
                SELECT
                    CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER)
                        AS DateStartInclusive,
                    CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER)
                        AS DateEndExclusive,
                    Result,
                    RepairStatus,
                    count(*) AS Count
                FROM source.HealthCheckResults
                GROUP BY 1, 2, 3, 4
            ), missing AS (
                SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count FROM expected
                EXCEPT
                SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count
                FROM source.HealthCheckStats
            ), extra AS (
                SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count
                FROM source.HealthCheckStats
                EXCEPT
                SELECT DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count FROM expected
            ), mismatch AS (
                SELECT * FROM missing
                UNION ALL
                SELECT * FROM extra
            )
            SELECT typeof(DateStartInclusive), DateStartInclusive,
                   typeof(DateEndExclusive), DateEndExclusive,
                   typeof(Result), Result,
                   typeof(RepairStatus), RepairStatus,
                   typeof(Count), Count
            FROM mismatch
            ORDER BY DateStartInclusive IS NOT NULL, DateStartInclusive,
                     DateEndExclusive IS NOT NULL, DateEndExclusive,
                     Result IS NOT NULL, Result,
                     RepairStatus IS NOT NULL, RepairStatus,
                     Count IS NOT NULL, Count
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
        var values = new long[5];
        for (var index = 0; index < values.Length; index++)
        {
            var storageOrdinal = index * 2;
            var valueOrdinal = storageOrdinal + 1;
            if (reader.GetString(storageOrdinal) != "integer" || reader.IsDBNull(valueOrdinal))
                throw DerivedFailure("bucket-storage");
            values[index] = reader.GetInt64(valueOrdinal);
        }
        if (values[0] < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || values[0] > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || values[1] < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || values[1] > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || values.Skip(2).Any(value => value is < int.MinValue or > int.MaxValue))
            throw DerivedFailure("bucket-range");
        var canonical = string.Join(
            "|",
            values.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        throw TransferV3SourceValidationException.Create(
            "derived-state", "HealthCheckStats", "Count", 1, DigestPrefix(canonical));
    }

    private static TransferV3SourceValidationException DerivedFailure(string reason) =>
        TransferV3SourceValidationException.Create(
            "derived-state", "HealthCheckStats", "Count", 1, DigestPrefix(reason));

    private async Task ValidateBootstrapAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT ConfigName, ConfigValue FROM source.ConfigItems "
                + "WHERE ConfigName IN ('api.key', 'api.strm-key', 'database.import-state') "
                + "ORDER BY ConfigName COLLATE BINARY;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (!config.TryAdd(name, reader.GetString(1)))
                    throw ContractFailure("bootstrap-config", "ConfigItems", "ConfigName", name);
            }
        }
        if (config.ContainsKey("database.import-state"))
            throw ContractFailure("reserved-config", "ConfigItems", "ConfigName", "database.import-state");
        foreach (var required in _contract.Bootstrap.Config)
        {
            if (!config.TryGetValue(required.Name, out var value)
                || !MatchesEntireBootstrapPattern(value, required.Pattern))
            {
                throw ContractFailure("bootstrap-config", "ConfigItems", "ConfigValue", required.Name);
            }
        }
        if (config.Values.Distinct(StringComparer.Ordinal).Count() != config.Count)
            throw ContractFailure("bootstrap-config", "ConfigItems", "ConfigValue", "duplicate-secrets");

        foreach (var root in _contract.Bootstrap.Roots)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id, ParentId, Name, IdPrefix, CreatedAt, Type, SubType, Path,
                       FileSize, ReleaseDate, LastHealthCheck, NextHealthCheck,
                       HistoryItemId, FileBlobId, NzbBlobId
                FROM source.DavItems
                WHERE lower(Id) = $id;
                """;
            command.Parameters.AddWithValue("$id", root.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !RootMatches(reader, root)
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw ContractFailure("bootstrap-root", "DavItems", "Id", root.Id);
            }
        }
    }

    private static bool MatchesEntireBootstrapPattern(string value, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && match.Index == 0 && match.Length == value.Length;
    }

    private static bool RootMatches(SqliteDataReader reader, TransferV3BootstrapRootContract root)
    {
        if (!Guid.TryParseExact(reader.GetString(0), "D", out var id)
            || !string.Equals(id.ToString("D"), root.Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (root.ParentId is null)
        {
            if (!reader.IsDBNull(1)) return false;
        }
        else if (reader.IsDBNull(1)
                 || !Guid.TryParseExact(reader.GetString(1), "D", out var parent)
                 || !string.Equals(parent.ToString("D"), root.ParentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return reader.GetString(2) == root.Name
               && reader.GetString(3) == root.IdPrefix
               && reader.GetString(4) == root.CreatedAt
               && reader.GetInt64(5) == root.Type
               && reader.GetInt64(6) == root.SubType
               && reader.GetString(7) == root.Path
               && Enumerable.Range(8, 7).All(reader.IsDBNull);
    }

    private static TransferV3SourceValidationException ContractFailure(
        string code,
        string table,
        string column,
        string value = "",
        long rowOrdinal = 0) =>
        TransferV3SourceValidationException.Create(
            code,
            table,
            column,
            rowOrdinal,
            DigestPrefix(value));

    private static string DigestPrefix(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

}

internal sealed record TransferV3SqliteValidationOptions(
    string BlobRootPath,
    int MaxRowsPerBatch = 256,
    long MaxBytesPerBatch = 4 * 1024 * 1024,
    IProgress<TransferV3ValidationProgress>? Progress = null)
{
    internal TransferV3SqliteExportSessionHooks? SessionHooks { get; init; }
}

internal sealed record TransferV3ValidationProgress(
    string Phase,
    string? Table,
    long RowsProcessed);

internal sealed record TransferV3ValidatedSource(
    string ContractSha256,
    IReadOnlyList<TransferV3ValidatedTable> Tables,
    IReadOnlyList<TransferV3ReferenceSummary> InformationalReferences,
    TransferV3BlobInventory Blobs,
    int MaxObservedRowsPerBatch,
    long MaxObservedBytesPerBatch,
    int MaxObservedIoBufferBytes);

internal sealed record TransferV3ValidatedTable(
    string Name,
    long RowCount,
    IReadOnlyList<TransferV3KeyComponentContract> Keyset,
    string SqliteOrderExpression,
    string PostgreSqlOrderExpression);

internal sealed record TransferV3ReferenceSummary(
    string Name,
    long UnresolvedCount,
    string UnresolvedSha256);

internal sealed record TransferV3BlobInventory(
    long Count,
    long TotalBytes,
    string Sha256);

internal sealed class TransferV3SourceValidationException : Exception
{
    internal TransferV3SourceValidationException(string message) : base(message)
    {
    }

    internal static TransferV3SourceValidationException Create(
        string code,
        string table,
        string column,
        long rowOrdinal,
        string digestPrefix) =>
        new($"Transfer-v3 source validation failed: code={code} table={table} column={column} row={rowOrdinal} digest={digestPrefix}");
}

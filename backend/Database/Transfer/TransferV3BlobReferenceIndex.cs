using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NzbWebDAV.Database.Transfer;

internal sealed record TransferV3BlobReferenceIndexHooks(
    Action<string>? BeforeFaultPoint = null,
    Action<string, byte[]>? ObserveOwnedParameterBufferForTesting = null);

internal sealed record TransferV3BlobReferenceIndexDiagnostics(
    string VerificationFile,
    long MainTableCount,
    IReadOnlyList<string> TableNames,
    long TempStore,
    long ForeignKeys,
    long TrustedSchema,
    long CacheSize,
    bool CacheSpill,
    long MmapSize,
    long SecureDelete,
    long Synchronous,
    string JournalMode,
    bool IsWritable,
    long PageCount,
    long PageSize);

internal sealed record TransferV3BlobReferenceFactCounts(
    long RowKeys,
    long UniqueValues,
    long UuidValues,
    long HardBlobReferences,
    long InformationalFacts,
    long PhysicalBlobs,
    long DavMetadata,
    long LegacyMetadata,
    long HealthBuckets,
    long BootstrapConfigSecrets,
    long BootstrapRootMarkers);

internal sealed record TransferV3UnresolvedBlobReference(
    int TableOrdinal,
    int ReferenceOrdinal,
    long RowOrdinal);

internal sealed class TransferV3BlobReferenceIndex : IAsyncDisposable
{
    private const string SavepointName = "transfer_batch";

    private static readonly string[] ExpectedTableNames =
    [
        "bootstrap_config", "bootstrap_roots", "dav_metadata", "hard_blob_refs",
        "health_buckets", "informational_facts", "legacy_metadata", "physical_blobs",
        "row_keys", "unique_values", "uuid_values",
    ];

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly TransferV3BlobReferenceIndexHooks? _hooks;
    private readonly SqliteCommand _addRowKey;
    private readonly SqliteCommand _addUniqueValue;
    private readonly SqliteCommand _addUuidValue;
    private readonly SqliteCommand _addHardBlobReference;
    private readonly SqliteCommand _addInformationalFact;
    private readonly SqliteCommand _addPhysicalBlob;
    private readonly SqliteCommand _addDavMetadata;
    private readonly SqliteCommand _addLegacyMetadata;
    private readonly SqliteCommand _addHealthBucket;
    private readonly SqliteCommand _addBootstrapConfig;
    private readonly SqliteCommand _addBootstrapRoot;
    private readonly SqliteCommand[] _commands;
    private bool _batchActive;
    private int _batchTableOrdinal;
    private bool _disposed;

    private TransferV3BlobReferenceIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3BlobReferenceIndexHooks? hooks)
    {
        _connection = connection;
        _transaction = transaction;
        _hooks = hooks;
        _addRowKey = CreatePreparedCommand(
            "INSERT INTO verification.row_keys(table_ordinal, row_ordinal, key_sha256) "
            + "VALUES ($table, $row, $sha);",
            ("$table", SqliteType.Integer, 0), ("$row", SqliteType.Integer, 0),
            ("$sha", SqliteType.Blob, 32));
        _addUniqueValue = CreatePreparedCommand(
            "INSERT INTO verification.unique_values(table_ordinal, rule_ordinal, row_ordinal, key_sha256) "
            + "VALUES ($table, $rule, $row, $sha);",
            ("$table", SqliteType.Integer, 0), ("$rule", SqliteType.Integer, 0),
            ("$row", SqliteType.Integer, 0), ("$sha", SqliteType.Blob, 32));
        _addUuidValue = CreatePreparedCommand(
            "INSERT INTO verification.uuid_values(table_ordinal, column_ordinal, row_ordinal, normalized_uuid) "
            + "VALUES ($table, $column, $row, $uuid);",
            ("$table", SqliteType.Integer, 0), ("$column", SqliteType.Integer, 0),
            ("$row", SqliteType.Integer, 0), ("$uuid", SqliteType.Blob, 16));
        _addHardBlobReference = CreatePreparedCommand(
            "INSERT INTO verification.hard_blob_refs(table_ordinal, reference_ordinal, row_ordinal, normalized_uuid) "
            + "VALUES ($table, $reference, $row, $uuid);",
            ("$table", SqliteType.Integer, 0), ("$reference", SqliteType.Integer, 0),
            ("$row", SqliteType.Integer, 0), ("$uuid", SqliteType.Blob, 16));
        _addInformationalFact = CreatePreparedCommand(
            "INSERT INTO verification.informational_facts("
            + "table_ordinal, reference_ordinal, row_ordinal, owner_uuid, target_uuid, discriminator) "
            + "VALUES ($table, $reference, $row, $owner, $target, $discriminator);",
            ("$table", SqliteType.Integer, 0), ("$reference", SqliteType.Integer, 0),
            ("$row", SqliteType.Integer, 0), ("$owner", SqliteType.Blob, 16),
            ("$target", SqliteType.Blob, 16), ("$discriminator", SqliteType.Integer, 0));
        _addPhysicalBlob = CreatePreparedCommand(
            "INSERT INTO verification.physical_blobs(normalized_uuid, length_bytes, content_sha256) "
            + "VALUES ($uuid, $length, $sha);",
            ("$uuid", SqliteType.Blob, 16), ("$length", SqliteType.Integer, 0),
            ("$sha", SqliteType.Blob, 32));
        _addDavMetadata = CreatePreparedCommand(
            "INSERT INTO verification.dav_metadata("
            + "row_ordinal, normalized_uuid, parent_uuid, type_value, subtype_value, file_blob_uuid) "
            + "VALUES ($row, $uuid, $parent, $type, $subtype, $blob);",
            ("$row", SqliteType.Integer, 0), ("$uuid", SqliteType.Blob, 16),
            ("$parent", SqliteType.Blob, 16), ("$type", SqliteType.Integer, 0),
            ("$subtype", SqliteType.Integer, 0), ("$blob", SqliteType.Blob, 16));
        _addLegacyMetadata = CreatePreparedCommand(
            "INSERT INTO verification.legacy_metadata(table_ordinal, row_ordinal, normalized_uuid) "
            + "VALUES ($table, $row, $uuid);",
            ("$table", SqliteType.Integer, 0), ("$row", SqliteType.Integer, 0),
            ("$uuid", SqliteType.Blob, 16));
        _addHealthBucket = CreatePreparedCommand(
            "INSERT INTO verification.health_buckets("
            + "date_start, date_end, result_value, repair_status, count_value) "
            + "VALUES ($start, $end, $result, $status, $count) "
            + "ON CONFLICT(date_start, date_end, result_value, repair_status) "
            + "DO UPDATE SET count_value = count_value + excluded.count_value;",
            ("$start", SqliteType.Integer, 0), ("$end", SqliteType.Integer, 0),
            ("$result", SqliteType.Integer, 0), ("$status", SqliteType.Integer, 0),
            ("$count", SqliteType.Integer, 0));
        _addBootstrapConfig = CreatePreparedCommand(
            "INSERT INTO verification.bootstrap_config(rule_ordinal, secret_sha256) VALUES ($rule, $sha);",
            ("$rule", SqliteType.Integer, 0), ("$sha", SqliteType.Blob, 32));
        _addBootstrapRoot = CreatePreparedCommand(
            "INSERT INTO verification.bootstrap_roots(rule_ordinal, marker_sha256) VALUES ($rule, $sha);",
            ("$rule", SqliteType.Integer, 0), ("$sha", SqliteType.Blob, 32));
        _commands =
        [
            _addRowKey, _addUniqueValue, _addUuidValue, _addHardBlobReference,
            _addInformationalFact, _addPhysicalBlob, _addDavMetadata, _addLegacyMetadata,
            _addHealthBucket, _addBootstrapConfig, _addBootstrapRoot,
        ];
    }

    internal static async Task<TransferV3BlobReferenceIndex> CreateAsync(
        TransferV3BlobReferenceIndexHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        SqliteConnection? connection = null;
        var phase = "index-open";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeHook(hooks, "before-open");
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            phase = "index-write";
            cancellationToken.ThrowIfCancellationRequested();
            InvokeHook(hooks, "before-schema-write");
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = SchemaSql;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            phase = "index-query";
            await VerifyStorageInvariantsAsync(connection, cancellationToken).ConfigureAwait(false);
            phase = "index-write";
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return new TransferV3BlobReferenceIndex(connection, transaction, hooks);
            }
            catch
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
                await DisposeFailedConnectionNoThrowAsync(connection, hooks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            var cleanupCodes = connection is null
                ? Array.Empty<string>()
                : await DisposeFailedConnectionNoThrowAsync(connection, hooks).ConfigureAwait(false);
            throw TransferV3BlobReferenceIndexException.Create(phase, cleanupCodes);
        }
    }

    internal void BeginBatch(
        int tableOrdinal,
        int batchOrdinal,
        CancellationToken cancellationToken = default)
    {
        ExecuteWrite(() =>
        {
            RequireNonNegative(tableOrdinal);
            RequireNonNegative(batchOrdinal);
            if (_batchActive)
                throw TransferV3BlobReferenceIndexException.Create("index-batch-state");
            _transaction.Save(SavepointName);
            _batchTableOrdinal = tableOrdinal;
            _batchActive = true;
        }, cancellationToken);
    }

    internal void AddRowKey(
        long rowOrdinal,
        ReadOnlySpan<byte> keySha256,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, keySha256, 32);
        ExecuteInsert(
            _addRowKey,
            cancellationToken,
            _batchTableOrdinal,
            rowOrdinal,
            keySha256.ToArray());
    }

    internal void AddUniqueValue(
        int ruleOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> keySha256,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, keySha256, 32);
        RequireNonNegative(ruleOrdinal);
        ExecuteInsert(
            _addUniqueValue,
            cancellationToken,
            _batchTableOrdinal,
            ruleOrdinal,
            rowOrdinal,
            keySha256.ToArray());
    }

    internal void AddUuidValue(
        int columnOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> normalizedUuid,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, normalizedUuid, 16);
        RequireNonNegative(columnOrdinal);
        ExecuteInsert(
            _addUuidValue,
            cancellationToken,
            _batchTableOrdinal,
            columnOrdinal,
            rowOrdinal,
            normalizedUuid.ToArray());
    }

    internal void AddHardBlobReference(
        int referenceOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> normalizedUuid,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, normalizedUuid, 16);
        RequireNonNegative(referenceOrdinal);
        ExecuteInsert(
            _addHardBlobReference,
            cancellationToken,
            _batchTableOrdinal,
            referenceOrdinal,
            rowOrdinal,
            normalizedUuid.ToArray());
    }

    internal void AddInformationalFact(
        int referenceOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> ownerUuid,
        ReadOnlySpan<byte> targetUuid,
        long? discriminator,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, ownerUuid, 16);
        RequireShape(targetUuid, 16);
        RequireNonNegative(referenceOrdinal);
        var ownerCopy = ownerUuid.ToArray();
        byte[]? targetCopy = null;
        try
        {
            targetCopy = targetUuid.ToArray();
            ExecuteInsert(
                _addInformationalFact,
                cancellationToken,
                _batchTableOrdinal,
                referenceOrdinal,
                rowOrdinal,
                ownerCopy,
                targetCopy,
                discriminator.HasValue ? discriminator.Value : DBNull.Value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerCopy);
            if (targetCopy is not null)
                CryptographicOperations.ZeroMemory(targetCopy);
        }
    }

    internal void AddPhysicalBlob(
        ReadOnlySpan<byte> normalizedUuid,
        long lengthBytes,
        ReadOnlySpan<byte> contentSha256,
        CancellationToken cancellationToken = default)
    {
        RequireActiveBatch();
        RequireShape(normalizedUuid, 16);
        RequireShape(contentSha256, 32);
        RequireNonNegative(lengthBytes);
        var uuidCopy = normalizedUuid.ToArray();
        byte[]? shaCopy = null;
        try
        {
            shaCopy = contentSha256.ToArray();
            ExecuteInsert(
                _addPhysicalBlob,
                cancellationToken,
                uuidCopy,
                lengthBytes,
                shaCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uuidCopy);
            if (shaCopy is not null)
                CryptographicOperations.ZeroMemory(shaCopy);
        }
    }

    internal void AddDavMetadata(
        long rowOrdinal,
        ReadOnlySpan<byte> normalizedUuid,
        byte[]? parentId,
        long type,
        long subType,
        byte[]? fileBlobId,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, normalizedUuid, 16);
        RequireOptionalShape(parentId, 16);
        RequireOptionalShape(fileBlobId, 16);
        var uuidCopy = normalizedUuid.ToArray();
        byte[]? parentCopy = null;
        byte[]? fileBlobCopy = null;
        try
        {
            if (parentId is not null) parentCopy = parentId.ToArray();
            if (fileBlobId is not null) fileBlobCopy = fileBlobId.ToArray();
            ExecuteInsert(
                _addDavMetadata,
                cancellationToken,
                rowOrdinal,
                uuidCopy,
                parentCopy is null ? DBNull.Value : parentCopy,
                type,
                subType,
                fileBlobCopy is null ? DBNull.Value : fileBlobCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uuidCopy);
            if (parentCopy is not null)
                CryptographicOperations.ZeroMemory(parentCopy);
            if (fileBlobCopy is not null)
                CryptographicOperations.ZeroMemory(fileBlobCopy);
        }
    }

    internal void AddLegacyMetadata(
        int tableOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> normalizedUuid,
        CancellationToken cancellationToken = default)
    {
        RequireBatchAndShape(rowOrdinal, normalizedUuid, 16);
        RequireNonNegative(tableOrdinal);
        ExecuteInsert(
            _addLegacyMetadata,
            cancellationToken,
            tableOrdinal,
            rowOrdinal,
            normalizedUuid.ToArray());
    }

    internal void AddHealthBucket(
        long dateStart,
        long dateEnd,
        long result,
        long repairStatus,
        long count,
        CancellationToken cancellationToken = default)
    {
        RequireActiveBatch();
        RequireNonNegative(count);
        ExecuteInsert(
            _addHealthBucket,
            cancellationToken,
            dateStart,
            dateEnd,
            result,
            repairStatus,
            count);
    }

    internal void AddBootstrapConfigSecret(
        int ruleOrdinal,
        ReadOnlySpan<byte> secretSha256,
        CancellationToken cancellationToken = default)
    {
        RequireActiveBatch();
        RequireNonNegative(ruleOrdinal);
        RequireShape(secretSha256, 32);
        ExecuteInsert(
            _addBootstrapConfig,
            cancellationToken,
            ruleOrdinal,
            secretSha256.ToArray());
    }

    internal void AddBootstrapRootMarker(
        int ruleOrdinal,
        ReadOnlySpan<byte> markerSha256,
        CancellationToken cancellationToken = default)
    {
        RequireActiveBatch();
        RequireNonNegative(ruleOrdinal);
        RequireShape(markerSha256, 32);
        ExecuteInsert(
            _addBootstrapRoot,
            cancellationToken,
            ruleOrdinal,
            markerSha256.ToArray());
    }

    internal void CommitBatch(CancellationToken cancellationToken = default)
    {
        ExecuteWrite(() =>
        {
            RequireActiveBatch();
            _transaction.Release(SavepointName);
            _batchActive = false;
        }, cancellationToken);
    }

    internal void AbortBatchNoThrow()
    {
        if (_disposed || !_batchActive) return;
        try
        {
            _transaction.Rollback(SavepointName);
            _transaction.Release(SavepointName);
        }
        catch
        {
            // The outer transaction and connection are still owned by this object and are
            // unconditionally rolled back/closed during disposal. Abort is deliberately no-throw
            // so a parser failure remains the primary failure.
        }
        finally
        {
            _batchActive = false;
        }
    }

    internal TransferV3BlobReferenceFactCounts GetFactCounts(
        CancellationToken cancellationToken = default) =>
        ExecuteQuery(() =>
        {
            using var command = CreateQueryCommand(
                "SELECT "
                + "(SELECT count(*) FROM verification.row_keys), "
                + "(SELECT count(*) FROM verification.unique_values), "
                + "(SELECT count(*) FROM verification.uuid_values), "
                + "(SELECT count(*) FROM verification.hard_blob_refs), "
                + "(SELECT count(*) FROM verification.informational_facts), "
                + "(SELECT count(*) FROM verification.physical_blobs), "
                + "(SELECT count(*) FROM verification.dav_metadata), "
                + "(SELECT count(*) FROM verification.legacy_metadata), "
                + "(SELECT count(*) FROM verification.health_buckets), "
                + "(SELECT count(*) FROM verification.bootstrap_config), "
                + "(SELECT count(*) FROM verification.bootstrap_roots);");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw TransferV3BlobReferenceIndexException.Create("index-query");
            return new TransferV3BlobReferenceFactCounts(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10));
        }, cancellationToken);

    internal bool ContainsRowKey(
        int tableOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> keySha256,
        CancellationToken cancellationToken = default)
    {
        RequireNonNegative(tableOrdinal);
        RequireNonNegative(rowOrdinal);
        RequireShape(keySha256, 32);
        var key = keySha256.ToArray();
        try
        {
            ObserveOwnedParameterBuffer("query-parameter", key);
            return ExecuteQuery(() => Exists(
                "SELECT 1 FROM verification.row_keys "
                + "WHERE table_ordinal = $table AND row_ordinal = $row AND key_sha256 = $sha;",
                ("$table", tableOrdinal), ("$row", rowOrdinal), ("$sha", key)), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal bool ContainsUuidValue(
        int tableOrdinal,
        int columnOrdinal,
        long rowOrdinal,
        ReadOnlySpan<byte> normalizedUuid,
        CancellationToken cancellationToken = default)
    {
        RequireNonNegative(tableOrdinal);
        RequireNonNegative(columnOrdinal);
        RequireNonNegative(rowOrdinal);
        RequireShape(normalizedUuid, 16);
        var uuid = normalizedUuid.ToArray();
        try
        {
            ObserveOwnedParameterBuffer("query-parameter", uuid);
            return ExecuteQuery(() => Exists(
                "SELECT 1 FROM verification.uuid_values WHERE table_ordinal = $table "
                + "AND column_ordinal = $column AND row_ordinal = $row AND normalized_uuid = $uuid;",
                ("$table", tableOrdinal), ("$column", columnOrdinal),
                ("$row", rowOrdinal), ("$uuid", uuid)), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uuid);
        }
    }

    internal bool ContainsPhysicalBlob(
        ReadOnlySpan<byte> normalizedUuid,
        long lengthBytes,
        ReadOnlySpan<byte> contentSha256,
        CancellationToken cancellationToken = default)
    {
        RequireShape(normalizedUuid, 16);
        RequireShape(contentSha256, 32);
        RequireNonNegative(lengthBytes);
        var uuid = normalizedUuid.ToArray();
        byte[]? sha = null;
        try
        {
            sha = contentSha256.ToArray();
            ObserveOwnedParameterBuffer("query-parameter", uuid);
            ObserveOwnedParameterBuffer("query-parameter", sha);
            return ExecuteQuery(() => Exists(
                "SELECT 1 FROM verification.physical_blobs WHERE normalized_uuid = $uuid "
                + "AND length_bytes = $length AND content_sha256 = $sha;",
                ("$uuid", uuid), ("$length", lengthBytes), ("$sha", sha)), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(uuid);
            if (sha is not null)
                CryptographicOperations.ZeroMemory(sha);
        }
    }

    internal TransferV3UnresolvedBlobReference? FindFirstUnresolvedHardBlobReference(
        CancellationToken cancellationToken = default) =>
        ExecuteQuery(() =>
        {
            using var command = CreateQueryCommand(
                "SELECT reference.table_ordinal, reference.reference_ordinal, "
                + "reference.row_ordinal "
                + "FROM verification.hard_blob_refs AS reference "
                + "LEFT JOIN verification.physical_blobs AS blob "
                + "ON blob.normalized_uuid = reference.normalized_uuid "
                + "WHERE blob.normalized_uuid IS NULL "
                + "ORDER BY reference.table_ordinal, reference.reference_ordinal, "
                + "reference.row_ordinal LIMIT 1;");
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new TransferV3UnresolvedBlobReference(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2))
                : null;
        }, cancellationToken);

    internal TransferV3BlobReferenceIndexDiagnostics GetDiagnostics(
        CancellationToken cancellationToken = default) =>
        ExecuteQuery(() =>
        {
            var verificationFile = Convert.ToString(Scalar(
                "SELECT file FROM pragma_database_list WHERE name = 'verification';"),
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var names = new List<string>();
            using (var command = CreateQueryCommand(
                       "SELECT name FROM verification.sqlite_schema "
                       + "WHERE type = 'table' ORDER BY name COLLATE BINARY;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    names.Add(reader.GetString(0));
            return new TransferV3BlobReferenceIndexDiagnostics(
                verificationFile,
                IntegerScalar("SELECT count(*) FROM main.sqlite_schema WHERE type = 'table';"),
                new ReadOnlyCollection<string>(names),
                IntegerScalar("PRAGMA temp_store;"),
                IntegerScalar("PRAGMA foreign_keys;"),
                IntegerScalar("PRAGMA trusted_schema;"),
                IntegerScalar("PRAGMA verification.cache_size;"),
                IntegerScalar("PRAGMA verification.cache_spill;") != 0,
                IntegerScalar("PRAGMA verification.mmap_size;"),
                IntegerScalar("PRAGMA verification.secure_delete;"),
                IntegerScalar("PRAGMA verification.synchronous;"),
                Convert.ToString(Scalar("PRAGMA verification.journal_mode;"),
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                raw.sqlite3_db_readonly(_connection.Handle, "verification") == 0,
                IntegerScalar("PRAGMA verification.page_count;"),
                IntegerScalar("PRAGMA verification.page_size;"));
        }, cancellationToken);

    internal void ValidateSemanticClosure(
        TransferV3SourceContract contract,
        TransferV3Manifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(manifest);
        RequireOpen();
        cancellationToken.ThrowIfCancellationRequested();
        using var interruptRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            InvokeHook(_hooks, "before-query");
            cancellationToken.ThrowIfCancellationRequested();
            ValidateHardBlobClosure();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReferenceClosure(contract);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateConditionalNzbNameClosure(contract);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDavMetadata(contract);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBootstrap(contract);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInformationalDigests(contract, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDerivedHealth(contract, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3SnapshotVerificationException)
        {
            throw;
        }
        catch (TransferV3BlobReferenceIndexException)
        {
            throw;
        }
        catch
        {
            throw TransferV3BlobReferenceIndexException.Create("index-query");
        }
    }

    private void ValidateHardBlobClosure()
    {
        using var command = CreateQueryCommand(
            "SELECT 1 FROM verification.hard_blob_refs AS reference "
            + "LEFT JOIN verification.physical_blobs AS blob "
            + "ON blob.normalized_uuid = reference.normalized_uuid "
            + "WHERE blob.normalized_uuid IS NULL LIMIT 1;");
        if (command.ExecuteScalar() is not null)
            throw SemanticFailure("reference-hard");
    }

    private void ValidateReferenceClosure(TransferV3SourceContract contract)
    {
        var referenceOrdinal = 0;
        for (var tableOrdinal = 0; tableOrdinal < contract.Tables.Count; tableOrdinal++)
        {
            var table = contract.Tables[tableOrdinal];
            foreach (var reference in table.References)
            {
                switch (reference.Policy)
                {
                    case TransferV3ReferencePolicy.DeclaredForeignKeyHard:
                        if (HasUnresolvedReference(
                                contract,
                                tableOrdinal,
                                table,
                                reference))
                            throw SemanticFailure("foreign-key");
                        break;
                    case TransferV3ReferencePolicy.ApplicationHard:
                        if (HasUnresolvedReference(
                                contract,
                                tableOrdinal,
                                table,
                                reference))
                            throw SemanticFailure("reference-hard");
                        break;
                    case TransferV3ReferencePolicy.StateAwareHard:
                        if (HasUnresolvedReference(
                                contract,
                                tableOrdinal,
                                table,
                                reference))
                            throw SemanticFailure("reference-state");
                        break;
                }
                referenceOrdinal++;
            }
        }
        if (referenceOrdinal != 33)
            throw SemanticFailure("reference-contract");
    }

    private bool HasUnresolvedReference(
        TransferV3SourceContract contract,
        int tableOrdinal,
        TransferV3TableContract table,
        TransferV3ReferenceContract reference)
    {
        if (reference.Columns.Count != 1
            || reference.PrincipalTables.Count == 0
            || reference.PrincipalColumns.Count == 0)
        {
            throw SemanticFailure("reference-contract");
        }
        var referenceColumn = FindColumnOrdinal(table, reference.Columns[0]);
        var clauses = new List<string>(reference.PrincipalTables.Count);
        using var command = CreateQueryCommand(string.Empty);
        command.Parameters.AddWithValue("$referenceTable", tableOrdinal);
        command.Parameters.AddWithValue("$referenceColumn", referenceColumn);
        for (var index = 0; index < reference.PrincipalTables.Count; index++)
        {
            var principalName = reference.PrincipalTables[index];
            if (string.Equals(principalName, "@blob", StringComparison.Ordinal))
                throw SemanticFailure("reference-contract");
            var principalTable = FindTableOrdinal(contract, principalName);
            var principalColumnName = reference.PrincipalColumns[
                Math.Min(index, reference.PrincipalColumns.Count - 1)];
            var principalColumn = FindColumnOrdinal(
                contract.Tables[principalTable],
                principalColumnName);
            var tableParameter = $"$principalTable{index}";
            var columnParameter = $"$principalColumn{index}";
            command.Parameters.AddWithValue(tableParameter, principalTable);
            command.Parameters.AddWithValue(columnParameter, principalColumn);
            clauses.Add(
                "EXISTS (SELECT 1 FROM verification.uuid_values AS principal "
                + $"WHERE principal.table_ordinal = {tableParameter} "
                + $"AND principal.column_ordinal = {columnParameter} "
                + "AND principal.normalized_uuid = reference.normalized_uuid)");
        }
        command.CommandText =
            "SELECT 1 FROM verification.uuid_values AS reference "
            + "WHERE reference.table_ordinal = $referenceTable "
            + "AND reference.column_ordinal = $referenceColumn "
            + $"AND NOT ({string.Join(" OR ", clauses)}) LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    private void ValidateConditionalNzbNameClosure(TransferV3SourceContract contract)
    {
        var namesTable = FindTableOrdinal(contract, "NzbNames");
        var namesId = FindColumnOrdinal(contract.Tables[namesTable], "Id");
        var cleanupTable = FindTableOrdinal(contract, "NzbBlobCleanupItems");
        var cleanupId = FindColumnOrdinal(contract.Tables[cleanupTable], "Id");
        var queueTable = FindTableOrdinal(contract, "QueueItems");
        var queueId = FindColumnOrdinal(contract.Tables[queueTable], "Id");
        var davTable = FindTableOrdinal(contract, "DavItems");
        var davNzb = FindColumnOrdinal(contract.Tables[davTable], "NzbBlobId");
        var historyTable = FindTableOrdinal(contract, "HistoryItems");
        var historyNzb = FindColumnOrdinal(contract.Tables[historyTable], "NzbBlobId");
        using var command = CreateQueryCommand(
            "SELECT 1 FROM verification.uuid_values AS name "
            + "LEFT JOIN verification.physical_blobs AS blob "
            + "ON blob.normalized_uuid = name.normalized_uuid "
            + "WHERE name.table_ordinal = $namesTable AND name.column_ordinal = $namesId "
            + "AND blob.normalized_uuid IS NULL AND ("
            + "NOT EXISTS (SELECT 1 FROM verification.uuid_values AS cleanup "
            + "WHERE cleanup.table_ordinal = $cleanupTable "
            + "AND cleanup.column_ordinal = $cleanupId "
            + "AND cleanup.normalized_uuid = name.normalized_uuid) "
            + "OR EXISTS (SELECT 1 FROM verification.uuid_values AS queue "
            + "WHERE queue.table_ordinal = $queueTable AND queue.column_ordinal = $queueId "
            + "AND queue.normalized_uuid = name.normalized_uuid) "
            + "OR EXISTS (SELECT 1 FROM verification.uuid_values AS dav "
            + "WHERE dav.table_ordinal = $davTable AND dav.column_ordinal = $davNzb "
            + "AND dav.normalized_uuid = name.normalized_uuid) "
            + "OR EXISTS (SELECT 1 FROM verification.uuid_values AS history "
            + "WHERE history.table_ordinal = $historyTable "
            + "AND history.column_ordinal = $historyNzb "
            + "AND history.normalized_uuid = name.normalized_uuid)) LIMIT 1;");
        command.Parameters.AddWithValue("$namesTable", namesTable);
        command.Parameters.AddWithValue("$namesId", namesId);
        command.Parameters.AddWithValue("$cleanupTable", cleanupTable);
        command.Parameters.AddWithValue("$cleanupId", cleanupId);
        command.Parameters.AddWithValue("$queueTable", queueTable);
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$davTable", davTable);
        command.Parameters.AddWithValue("$davNzb", davNzb);
        command.Parameters.AddWithValue("$historyTable", historyTable);
        command.Parameters.AddWithValue("$historyNzb", historyNzb);
        if (command.ExecuteScalar() is not null)
            throw SemanticFailure("reference-conditional");
    }

    private void ValidateDavMetadata(TransferV3SourceContract contract)
    {
        var davTable = FindTableOrdinal(contract, "DavItems");
        var rule = contract.Tables[davTable].MetadataRule
                   ?? throw SemanticFailure("metadata-contract");
        using (var domain = CreateQueryCommand(string.Empty))
        {
            var domains = new List<string>(rule.TypeDomains.Count);
            for (var domainIndex = 0; domainIndex < rule.TypeDomains.Count; domainIndex++)
            {
                var typeParameter = $"$type{domainIndex}";
                domain.Parameters.AddWithValue(
                    typeParameter,
                    rule.TypeDomains[domainIndex].Type);
                var subtypeParameters = new List<string>();
                for (var subtypeIndex = 0;
                     subtypeIndex < rule.TypeDomains[domainIndex].SubTypes.Count;
                     subtypeIndex++)
                {
                    var parameter = $"$subtype{domainIndex}_{subtypeIndex}";
                    domain.Parameters.AddWithValue(
                        parameter,
                        rule.TypeDomains[domainIndex].SubTypes[subtypeIndex]);
                    subtypeParameters.Add(parameter);
                }
                domains.Add(
                    $"(type_value = {typeParameter} AND subtype_value IN "
                    + $"({string.Join(",", subtypeParameters)}))");
            }
            domain.CommandText =
                "SELECT 1 FROM verification.dav_metadata WHERE NOT ("
                + string.Join(" OR ", domains)
                + ") LIMIT 1;";
            if (domain.ExecuteScalar() is not null)
                throw SemanticFailure("type-subtype-domain");
        }

        var zero = new byte[16];
        try
        {
            using var parent = CreateQueryCommand(
                "SELECT 1 FROM verification.dav_metadata "
                + "WHERE parent_uuid IS NULL AND normalized_uuid <> $zero LIMIT 1;");
            parent.Parameters.Add("$zero", SqliteType.Blob, 16).Value = zero;
            if (parent.ExecuteScalar() is not null)
                throw SemanticFailure("reference-state");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zero);
        }

        foreach (var subtype in rule.Subtypes)
        {
            var legacyTable = FindTableOrdinal(contract, subtype.LegacyTable);
            using var legacy = CreateQueryCommand(
                "SELECT 1 FROM verification.legacy_metadata AS legacy "
                + "LEFT JOIN verification.dav_metadata AS dav "
                + "ON dav.normalized_uuid = legacy.normalized_uuid "
                + "WHERE legacy.table_ordinal = $table "
                + "AND (dav.normalized_uuid IS NULL OR dav.type_value <> 2 "
                + "OR dav.subtype_value <> $subtype) LIMIT 1;");
            legacy.Parameters.AddWithValue("$table", legacyTable);
            legacy.Parameters.AddWithValue("$subtype", subtype.SubType);
            if (legacy.ExecuteScalar() is not null)
                throw SemanticFailure("metadata-subtype");
        }

        using (var source = CreateQueryCommand(string.Empty))
        {
            var alternatives = new List<string>(rule.Subtypes.Count);
            for (var index = 0; index < rule.Subtypes.Count; index++)
            {
                var subtypeParameter = $"$sourceSubtype{index}";
                var tableParameter = $"$sourceTable{index}";
                source.Parameters.AddWithValue(
                    subtypeParameter,
                    rule.Subtypes[index].SubType);
                source.Parameters.AddWithValue(
                    tableParameter,
                    FindTableOrdinal(contract, rule.Subtypes[index].LegacyTable));
                alternatives.Add(
                    $"(dav.subtype_value = {subtypeParameter} AND EXISTS ("
                    + "SELECT 1 FROM verification.legacy_metadata AS legacy "
                    + $"WHERE legacy.table_ordinal = {tableParameter} "
                    + "AND legacy.normalized_uuid = dav.normalized_uuid))");
            }
            source.CommandText =
                "SELECT 1 FROM verification.dav_metadata AS dav "
                + "WHERE dav.type_value = 2 AND dav.file_blob_uuid IS NULL "
                + $"AND NOT ({string.Join(" OR ", alternatives)}) LIMIT 1;";
            if (source.ExecuteScalar() is not null)
                throw SemanticFailure("metadata-source");
        }
    }

    private void ValidateBootstrap(TransferV3SourceContract contract)
    {
        using var command = CreateQueryCommand(
            "SELECT (SELECT count(*) FROM verification.bootstrap_config), "
            + "(SELECT count(*) FROM verification.bootstrap_roots);");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw SemanticFailure("bootstrap-config");
        if (reader.GetInt64(0) != contract.Bootstrap.Config.Count)
            throw SemanticFailure("bootstrap-config");
        if (reader.GetInt64(1) != contract.Bootstrap.Roots.Count)
            throw SemanticFailure("bootstrap-root");
    }

    private void ValidateInformationalDigests(
        TransferV3SourceContract contract,
        TransferV3Manifest manifest,
        CancellationToken cancellationToken)
    {
        if (_hooks is null)
        {
            ValidateInformationalDigestsCore(contract, manifest, cancellationToken);
            return;
        }

        delegate_progress progress = static state =>
        {
            try
            {
                var index = (TransferV3BlobReferenceIndex)state;
                InvokeHook(index._hooks, "query-progress");
                return 0;
            }
            catch
            {
                return 1;
            }
        };
        var installed = false;
        try
        {
            raw.sqlite3_progress_handler(_connection.Handle, 1000, progress, this);
            installed = true;
            ValidateInformationalDigestsCore(contract, manifest, cancellationToken);
        }
        finally
        {
            if (installed)
            {
                try
                {
                    raw.sqlite3_progress_handler(_connection.Handle, 0, null!, null!);
                }
                catch
                {
                    // Progress-observer cleanup cannot replace verification's primary result.
                }
            }
            GC.KeepAlive(progress);
        }
    }

    private void ValidateInformationalDigestsCore(
        TransferV3SourceContract contract,
        TransferV3Manifest manifest,
        CancellationToken cancellationToken)
    {
        var informational = new List<TransferV3IndexedReference>();
        var globalOrdinal = 0;
        for (var tableOrdinal = 0; tableOrdinal < contract.Tables.Count; tableOrdinal++)
        {
            foreach (var reference in contract.Tables[tableOrdinal].References)
            {
                if (reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                    or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
                {
                    informational.Add(new TransferV3IndexedReference(
                        tableOrdinal,
                        globalOrdinal,
                        reference));
                }
                globalOrdinal++;
            }
        }
        if (informational.Count != manifest.InformationalReferences.Length)
            throw SemanticFailure("informational-reference");

        for (var index = 0; index < informational.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexed = informational[index];
            var descriptor = manifest.InformationalReferences[index];
            if (!string.Equals(
                    descriptor.Name,
                    indexed.Reference.Name,
                    StringComparison.Ordinal))
            {
                throw SemanticFailure("informational-reference");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var name = Encoding.UTF8.GetBytes(indexed.Reference.Name);
            try
            {
                hash.AppendData(name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(name);
            }
            long unresolved = 0;
            var encodedDiscriminator = new byte[sizeof(int)];
            try
            {
                using var query = CreateInformationalQuery(contract, indexed);
                using var rows = query.ExecuteReader();
                while (rows.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var owner = ((byte[])rows.GetValue(0)).ToArray();
                    byte[]? target = null;
                    try
                    {
                        target = ((byte[])rows.GetValue(1)).ToArray();
                        hash.AppendData(owner);
                        if (indexed.Reference.Policy
                            == TransferV3ReferencePolicy.PolymorphicInformationalDigest)
                        {
                            var discriminator = rows.GetInt64(2);
                            if (discriminator is < int.MinValue or > int.MaxValue)
                                throw SemanticFailure("informational-reference");
                            BinaryPrimitives.WriteInt32BigEndian(
                                encodedDiscriminator,
                                (int)discriminator);
                            hash.AppendData(encodedDiscriminator);
                        }
                        hash.AppendData(target);
                        unresolved = checked(unresolved + 1);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(owner);
                        if (target is not null)
                            CryptographicOperations.ZeroMemory(target);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedDiscriminator);
            }
            var digest = hash.GetHashAndReset();
            try
            {
                if (unresolved != descriptor.UnresolvedCount
                    || !string.Equals(
                        Convert.ToHexString(digest).ToLowerInvariant(),
                        descriptor.UnresolvedSha256,
                        StringComparison.Ordinal))
                {
                    throw SemanticFailure("informational-reference");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private SqliteCommand CreateInformationalQuery(
        TransferV3SourceContract contract,
        TransferV3IndexedReference indexed)
    {
        var command = CreateQueryCommand(string.Empty);
        command.Parameters.AddWithValue("$reference", indexed.ReferenceOrdinal);
        string resolved;
        string order;
        string select;
        if (indexed.Reference.Policy == TransferV3ReferencePolicy.InformationalDigest)
        {
            var principalTable = FindTableOrdinal(
                contract,
                indexed.Reference.PrincipalTables[0]);
            var principalColumn = FindColumnOrdinal(
                contract.Tables[principalTable],
                indexed.Reference.PrincipalColumns[0]);
            command.Parameters.AddWithValue("$principalTable", principalTable);
            command.Parameters.AddWithValue("$principalColumn", principalColumn);
            resolved =
                "EXISTS (SELECT 1 FROM verification.uuid_values AS principal "
                + "WHERE principal.table_ordinal = $principalTable "
                + "AND principal.column_ordinal = $principalColumn "
                + "AND principal.normalized_uuid = fact.target_uuid)";
            select = "fact.owner_uuid, fact.target_uuid";
            order = "fact.owner_uuid, fact.target_uuid";
        }
        else
        {
            var cases = indexed.Reference.PolymorphicCases
                        ?? throw SemanticFailure("reference-contract");
            var clauses = new List<string>(cases.Count);
            for (var index = 0; index < cases.Count; index++)
            {
                var principalTable = FindTableOrdinal(contract, cases[index].PrincipalTable);
                var principalColumn = FindColumnOrdinal(
                    contract.Tables[principalTable],
                    indexed.Reference.PrincipalColumns[0]);
                var discriminatorParameter = $"$discriminator{index}";
                var tableParameter = $"$caseTable{index}";
                var columnParameter = $"$caseColumn{index}";
                command.Parameters.AddWithValue(
                    discriminatorParameter,
                    cases[index].DiscriminatorValue);
                command.Parameters.AddWithValue(tableParameter, principalTable);
                command.Parameters.AddWithValue(columnParameter, principalColumn);
                clauses.Add(
                    $"(fact.discriminator = {discriminatorParameter} AND EXISTS ("
                    + "SELECT 1 FROM verification.uuid_values AS principal "
                    + $"WHERE principal.table_ordinal = {tableParameter} "
                    + $"AND principal.column_ordinal = {columnParameter} "
                    + "AND principal.normalized_uuid = fact.target_uuid))");
            }
            resolved = $"({string.Join(" OR ", clauses)})";
            select = "fact.owner_uuid, fact.target_uuid, fact.discriminator";
            order = "fact.owner_uuid, fact.discriminator, fact.target_uuid";
        }
        command.CommandText =
            $"SELECT {select} FROM verification.informational_facts AS fact "
            + "WHERE fact.reference_ordinal = $reference "
            + $"AND NOT {resolved} ORDER BY {order};";
        return command;
    }

    private void ValidateDerivedHealth(
        TransferV3SourceContract contract,
        TransferV3Manifest manifest,
        CancellationToken cancellationToken)
    {
        if (contract.DerivedTables.Count != 1
            || manifest.DerivedTables.Length != 1
            || !string.Equals(
                contract.DerivedTables[0].Name,
                "HealthCheckStats",
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.DerivedTables[0].Name,
                "HealthCheckStats",
                StringComparison.Ordinal))
        {
            throw SemanticFailure("derived-state");
        }
        var table = contract.DerivedTables[0];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var int32 = new byte[sizeof(int)];
        var int64 = new byte[sizeof(long)];
        long count = 0;
        try
        {
            using var command = CreateQueryCommand(
                "SELECT date_start, date_end, result_value, repair_status, count_value "
                + "FROM verification.health_buckets "
                + "ORDER BY date_start, date_end, result_value, repair_status;");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = reader.GetInt64(0);
                var end = reader.GetInt64(1);
                var result = reader.GetInt64(2);
                var status = reader.GetInt64(3);
                var rowCount = reader.GetInt64(4);
                if (rowCount is <= 0 or > int.MaxValue)
                    throw SemanticFailure("derived-state");
                var fields = new byte[5][];
                byte[]? cursorBytes = null;
                try
                {
                    fields[0] = TransferV3RowCodec.EncodeField(table.Columns[0], start);
                    fields[1] = TransferV3RowCodec.EncodeField(table.Columns[1], end);
                    fields[2] = TransferV3RowCodec.EncodeField(table.Columns[2], checked((int)result));
                    fields[3] = TransferV3RowCodec.EncodeField(table.Columns[3], checked((int)status));
                    fields[4] = TransferV3RowCodec.EncodeField(table.Columns[4], checked((int)rowCount));
                    var cursor = TransferV3CursorCodec.Encode(
                        TransferV3CursorComponent.FromInt64(start),
                        TransferV3CursorComponent.FromInt64(end),
                        TransferV3CursorComponent.FromInt64(result),
                        TransferV3CursorComponent.FromInt64(status));
                    cursorBytes = Encoding.ASCII.GetBytes(cursor);
                    BinaryPrimitives.WriteInt32BigEndian(int32, cursorBytes.Length);
                    hash.AppendData(int32);
                    hash.AppendData(cursorBytes);
                    BinaryPrimitives.WriteInt32BigEndian(int32, fields.Length);
                    hash.AppendData(int32);
                    foreach (var field in fields)
                    {
                        BinaryPrimitives.WriteInt64BigEndian(int64, field.Length);
                        hash.AppendData(int64);
                        hash.AppendData(field);
                    }
                    count = checked(count + 1);
                }
                catch (TransferV3RowFormatException)
                {
                    throw SemanticFailure("derived-state");
                }
                finally
                {
                    if (cursorBytes is not null)
                        CryptographicOperations.ZeroMemory(cursorBytes);
                    foreach (var field in fields)
                    {
                        if (field is not null) CryptographicOperations.ZeroMemory(field);
                    }
                }
            }
            var digest = hash.GetHashAndReset();
            try
            {
                if (count != manifest.DerivedTables[0].Rows
                    || !string.Equals(
                        Convert.ToHexString(digest).ToLowerInvariant(),
                        manifest.DerivedTables[0].LogicalSha256,
                        StringComparison.Ordinal))
                {
                    throw SemanticFailure("derived-state");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(int32);
            CryptographicOperations.ZeroMemory(int64);
        }
    }

    private static int FindTableOrdinal(TransferV3SourceContract contract, string name)
    {
        for (var index = 0; index < contract.Tables.Count; index++)
        {
            if (string.Equals(contract.Tables[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        throw SemanticFailure("reference-contract");
    }

    private static int FindColumnOrdinal(TransferV3TableContract table, string name)
    {
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        throw SemanticFailure("reference-contract");
    }

    private static TransferV3SnapshotVerificationException SemanticFailure(string code) =>
        new(code);

    private sealed record TransferV3IndexedReference(
        int TableOrdinal,
        int ReferenceOrdinal,
        TransferV3ReferenceContract Reference);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var failed = false;
        try
        {
            InvokeHook(_hooks, "before-dispose");
        }
        catch
        {
            failed = true;
        }

        AbortBatchNoThrow();
        foreach (var command in _commands)
            try
            {
                command.Dispose();
            }
            catch
            {
                failed = true;
            }
        try
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            failed = true;
        }
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            failed = true;
        }
        if (failed)
            throw TransferV3BlobReferenceIndexException.Create("index-dispose");
    }

    private SqliteCommand CreatePreparedCommand(
        string sql,
        params (string Name, SqliteType Type, int Size)[] parameters)
    {
        var command = _connection.CreateCommand();
        try
        {
            command.Transaction = _transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                var value = command.Parameters.Add(parameter.Name, parameter.Type);
                if (parameter.Size != 0)
                    value.Size = parameter.Size;
            }
            command.Prepare();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private void ExecuteInsert(
        SqliteCommand command,
        CancellationToken cancellationToken,
        params object[] values)
    {
        try
        {
            foreach (var value in values)
            {
                if (value is byte[] buffer)
                    ObserveOwnedParameterBuffer("insert-parameter", buffer);
            }

            ExecuteWrite(() =>
            {
                if (values.Length != command.Parameters.Count)
                    throw TransferV3BlobReferenceIndexException.Create("index-fact-shape");
                try
                {
                    for (var index = 0; index < values.Length; index++)
                        command.Parameters[index].Value = values[index];
                    command.ExecuteNonQuery();
                }
                finally
                {
                    foreach (SqliteParameter parameter in command.Parameters)
                        parameter.Value = DBNull.Value;
                }
            }, cancellationToken);
        }
        finally
        {
            foreach (var value in values)
            {
                if (value is byte[] buffer)
                    CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private void ObserveOwnedParameterBuffer(string kind, byte[] buffer) =>
        _hooks?.ObserveOwnedParameterBufferForTesting?.Invoke(kind, buffer);

    private void ExecuteWrite(Action operation, CancellationToken cancellationToken)
    {
        RequireOpen();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokeHook(_hooks, "before-write");
            operation();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (TransferV3BlobReferenceIndexException)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw TransferV3BlobReferenceIndexException.Create("index-constraint");
        }
        catch
        {
            throw TransferV3BlobReferenceIndexException.Create("index-write");
        }
    }

    private T ExecuteQuery<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        RequireOpen();
        cancellationToken.ThrowIfCancellationRequested();
        using var interruptRegistration = RegisterSqliteInterrupt(cancellationToken);
        try
        {
            InvokeHook(_hooks, "before-query");
            cancellationToken.ThrowIfCancellationRequested();
            var result = operation();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3BlobReferenceIndexException)
        {
            throw;
        }
        catch
        {
            throw TransferV3BlobReferenceIndexException.Create("index-query");
        }
    }

    private CancellationTokenRegistration RegisterSqliteInterrupt(
        CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(
                static state => ((TransferV3BlobReferenceIndex)state!).InterruptSqliteNoThrow(),
                this)
            : default;

    private void InterruptSqliteNoThrow()
    {
        try
        {
            raw.sqlite3_interrupt(_connection.Handle);
            InvokeHook(_hooks, "sqlite-interrupt");
        }
        catch
        {
            // Cancellation must not surface callback or native-lifetime details.
        }
    }

    private bool Exists(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = CreateQueryCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteScalar() is not null;
    }

    private object? Scalar(string sql)
    {
        using var command = CreateQueryCommand(sql);
        return command.ExecuteScalar();
    }

    private long IntegerScalar(string sql) => Convert.ToInt64(
        Scalar(sql), System.Globalization.CultureInfo.InvariantCulture);

    private SqliteCommand CreateQueryCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    private void RequireBatchAndShape(long rowOrdinal, ReadOnlySpan<byte> value, int length)
    {
        RequireActiveBatch();
        RequireNonNegative(rowOrdinal);
        RequireShape(value, length);
    }

    private void RequireActiveBatch()
    {
        RequireOpen();
        if (!_batchActive)
            throw TransferV3BlobReferenceIndexException.Create("index-batch-state");
    }

    private void RequireOpen()
    {
        if (_disposed)
            throw TransferV3BlobReferenceIndexException.Create("index-disposed");
    }

    private static void RequireShape(ReadOnlySpan<byte> value, int length)
    {
        if (value.Length != length)
            throw TransferV3BlobReferenceIndexException.Create("index-fact-shape");
    }

    private static void RequireOptionalShape(byte[]? value, int length)
    {
        if (value is not null && value.Length != length)
            throw TransferV3BlobReferenceIndexException.Create("index-fact-shape");
    }

    private static void RequireNonNegative(long value)
    {
        if (value < 0)
            throw TransferV3BlobReferenceIndexException.Create("index-fact-shape");
    }

    private static void InvokeHook(TransferV3BlobReferenceIndexHooks? hooks, string point) =>
        hooks?.BeforeFaultPoint?.Invoke(point);

    private static async Task VerifyStorageInvariantsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT file FROM pragma_database_list WHERE name = 'verification';";
        var file = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "PRAGMA temp_store;";
        var tempStore = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA trusted_schema;";
        var trustedSchema = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.cache_size;";
        var cacheSize = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.cache_spill;";
        var cacheSpill = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.mmap_size;";
        var mmapSize = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.secure_delete;";
        var secureDelete = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.synchronous;";
        var synchronous = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA verification.journal_mode;";
        var journalMode = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "SELECT count(*) FROM main.sqlite_schema WHERE type = 'table';";
        var mainTables = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText =
            "SELECT group_concat(name, ',') FROM (SELECT name FROM verification.sqlite_schema "
            + "WHERE type = 'table' ORDER BY name COLLATE BINARY);";
        var names = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (file is not string { Length: 0 }
            || raw.sqlite3_db_readonly(connection.Handle, "verification") != 0
            || tempStore != 1
            || foreignKeys != 1
            || trustedSchema != 0
            || cacheSize != -8192
            || cacheSpill == 0
            || mmapSize != 0
            || secureDelete != 1
            || synchronous != 0
            || journalMode != "memory"
            || mainTables != 0
            || names != string.Join(',', ExpectedTableNames))
        {
            throw new InvalidOperationException("Private verification index invariant failed.");
        }
    }

    private static async Task<IReadOnlyList<string>> DisposeFailedConnectionNoThrowAsync(
        SqliteConnection connection,
        TransferV3BlobReferenceIndexHooks? hooks)
    {
        var cleanup = new List<string>();
        try
        {
            InvokeHook(hooks, "before-dispose");
        }
        catch
        {
            cleanup.Add("index-dispose");
        }
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            if (!cleanup.Contains("index-dispose", StringComparer.Ordinal))
                cleanup.Add("index-dispose");
        }
        return cleanup.AsReadOnly();
    }

    private const string SchemaSql =
        """
        PRAGMA temp_store = FILE;
        PRAGMA foreign_keys = ON;
        PRAGMA trusted_schema = OFF;
        ATTACH DATABASE '' AS verification;
        PRAGMA verification.journal_mode = MEMORY;
        PRAGMA verification.cache_size = -8192;
        PRAGMA verification.cache_spill = ON;
        PRAGMA verification.mmap_size = 0;
        PRAGMA verification.secure_delete = ON;
        PRAGMA verification.synchronous = OFF;
        CREATE TABLE verification.row_keys (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            key_sha256 BLOB NOT NULL CHECK(typeof(key_sha256) = 'blob' AND length(key_sha256) = 32),
            PRIMARY KEY (table_ordinal, key_sha256),
            UNIQUE (table_ordinal, row_ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE verification.unique_values (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            rule_ordinal INTEGER NOT NULL CHECK(typeof(rule_ordinal) = 'integer' AND rule_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            key_sha256 BLOB NOT NULL CHECK(typeof(key_sha256) = 'blob' AND length(key_sha256) = 32),
            PRIMARY KEY (table_ordinal, rule_ordinal, key_sha256),
            UNIQUE (table_ordinal, rule_ordinal, row_ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE verification.uuid_values (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            column_ordinal INTEGER NOT NULL CHECK(typeof(column_ordinal) = 'integer' AND column_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
            PRIMARY KEY (table_ordinal, column_ordinal, row_ordinal)
        ) WITHOUT ROWID;
        CREATE INDEX verification.IX_uuid_values_lookup
            ON uuid_values(table_ordinal, column_ordinal, normalized_uuid);
        CREATE TABLE verification.hard_blob_refs (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            reference_ordinal INTEGER NOT NULL CHECK(typeof(reference_ordinal) = 'integer' AND reference_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
            PRIMARY KEY (table_ordinal, reference_ordinal, row_ordinal)
        ) WITHOUT ROWID;
        CREATE INDEX verification.IX_hard_blob_refs_lookup ON hard_blob_refs(normalized_uuid);
        CREATE TABLE verification.informational_facts (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            reference_ordinal INTEGER NOT NULL CHECK(typeof(reference_ordinal) = 'integer' AND reference_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            owner_uuid BLOB NOT NULL CHECK(typeof(owner_uuid) = 'blob' AND length(owner_uuid) = 16),
            target_uuid BLOB NOT NULL CHECK(typeof(target_uuid) = 'blob' AND length(target_uuid) = 16),
            discriminator INTEGER NULL CHECK(discriminator IS NULL OR typeof(discriminator) = 'integer'),
            PRIMARY KEY (table_ordinal, reference_ordinal, row_ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE verification.physical_blobs (
            normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
            length_bytes INTEGER NOT NULL CHECK(typeof(length_bytes) = 'integer' AND length_bytes >= 0),
            content_sha256 BLOB NOT NULL CHECK(typeof(content_sha256) = 'blob' AND length(content_sha256) = 32),
            PRIMARY KEY (normalized_uuid)
        ) WITHOUT ROWID;
        CREATE TABLE verification.dav_metadata (
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
            parent_uuid BLOB NULL CHECK(parent_uuid IS NULL OR (typeof(parent_uuid) = 'blob' AND length(parent_uuid) = 16)),
            type_value INTEGER NOT NULL CHECK(typeof(type_value) = 'integer'),
            subtype_value INTEGER NOT NULL CHECK(typeof(subtype_value) = 'integer'),
            file_blob_uuid BLOB NULL CHECK(file_blob_uuid IS NULL OR (typeof(file_blob_uuid) = 'blob' AND length(file_blob_uuid) = 16)),
            PRIMARY KEY (row_ordinal),
            UNIQUE (normalized_uuid)
        ) WITHOUT ROWID;
        CREATE TABLE verification.legacy_metadata (
            table_ordinal INTEGER NOT NULL CHECK(typeof(table_ordinal) = 'integer' AND table_ordinal >= 0),
            row_ordinal INTEGER NOT NULL CHECK(typeof(row_ordinal) = 'integer' AND row_ordinal >= 0),
            normalized_uuid BLOB NOT NULL CHECK(typeof(normalized_uuid) = 'blob' AND length(normalized_uuid) = 16),
            PRIMARY KEY (table_ordinal, row_ordinal),
            UNIQUE (table_ordinal, normalized_uuid)
        ) WITHOUT ROWID;
        CREATE TABLE verification.health_buckets (
            date_start INTEGER NOT NULL CHECK(typeof(date_start) = 'integer'),
            date_end INTEGER NOT NULL CHECK(typeof(date_end) = 'integer'),
            result_value INTEGER NOT NULL CHECK(typeof(result_value) = 'integer'),
            repair_status INTEGER NOT NULL CHECK(typeof(repair_status) = 'integer'),
            count_value INTEGER NOT NULL CHECK(typeof(count_value) = 'integer' AND count_value > 0 AND count_value <= 2147483647),
            PRIMARY KEY (date_start, date_end, result_value, repair_status)
        ) WITHOUT ROWID;
        CREATE TABLE verification.bootstrap_config (
            rule_ordinal INTEGER NOT NULL CHECK(typeof(rule_ordinal) = 'integer' AND rule_ordinal >= 0),
            secret_sha256 BLOB NOT NULL CHECK(typeof(secret_sha256) = 'blob' AND length(secret_sha256) = 32),
            PRIMARY KEY (rule_ordinal),
            UNIQUE (secret_sha256)
        ) WITHOUT ROWID;
        CREATE TABLE verification.bootstrap_roots (
            rule_ordinal INTEGER NOT NULL CHECK(typeof(rule_ordinal) = 'integer' AND rule_ordinal >= 0),
            marker_sha256 BLOB NOT NULL CHECK(typeof(marker_sha256) = 'blob' AND length(marker_sha256) = 32),
            PRIMARY KEY (rule_ordinal)
        ) WITHOUT ROWID;
        """;
}

internal sealed class TransferV3BlobReferenceIndexException : Exception
{
    private TransferV3BlobReferenceIndexException(string code, IReadOnlyList<string> cleanupCodes)
        : base($"Transfer-v3 verification index failed: code={code}.")
    {
        Code = code;
        CleanupCodes = cleanupCodes;
    }

    internal string Code { get; }

    internal IReadOnlyList<string> CleanupCodes { get; }

    internal static TransferV3BlobReferenceIndexException Create(
        string code,
        IReadOnlyList<string>? cleanupCodes = null) =>
        new(code, cleanupCodes ?? Array.Empty<string>());
}

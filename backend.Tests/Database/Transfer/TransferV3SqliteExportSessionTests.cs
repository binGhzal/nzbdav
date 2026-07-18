using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SqliteExportSessionTests
{
    [Fact]
    public async Task OpenValidatedExportSessionAsync_RetainsOneAttachmentTransactionAndContentFreeIntegrityIndexes()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string probe = "field-integrity-secret-value";
        await source.InsertConfigAsync("field-integrity-probe", probe);
        await source.WriteBlobAsync("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "blob-probe"u8.ToArray());
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var validator = new TransferV3SqlitePreflightValidator();

        await using var session = await validator.OpenValidatedExportSessionAsync(
            source.DatabasePath,
            source.Options(MaxRowsPerBatch: 2),
            provenance);

        Assert.Equal(TransferV3SqliteExportSessionState.Ready, session.State);
        Assert.Equal(provenance, session.Provenance);
        Assert.Equal(27, session.Validation.Tables.Count);
        SqliteTransaction? retainedTransaction = null;
        TransferV3SqliteExportContext? retainedContext = null;
        await session.RunExportAsync(async (context, cancellationToken) =>
        {
            retainedContext = context;
            retainedTransaction = context.Transaction;
            Assert.Same(context.Connection, context.Transaction.Connection);
            Assert.Same(session.Validation, context.Validation);
            Assert.Equal(provenance, context.Provenance);

            await using (var databases = context.Connection.CreateCommand())
            {
                databases.Transaction = context.Transaction;
                databases.CommandText =
                    "SELECT count(*), min(file) FROM pragma_database_list WHERE name = 'source';";
                await using var reader = await databases.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(1L, reader.GetInt64(0));
                var attached = reader.GetString(1);
                Assert.DoesNotContain(source.DatabasePath, attached, StringComparison.Ordinal);
                Assert.True(
                    attached.StartsWith("/proc/self/fd/", StringComparison.Ordinal)
                    || attached.StartsWith("/dev/fd/", StringComparison.Ordinal),
                    attached);
            }

            await using (var schema = context.Connection.CreateCommand())
            {
                schema.Transaction = context.Transaction;
                schema.CommandText =
                    "SELECT name, sql FROM scratch.sqlite_schema "
                    + "WHERE type = 'table' AND name IN "
                    + "('validated_fields','blob_first_shards','blob_second_shards','blob_inventory') "
                    + "ORDER BY name COLLATE BINARY;";
                var found = new List<(string Name, string Sql)>();
                await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    found.Add((reader.GetString(0), reader.GetString(1)));
                Assert.Equal(
                    ["blob_first_shards", "blob_inventory", "blob_second_shards", "validated_fields"],
                    found.Select(value => value.Name));
                Assert.All(found, value => Assert.Contains("WITHOUT ROWID", value.Sql, StringComparison.Ordinal));
            }

            await using (var fields = context.Connection.CreateCommand())
            {
                fields.Transaction = context.Transaction;
                fields.CommandText =
                    "SELECT vf.length_bytes, vf.content_sha256 "
                    + "FROM scratch.validated_fields AS vf "
                    + "JOIN source.ConfigItems AS c ON c.rowid = vf.source_rowid "
                    + "WHERE vf.table_name = $table AND vf.column_name = $column "
                    + "AND c.ConfigName = 'field-integrity-probe';";
                fields.Parameters.Add("$table", SqliteType.Blob).Value = Encoding.UTF8.GetBytes("ConfigItems");
                fields.Parameters.Add("$column", SqliteType.Blob).Value = Encoding.UTF8.GetBytes("ConfigValue");
                await using var reader = await fields.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(Encoding.UTF8.GetByteCount(probe), reader.GetInt64(0));
                Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(probe)), reader.GetFieldValue<byte[]>(1));
                Assert.False(await reader.ReadAsync(cancellationToken));
            }

            await using (var fieldColumns = context.Connection.CreateCommand())
            {
                fieldColumns.Transaction = context.Transaction;
                fieldColumns.CommandText =
                    "SELECT group_concat(name, ',') FROM pragma_table_info('validated_fields', 'scratch');";
                Assert.Equal(
                    "table_name,source_rowid,column_name,length_bytes,content_sha256",
                    await fieldColumns.ExecuteScalarAsync(cancellationToken));
            }

            await using (var blobs = context.Connection.CreateCommand())
            {
                blobs.Transaction = context.Transaction;
                blobs.CommandText =
                    "SELECT length(normalized_uuid), length(first_name), length(second_name), "
                    + "length(content_sha256), length(file_fingerprint) FROM scratch.blob_inventory;";
                await using var reader = await blobs.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal([16L, 2L, 2L, 32L, 56L], Enumerable.Range(0, 5).Select(reader.GetInt64));
                Assert.False(await reader.ReadAsync(cancellationToken));
            }
        });

        Assert.Equal(TransferV3SqliteExportSessionState.Completed, session.State);
        Assert.NotNull(retainedTransaction);
        Assert.Null(retainedTransaction.Connection);
        Assert.NotNull(retainedContext);
        Assert.Throws<InvalidOperationException>(() => _ = retainedContext.Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunExportAsync((_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsValidationOnlySummaryThatCannotAuthorizeExport()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var result = await source.ValidateAsync();

        Assert.Equal(27, result.Tables.Count);
        Assert.Null(typeof(TransferV3ValidatedSource).GetMethod(
            "RunExportAsync",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic));
        Assert.False(typeof(TransferV3ValidatedSource).IsAssignableTo(typeof(TransferV3SqliteExportSession)));
    }

    [Fact]
    public async Task OpenValidatedExportSessionAsync_RejectsMissingMismatchedOrInvalidExplicitProvenance()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var validator = new TransferV3SqlitePreflightValidator();
        var valid = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var cases = new[]
        {
            default(TransferV3SourceProvenance),
            valid with
            {
                DatabaseIdentity = new TransferV3FileIdentity(
                    valid.DatabaseIdentity.Device,
                    checked(valid.DatabaseIdentity.Inode + 1)),
            },
            valid with
            {
                BlobRootIdentity = new TransferV3FileIdentity(
                    valid.BlobRootIdentity.Device,
                    checked(valid.BlobRootIdentity.Inode + 1)),
            },
            valid with { SourceTimeZoneId = "" },
            valid with { SourceTimeZoneId = "UTC\nsecret" },
            valid with { SourceTimeZoneId = "missing-zone-" + Guid.NewGuid().ToString("N") },
        };

        foreach (var provenance in cases)
        {
            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                validator.OpenValidatedExportSessionAsync(
                    source.DatabasePath,
                    source.Options(),
                    provenance));
            Assert.Contains("code=source-provenance", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(source.DatabasePath, exception.Message, StringComparison.Ordinal);
            Assert.InRange(exception.Message.Length, 1, 512);
        }

        var oversized = valid with { SourceTimeZoneId = new string('x', 256) };
        var oversizedException = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            validator.OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(),
                oversized));
        Assert.Contains(
            "digest=" + DigestPrefix("time-zone-shape"),
            oversizedException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenValidatedExportSessionAsync_AttachesPinnedDescriptorWhenEveryCallerPathAncestorIsReplaced()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var ancestorParent = source.ValidationWorkspaceRoot;
        var ancestor = Path.Combine(ancestorParent, "ancestor");
        var nested = Path.Combine(ancestor, "one", "two");
        var moved = Path.Combine(ancestorParent, "ancestor-pinned");
        Directory.CreateDirectory(nested);
        var databasePath = Path.Combine(nested, "source.sqlite");
        var blobRoot = Path.Combine(nested, "blobs");
        File.Copy(source.DatabasePath, databasePath);
        Directory.CreateDirectory(blobRoot);
        var options = new TransferV3SqliteValidationOptions(blobRoot);
        var provenance = CaptureProvenance(databasePath, blobRoot, TimeZoneInfo.Utc.Id);
        var replaced = false;
        options = options with
        {
            Progress = new ImmediateProgress<TransferV3ValidationProgress>(value =>
            {
                if (replaced || value.Phase != "source-guards-pinned") return;
                replaced = true;
                Directory.Move(ancestor, moved);
                Directory.CreateDirectory(nested);
                File.WriteAllText(databasePath, "caller-path-decoy");
                Directory.CreateDirectory(blobRoot);
            }),
        };

        try
        {
            var validator = new TransferV3SqlitePreflightValidator();
            await using var session = await validator.OpenValidatedExportSessionAsync(
                databasePath,
                options,
                provenance);
            await session.RunExportAsync((context, _) =>
            {
                Assert.Equal(27, context.Validation.Tables.Count);
                return Task.CompletedTask;
            });
            Assert.True(replaced);
        }
        finally
        {
            if (Directory.Exists(ancestor)) Directory.Delete(ancestor, recursive: true);
            if (Directory.Exists(moved)) Directory.Move(moved, ancestor);
        }
    }

    [Fact]
    public async Task OpenValidatedExportSessionAsync_RefusesSourceEntryReplacementBeforeDescriptorAttachWithoutReadingDecoy()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var original = source.DatabasePath + ".pinned";
        var replaced = false;
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (replaced || value.Phase != "source-guards-pinned") return;
            replaced = true;
            File.Move(source.DatabasePath, original);
            File.WriteAllText(source.DatabasePath, "caller-path-decoy");
        }));

        try
        {
            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                new TransferV3SqlitePreflightValidator().OpenValidatedExportSessionAsync(
                    source.DatabasePath,
                    options,
                    provenance));
            Assert.Contains("code=source-stability", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("history-shape", exception.Message, StringComparison.Ordinal);
            Assert.True(replaced);
        }
        finally
        {
            if (File.Exists(source.DatabasePath)) File.Delete(source.DatabasePath);
            if (File.Exists(original)) File.Move(original, source.DatabasePath);
        }
    }

    [Fact]
    public async Task RunExportAsync_UsesPinnedDatabaseAndBlobDescriptorsWhenSharedAncestorIsReplacedDuringCallback()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var ancestorParent = source.ValidationWorkspaceRoot;
        var ancestor = Path.Combine(ancestorParent, "callback-ancestor");
        var nested = Path.Combine(ancestor, "one", "two");
        var moved = Path.Combine(ancestorParent, "callback-ancestor-pinned");
        Directory.CreateDirectory(nested);
        var databasePath = Path.Combine(nested, "source.sqlite");
        var blobRoot = Path.Combine(nested, "blobs");
        File.Copy(source.DatabasePath, databasePath);
        Directory.CreateDirectory(blobRoot);
        var options = new TransferV3SqliteValidationOptions(blobRoot);
        var provenance = CaptureProvenance(databasePath, blobRoot, TimeZoneInfo.Utc.Id);

        try
        {
            await using var session = await new TransferV3SqlitePreflightValidator()
                .OpenValidatedExportSessionAsync(databasePath, options, provenance);
            await session.RunExportAsync((context, _) =>
            {
                Directory.Move(ancestor, moved);
                Directory.CreateDirectory(nested);
                File.WriteAllText(databasePath, "callback-caller-path-decoy");
                Directory.CreateDirectory(blobRoot);
                Assert.Equal(27, context.Validation.Tables.Count);
                return Task.CompletedTask;
            });
            Assert.Equal(TransferV3SqliteExportSessionState.Completed, session.State);
        }
        finally
        {
            if (Directory.Exists(ancestor)) Directory.Delete(ancestor, recursive: true);
            if (Directory.Exists(moved)) Directory.Move(moved, ancestor);
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("first-shard")]
    [InlineData("second-shard")]
    [InlineData("file")]
    public async Task RunExportAsync_RejectsSameByteBlobIdentityReplacementAfterValidation(string replacement)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = "same-byte-replacement"u8.ToArray();
        await source.WriteBlobAsync(id, contents);
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var validator = new TransferV3SqlitePreflightValidator();
        await using var session = await validator.OpenValidatedExportSessionAsync(
            source.DatabasePath,
            source.Options(),
            provenance);

        var first = Path.Combine(source.BlobRootPath, "aa");
        var second = Path.Combine(first, "aa");
        var file = source.BlobPath(id);
        var outside = Path.Combine(source.ValidationWorkspaceRoot, "replaced-" + replacement);
        try
        {
            switch (replacement)
            {
                case "root":
                    Directory.Move(source.BlobRootPath, outside);
                    Directory.CreateDirectory(source.BlobRootPath);
                    CopyBlob(source.BlobRootPath, id, contents);
                    break;
                case "first-shard":
                    Directory.Move(first, outside);
                    CopyBlob(source.BlobRootPath, id, contents);
                    break;
                case "second-shard":
                    Directory.Move(second, outside);
                    CopyBlob(source.BlobRootPath, id, contents);
                    break;
                case "file":
                    File.Move(file, outside);
                    await File.WriteAllBytesAsync(file, contents);
                    break;
                default:
                    throw new InvalidOperationException(replacement);
            }

            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                session.RunExportAsync((_, _) => Task.CompletedTask));
            Assert.Contains("code=blob-layout", exception.Message, StringComparison.Ordinal);
            Assert.Equal(TransferV3SqliteExportSessionState.Faulted, session.State);
        }
        finally
        {
            RestoreBlobReplacement(source, id, replacement, outside);
        }
    }

    [Fact]
    public async Task RunExportAsync_IsSingleUseSerialAndRejectsContextUseAfterCompletion()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = session.RunExportAsync(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        await entered.Task;
        Assert.Equal(TransferV3SqliteExportSessionState.Running, session.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunExportAsync((_, _) => Task.CompletedTask));
        release.SetResult();
        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunExportAsync((_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task RunExportAsync_InvalidatesRetainedContextBeforeSessionFinalizationBegins()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        TransferV3SqliteExportContext? retained = null;
        Exception? contextFailure = null;
        var finalizationObserved = false;
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (value.Phase != "export-callback-completed") return;
            finalizationObserved = true;
            contextFailure = Record.Exception(() => _ = retained!.Connection);
        }));
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, options, provenance);

        await session.RunExportAsync((context, _) =>
        {
            retained = context;
            Assert.NotNull(context.Transaction.Connection);
            return Task.CompletedTask;
        });

        Assert.True(finalizationObserved);
        Assert.IsType<InvalidOperationException>(contextFailure);
        Assert.Equal(TransferV3SqliteExportSessionState.Completed, session.State);
    }

    [Fact]
    public async Task RunExportAsync_InvalidatesRetainedContextWhenCallbackThrows()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);
        TransferV3SqliteExportContext? retained = null;
        var primary = new InvalidOperationException("callback-failure-probe");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunExportAsync((context, _) =>
            {
                retained = context;
                return Task.FromException(primary);
            }));

        Assert.Same(primary, exception);
        Assert.NotNull(retained);
        Assert.Throws<InvalidOperationException>(() => _ = retained.Connection);
        Assert.Equal(TransferV3SqliteExportSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task RunExportAsync_CancellationFaultsSessionAndDisposeIsIdempotent()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.RunExportAsync(
                (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                cancellation.Token));
        Assert.Equal(TransferV3SqliteExportSessionState.Faulted, session.State);
        await session.DisposeAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task OpenValidatedExportSessionAsync_RefusesStoppedSourceWithRetainedWal()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = source.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var writer = new SqliteConnection(connectionString);
        await writer.OpenAsync();
        await using (var command = writer.CreateCommand())
        {
            command.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; "
                + "UPDATE ConfigItems SET ConfigValue = lower(ConfigValue) WHERE ConfigName = 'api.key';";
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(File.Exists(source.DatabasePath + "-wal"));

        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            new TransferV3SqlitePreflightValidator().OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(),
                provenance));

        Assert.Contains("code=source-stability", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(source.DatabasePath + "-wal"));
    }

    [Fact]
    public async Task ValidateAsync_DisposesPinnedSourceAndBlobDescriptorsBeforeReturningSummary()
    {
        if (!OperatingSystem.IsLinux() || !TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        Assert.False(IsPathOpenByCurrentProcess(source.DatabasePath));
        Assert.False(IsPathOpenByCurrentProcess(source.BlobRootPath));
        using (var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath))
        using (var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath))
        {
            Assert.True(IsPathOpenByCurrentProcess(source.DatabasePath));
            Assert.True(IsPathOpenByCurrentProcess(source.BlobRootPath));
        }
        Assert.False(IsPathOpenByCurrentProcess(source.DatabasePath));
        Assert.False(IsPathOpenByCurrentProcess(source.BlobRootPath));

        var result = await source.ValidateAsync();

        Assert.Equal(27, result.Tables.Count);
        Assert.False(IsPathOpenByCurrentProcess(source.DatabasePath));
        Assert.False(IsPathOpenByCurrentProcess(source.BlobRootPath));
    }

    [Fact]
    public async Task ValidateAsync_PreservesExactPrimaryWhenOrderedCleanupAlsoFails()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var primary = new InvalidOperationException("validation-primary-probe");
        var steps = new List<string>();
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (value.Phase == "source-guards-pinned") throw primary;
        })) with
        {
            SessionHooks = new TransferV3SqliteExportSessionHooks(step =>
            {
                steps.Add(step);
                if (step == "blob-guard") throw new IOException("validation-cleanup-probe");
            }),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransferV3SqlitePreflightValidator().ValidateAsync(
                source.DatabasePath,
                options));

        Assert.Same(primary, exception);
        var cleanup = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data[TransferV3SqliteExportSession.CleanupFailuresDataKey]);
        Assert.Contains(cleanup, value =>
            value is IOException { Message: "validation-cleanup-probe" });
        Assert.Equal(["blob-guard", "source-guard"], steps);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("blob")]
    public async Task ValidateAsync_RejectsNonThrowingCleanupHookMutationBeforeSuccess(string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
        var steps = new List<string>();
        var mutated = false;
        var options = source.Options() with
        {
            SessionHooks = new TransferV3SqliteExportSessionHooks(step =>
            {
                steps.Add(step);
                if (mutated || step != "transaction") return;
                mutated = true;
                File.SetLastWriteTimeUtc(
                    mutation == "source" ? source.DatabasePath : source.BlobPath(id),
                    DateTime.UnixEpoch.AddDays(13));
            }),
        };

        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            new TransferV3SqlitePreflightValidator().ValidateAsync(
                source.DatabasePath,
                options));

        Assert.True(mutated);
        Assert.Equal(["transaction", "connection", "blob-guard", "source-guard"], steps);
        Assert.Contains(
            mutation == "source" ? "code=source-stability" : "code=blob-layout",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_FinalPostcommitBlobVerificationNeverReportsProgress()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.WriteBlobAsync(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
        await source.WriteBlobAsync(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            Enumerable.Repeat((byte)'b', 64 * 1024).ToArray());
        var firstBlobPath = FirstBlobPathInScannerOrder(source);
        var committed = false;
        var postcommitChunkCallbacks = 0;
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (value.Phase == "validation-transaction-committed")
            {
                committed = true;
                return;
            }
            if (!committed || value.Phase != "blob-first-chunk-read") return;
            postcommitChunkCallbacks++;
            if (postcommitChunkCallbacks == 2)
                File.WriteAllBytes(firstBlobPath, Enumerable.Repeat((byte)'x', 64 * 1024).ToArray());
        }));

        var result = await new TransferV3SqlitePreflightValidator().ValidateAsync(
            source.DatabasePath,
            options);

        Assert.Equal(27, result.Tables.Count);
        Assert.Equal(0, postcommitChunkCallbacks);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("content")]
    public async Task RunExportAsync_RejectsSourceMutationDuringCallback(string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var original = await File.ReadAllBytesAsync(source.DatabasePath);
        var originalWrite = File.GetLastWriteTimeUtc(source.DatabasePath);
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);

        try
        {
            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                session.RunExportAsync(async (_, cancellationToken) =>
                {
                    if (mutation == "metadata")
                    {
                        File.SetLastWriteTimeUtc(source.DatabasePath, DateTime.UnixEpoch.AddDays(7));
                    }
                    else
                    {
                        var changed = (byte[])original.Clone();
                        changed[^1] ^= 0x01;
                        await File.WriteAllBytesAsync(source.DatabasePath, changed, cancellationToken);
                    }
                }));
            Assert.Contains("code=source-stability", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllBytesAsync(source.DatabasePath, original);
            File.SetLastWriteTimeUtc(source.DatabasePath, originalWrite);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExportAsync_RejectsInPlaceBlobMutationOrTruncationAfterValidation(bool truncate)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var original = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        await source.WriteBlobAsync(id, original);
        var path = source.BlobPath(id);
        var originalWrite = File.GetLastWriteTimeUtc(path);
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);

        try
        {
            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                session.RunExportAsync(async (_, cancellationToken) =>
                {
                    if (truncate)
                        await File.WriteAllBytesAsync(path, [0x01], cancellationToken);
                    else
                        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'b', original.Length).ToArray(), cancellationToken);
                }));
            Assert.Contains("code=blob-layout", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original);
            File.SetLastWriteTimeUtc(path, originalWrite);
        }
    }

    [Fact]
    public async Task OpenValidatedExportSessionAsync_RetainsCanonicalEmptyFirstAndSecondShardDirectories()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        Directory.CreateDirectory(Path.Combine(source.BlobRootPath, "aa"));
        Directory.CreateDirectory(Path.Combine(source.BlobRootPath, "bb", "cc"));
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, source.Options(), provenance);

        await session.RunExportAsync(async (context, cancellationToken) =>
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "SELECT "
                + "(SELECT count(*) FROM scratch.blob_first_shards), "
                + "(SELECT count(*) FROM scratch.blob_second_shards), "
                + "(SELECT count(*) FROM scratch.blob_inventory);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal([2L, 1L, 0L], Enumerable.Range(0, 3).Select(reader.GetInt64));
        });
    }

    [Theory]
    [InlineData("source")]
    [InlineData("blob")]
    public async Task RunExportAsync_PerformsSecondSourceAndBlobVerificationAfterTransactionCommit(string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, "post-commit-probe"u8.ToArray());
        var phases = new List<string>();
        var mutated = false;
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            phases.Add(value.Phase);
            if (mutated || value.Phase != "export-transaction-committed") return;
            mutated = true;
            if (mutation == "source")
                File.SetLastWriteTimeUtc(source.DatabasePath, DateTime.UnixEpoch.AddDays(9));
            else
                File.SetLastWriteTimeUtc(source.BlobPath(id), DateTime.UnixEpoch.AddDays(9));
        }));
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, options, provenance);

        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            session.RunExportAsync((_, _) => Task.CompletedTask));

        Assert.True(mutated);
        Assert.Contains("export-precommit-verified", phases);
        Assert.Contains("export-transaction-committed", phases);
        Assert.DoesNotContain("export-postcommit-verified", phases);
        Assert.Contains(
            mutation == "source" ? "code=source-stability" : "code=blob-layout",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunExportAsync_PreservesExactCallbackFailureWhenCleanupAlsoFailsAndRunsAllStepsInOrder()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var steps = new List<string>();
        var hooks = new TransferV3SqliteExportSessionHooks(step =>
        {
            steps.Add(step);
            if (step == "connection") throw new IOException("cleanup-probe");
        });
        var options = source.Options() with { SessionHooks = hooks };
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, options, provenance);
        var primary = new InvalidOperationException("callback-primary-probe");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunExportAsync((_, _) => Task.FromException(primary)));

        Assert.Same(primary, exception);
        var cleanup = Assert.IsAssignableFrom<IReadOnlyList<Exception>>(
            exception.Data[TransferV3SqliteExportSession.CleanupFailuresDataKey]);
        Assert.Contains(cleanup, value =>
            value is IOException { Message: "cleanup-probe" });
        Assert.Equal(["transaction", "connection", "blob-guard", "source-guard"], steps);
        Assert.Equal(TransferV3SqliteExportSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("blob")]
    public async Task RunExportAsync_RejectsNonThrowingCleanupHookMutationBeforeSuccess(string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
        var steps = new List<string>();
        var mutated = false;
        var options = source.Options() with
        {
            SessionHooks = new TransferV3SqliteExportSessionHooks(step =>
            {
                steps.Add(step);
                if (mutated || step != "transaction") return;
                mutated = true;
                File.SetLastWriteTimeUtc(
                    mutation == "source" ? source.DatabasePath : source.BlobPath(id),
                    DateTime.UnixEpoch.AddDays(11));
            }),
        };
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, options, provenance);

        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            session.RunExportAsync((_, _) => Task.CompletedTask));

        Assert.True(mutated);
        Assert.Equal(["transaction", "connection", "blob-guard", "source-guard"], steps);
        Assert.Contains(
            mutation == "source" ? "code=source-stability" : "code=blob-layout",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(TransferV3SqliteExportSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task RunExportAsync_FinalPostcommitBlobVerificationNeverReportsProgress()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.WriteBlobAsync(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
        await source.WriteBlobAsync(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            Enumerable.Repeat((byte)'b', 64 * 1024).ToArray());
        var firstBlobPath = FirstBlobPathInScannerOrder(source);
        var committed = false;
        var postcommitChunkCallbacks = 0;
        var options = source.Options(Progress: new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (value.Phase == "export-transaction-committed")
            {
                committed = true;
                return;
            }
            if (!committed || value.Phase != "blob-first-chunk-read") return;
            postcommitChunkCallbacks++;
            if (postcommitChunkCallbacks == 2)
                File.WriteAllBytes(firstBlobPath, Enumerable.Repeat((byte)'x', 64 * 1024).ToArray());
        }));
        var provenance = CaptureProvenance(source, TimeZoneInfo.Utc.Id);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(source.DatabasePath, options, provenance);

        await session.RunExportAsync((_, _) => Task.CompletedTask);

        Assert.Equal(0, postcommitChunkCallbacks);
        Assert.Equal(TransferV3SqliteExportSessionState.Completed, session.State);
    }

    [Fact]
    public void DescriptorRouteUri_IsExactAndCannotAcceptArbitraryPathText()
    {
        Assert.Equal(
            "file:///proc/self/fd/42?mode=ro&immutable=1&cache=private",
            TransferV3Posix.BuildSqliteDescriptorUri(42, TransferV3DescriptorRoutePlatform.Linux));
        Assert.Equal(
            "file:///dev/fd/42?mode=ro&immutable=1&cache=private",
            TransferV3Posix.BuildSqliteDescriptorUri(42, TransferV3DescriptorRoutePlatform.MacOs));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransferV3Posix.BuildSqliteDescriptorUri(-1, TransferV3DescriptorRoutePlatform.Linux));
        Assert.DoesNotContain(
            typeof(TransferV3Posix).GetMethods(
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .Where(method => method.Name.Contains("DescriptorRoute", StringComparison.Ordinal)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)));
    }

    [Fact]
    public void ExportContext_HasNoCallerPathOrTargetProviderSurface()
    {
        var properties = typeof(TransferV3SqliteExportContext).GetProperties(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        Assert.DoesNotContain(properties, property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, property =>
            property.PropertyType == typeof(string)
            || property.PropertyType.FullName?.Contains("Npgsql", StringComparison.Ordinal) == true
            || property.Name.Contains("Target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreflightSource_HasNoCallerPathSqliteUriConstruction()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqlitePreflightValidator.cs"));

        Assert.DoesNotContain("BuildSqliteFileUri", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Uri.EscapeDataString", source, StringComparison.Ordinal);
        Assert.Contains("ProbeSqliteDescriptorUri", source, StringComparison.Ordinal);
        Assert.Contains("ATTACH DATABASE $path AS source", source, StringComparison.Ordinal);
    }

    private static TransferV3SourceProvenance CaptureProvenance(
        TransferV3ValidationSource source,
        string timeZoneId) =>
        CaptureProvenance(source.DatabasePath, source.BlobRootPath, timeZoneId);

    private static string FirstBlobPathInScannerOrder(TransferV3ValidationSource source)
    {
        using var root = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        foreach (var firstName in TransferV3Posix.EnumerateDirectoryNames(root.RootHandle))
        {
            using var first = TransferV3Posix.OpenDirectoryAt(root.RootHandle, firstName);
            foreach (var secondName in TransferV3Posix.EnumerateDirectoryNames(first))
            {
                using var second = TransferV3Posix.OpenDirectoryAt(first, secondName);
                foreach (var entryName in TransferV3Posix.EnumerateDirectoryNames(second))
                    return Path.Combine(source.BlobRootPath, firstName, secondName, entryName);
            }
        }
        throw new InvalidOperationException("The two-blob finalization fixture has no blob entries.");
    }

    private static TransferV3SourceProvenance CaptureProvenance(
        string databasePath,
        string blobRootPath,
        string timeZoneId)
    {
        using var database = TransferV3SqliteSourceGuard.Open(databasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(blobRootPath);
        return new TransferV3SourceProvenance(database.Identity, blobs.Identity, timeZoneId);
    }

    private static void CopyBlob(string root, string id, byte[] contents)
    {
        var value = Guid.ParseExact(id, "D");
        var normalized = value.ToString("N");
        var directory = Path.Combine(root, normalized[..2], normalized.Substring(2, 2));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, value.ToString("D")), contents);
    }

    private static void RestoreBlobReplacement(
        TransferV3ValidationSource source,
        string id,
        string replacement,
        string outside)
    {
        var first = Path.Combine(source.BlobRootPath, "aa");
        var second = Path.Combine(first, "aa");
        var file = source.BlobPath(id);
        switch (replacement)
        {
            case "root":
                if (Directory.Exists(source.BlobRootPath)) Directory.Delete(source.BlobRootPath, recursive: true);
                if (Directory.Exists(outside)) Directory.Move(outside, source.BlobRootPath);
                break;
            case "first-shard":
                if (Directory.Exists(first)) Directory.Delete(first, recursive: true);
                if (Directory.Exists(outside)) Directory.Move(outside, first);
                break;
            case "second-shard":
                if (Directory.Exists(second)) Directory.Delete(second, recursive: true);
                if (Directory.Exists(outside)) Directory.Move(outside, second);
                break;
            case "file":
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(outside)) File.Move(outside, file);
                break;
        }
    }

    private static bool IsPathOpenByCurrentProcess(string expectedPath)
    {
        if (!Directory.Exists("/proc/self/fd")) return false;
        var expected = Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                var target = File.ResolveLinkTarget(descriptor, returnFinalTarget: false)?.FullName;
                if (target is null) continue;
                var normalized = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(normalized, expected, StringComparison.Ordinal)) return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    private static string RepositoryPath(string relativePath)
    {
        var fromWorkingDirectory = Path.Combine(Environment.CurrentDirectory, relativePath);
        if (File.Exists(fromWorkingDirectory)) return fromWorkingDirectory;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }

    private static string DigestPrefix(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];
}

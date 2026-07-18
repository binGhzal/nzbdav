using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SqliteTableExporterTests
{
    private static readonly string[] ExpectedFiles =
    [
        "table-001-Accounts.jsonl",
        "table-002-ConfigItems.jsonl",
        "table-003-HistoryItems.jsonl",
        "table-004-QueueItems.jsonl",
        "table-005-RepairRuns.jsonl",
        "table-006-DavItems.jsonl",
        "table-007-ArrImportCommands.jsonl",
        "table-008-QueueNzbContents.jsonl",
        "table-009-QueuePriorityHints.jsonl",
        "table-010-RepairEntryHealth.jsonl",
        "table-011-RepairBrokenFiles.jsonl",
        "table-012-DavNzbFiles.jsonl",
        "table-013-DavRarFiles.jsonl",
        "table-014-DavMultipartFiles.jsonl",
        "table-015-HealthCheckResults.jsonl",
        "table-016-ArrDownloadCorrelations.jsonl",
        "table-017-ArrDownloadLifecycleEvents.jsonl",
        "table-018-ArrSearchNudgeCommands.jsonl",
        "table-019-ImportReceipts.jsonl",
        "table-020-WorkerJobs.jsonl",
        "table-021-MaintenanceRuns.jsonl",
        "table-022-BlobCleanupItems.jsonl",
        "table-023-HistoryCleanupItems.jsonl",
        "table-024-DavCleanupItems.jsonl",
        "table-025-NzbNames.jsonl",
        "table-026-NzbBlobCleanupItems.jsonl",
        "table-027-RcloneInvalidationItems.jsonl",
    ];

    [Fact]
    public async Task ExportAsync_WritesExactContractOrderedDurableFilesAndTypedDescriptors()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 2 * 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var result = await ExportAsync(source, limits, outputs);

        Assert.Equal(ExpectedFiles, outputs.CreatedNames);
        Assert.Equal(ExpectedFiles, result.Tables.Select(value => value.File));
        Assert.Equal(
            TransferV3SourceContract.LoadEmbedded().Tables.Select(value => value.Name),
            result.Tables.Select(value => value.Name));
        Assert.Equal(27, result.Tables.Length);
        Assert.All(outputs.Outputs.Values, output => Assert.True(output.DurablyCompleted));
        Assert.All(outputs.Outputs.Values, output => Assert.True(output.StreamClosed));
        var derived = Assert.Single(result.DerivedTables);
        Assert.Equal("HealthCheckStats", derived.Name);
        Assert.DoesNotContain(outputs.CreatedNames, value =>
            value.Contains("HealthCheckStats", StringComparison.Ordinal));
        Assert.False(result.Tables.IsDefault);
        Assert.False(result.DerivedTables.IsDefault);

        var zeroFrames = ParseFrames(outputs.Outputs[ExpectedFiles[0]].Bytes);
        Assert.Collection(
            zeroFrames,
            frame => Assert.IsType<TransferV3TableHeaderFrame>(frame),
            frame => Assert.IsType<TransferV3TableEndFrame>(frame));
        Assert.Equal(0, result.Tables[0].Batches);
        Assert.Equal(0, result.Tables[0].Rows);
        Assert.Equal(0, result.Tables[0].DecodedBytes);
    }

    [Theory]
    [InlineData("column")]
    [InlineData("exclusion")]
    public async Task ExportAsync_RejectsReviewedContractTamperingBeforeCreatingOutput(
        string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            session.RunExportAsync((context, token) =>
            {
                if (mutation == "column")
                {
                    var columns = Assert.IsAssignableFrom<IList<TransferV3ColumnContract>>(
                        context.Contract.Tables[0].Columns);
                    columns[0] = columns[0] with { Kind = TransferV3ColumnKind.Text };
                }
                else
                {
                    var exclusions = Assert.IsAssignableFrom<IList<string>>(
                        context.Contract.ExcludedConfigKeys);
                    exclusions[0] = "api.key";
                }

                return new TransferV3SqliteTableExporter()
                    .ExportAsync(context, limits, outputs, token);
            }));

        Assert.Equal("contract-shape", exception.Code);
        Assert.Empty(outputs.CreatedNames);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("stream")]
    [InlineData("write")]
    public async Task ExportAsync_FreezesAllContractAndValidationCollectionsBeforeFirstOutputCallback(
        string boundary)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);
        FirstBoundaryMutatingTableOutputFactory? outputs = null;

        var result = await session.RunExportAsync((context, token) =>
        {
            outputs = new FirstBoundaryMutatingTableOutputFactory(boundary, () =>
                MutateAllContractAndValidationCollections(
                    context.Contract,
                    context.Validation));
            return new TransferV3SqliteTableExporter()
                .ExportAsync(context, limits, outputs, token);
        });

        Assert.NotNull(outputs);
        Assert.True(outputs.Mutated);
        Assert.Equal(ExpectedFiles, outputs.CreatedNames);
        Assert.Equal(ExpectedFiles, result.Tables.Select(table => table.File));
        Assert.Equal(
            TransferV3SourceContract.LoadEmbedded().Tables.Select(table => table.Name),
            result.Tables.Select(table => table.Name));
        Assert.Equal(27, result.Tables.Length);
        Assert.Equal("HealthCheckStats", Assert.Single(result.DerivedTables).Name);
    }

    [Theory]
    [InlineData("create", "output-create")]
    [InlineData("null", "output-shape")]
    [InlineData("stream", "output-shape")]
    [InlineData("write", "output-write")]
    [InlineData("durable", "output-durable-close")]
    [InlineData("dispose", "output-dispose")]
    public async Task ExportAsync_RedactsExporterExceptionsFromEveryHostileOutputBoundary(
        string stage,
        string expectedCode)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new HostileBoundaryOutputFactory(stage);
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            ExportAsync(source, limits, outputs));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("/private/path/secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_RetriesResidualCleanupWhenFirstPostDurableDisposeFails()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RetryDisposeTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            ExportAsync(source, limits, outputs));

        Assert.Equal("output-dispose", exception.Code);
        Assert.Equal(2, outputs.FirstOutput.DisposeCalls);
        Assert.False(outputs.FirstOutput.ResourceOpen);
        Assert.DoesNotContain("/private/path/secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("validation-digest")]
    [InlineData("validation-order")]
    public async Task ExportAsync_RejectsMismatchedValidationStateBeforeCreatingOutput(
        string mutation)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            session.RunExportAsync((context, token) =>
            {
                if (mutation == "validation-digest")
                {
                    typeof(TransferV3ValidatedSource)
                        .GetProperty(nameof(TransferV3ValidatedSource.ContractSha256))!
                        .SetValue(context.Validation, new string('0', 64));
                }
                else
                {
                    var tables = Mutable(context.Validation.Tables);
                    tables[0] = tables[0] with
                    {
                        SqliteOrderExpression = "\"secret\"",
                    };
                }

                return new TransferV3SqliteTableExporter()
                    .ExportAsync(context, limits, outputs, token);
            }));

        Assert.Equal("contract-shape", exception.Code);
        Assert.Empty(outputs.CreatedNames);
    }

    [Fact]
    public void CleanupEvidence_AccumulatesOrderedCodesForCancellationPrimary()
    {
        var cancellation = new OperationCanceledException();
        var record = typeof(TransferV3SqliteTableExporter).GetMethod(
            "RecordCleanupFailure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        record.Invoke(null, [cancellation, "writer-dispose"]);
        record.Invoke(null, [cancellation, "output-dispose"]);
        record.Invoke(null, [cancellation, "output-dispose"]);

        Assert.Equal(
            ["writer-dispose", "output-dispose"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                cancellation.Data["TransferV3CleanupCodes"]));
    }

    [Fact]
    public async Task ExportAsync_PrepareFailureZeroesAlreadyEncodedFixedFields()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) "
            + "VALUES (1, 'prepare-failure', 'hash', 'salt');");
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        var observations = new List<(TransferV3SensitiveBufferKind Kind, bool Cleared)>();
        var hooks = new TransferV3SqliteTableExporterHooks(
            AfterSensitiveBufferCleared: (kind, cleared) =>
                observations.Add((kind, cleared)));
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            session.RunExportAsync(async (context, token) =>
            {
                await using var corrupt = context.Connection.CreateCommand();
                corrupt.Transaction = context.Transaction;
                corrupt.CommandText =
                    "UPDATE scratch.validated_fields SET content_sha256 = zeroblob(32) "
                    + "WHERE table_name = $table AND column_name = $column;";
                corrupt.Parameters.Add("$table", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
                    Encoding.UTF8.GetBytes("Accounts");
                corrupt.Parameters.Add("$column", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
                    Encoding.UTF8.GetBytes("Username");
                Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(token));
                return await new TransferV3SqliteTableExporter(hooks)
                    .ExportAsync(context, limits, outputs, token);
            }));

        Assert.Equal("text-integrity", exception.Code);
        Assert.Contains(
            observations,
            observation => observation is
            { Kind: TransferV3SensitiveBufferKind.FixedField, Cleared: true });
        Assert.DoesNotContain(
            observations,
            observation => observation is
            { Kind: TransferV3SensitiveBufferKind.FixedField, Cleared: false });
    }

    [Fact]
    public async Task ExportAsync_UsesChunkedCompositeCursorsAndMaximalPrefixBatches()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES
                (1, 'alpha', 'hash-a', 'salt-a'),
                (1, 'beta', 'hash-b', 'salt-b');
            """);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 1,
            maxBatchBytes: 1024 * 1024);

        var result = await ExportAsync(source, limits, outputs);

        var frames = ParseFrames(outputs.Outputs[ExpectedFiles[0]].Bytes);
        Assert.DoesNotContain(frames, frame => frame is TransferV3RowFrame);
        var rows = frames.OfType<TransferV3ChunkedRowStartFrame>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(4, row.Fields));
        Assert.Equal(
            [
                TransferV3CursorCodec.Encode(
                    TransferV3CursorComponent.FromInt64(1),
                    TransferV3CursorComponent.FromText("alpha")),
                TransferV3CursorCodec.Encode(
                    TransferV3CursorComponent.FromInt64(1),
                    TransferV3CursorComponent.FromText("beta")),
            ],
            rows.Select(value => value.Cursor));
        Assert.Equal(2, frames.OfType<TransferV3BatchStartFrame>().Count());
        var descriptor = result.Tables[0];
        Assert.Equal(2, descriptor.Batches);
        Assert.Equal(2, descriptor.Rows);
        var tableEnd = Assert.Single(frames.OfType<TransferV3TableEndFrame>());
        Assert.Equal(descriptor.DecodedBytes, tableEnd.Bytes);
        Assert.Equal(descriptor.Sha256, tableEnd.Sha256);
    }

    [Fact]
    public async Task ExportAsync_IsByteDeterministicAndDerivedDigestIgnoresPhysicalInsertionOrder()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var firstSource = await TransferV3ValidationSource.CreateAsync();
        await using var secondSource = await TransferV3ValidationSource.CreateAsync();
        var firstRows = HealthRowsSql(reverse: false);
        var secondRows = HealthRowsSql(reverse: true);
        await firstSource.ExecuteAsync(firstRows);
        await secondSource.ExecuteAsync(secondRows);
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var firstOutputs = new RecordingTableOutputFactory();
        var repeatedOutputs = new RecordingTableOutputFactory();
        var physicallyReorderedOutputs = new RecordingTableOutputFactory();
        var first = await ExportAsync(firstSource, limits, firstOutputs);
        var repeated = await ExportAsync(firstSource, limits, repeatedOutputs);
        var physicallyReordered = await ExportAsync(
            secondSource,
            limits,
            physicallyReorderedOutputs);

        Assert.True(first.Tables.SequenceEqual(repeated.Tables));
        Assert.True(first.DerivedTables.SequenceEqual(repeated.DerivedTables));
        Assert.Equal(ExpectedFiles, firstOutputs.CreatedNames);
        Assert.All(ExpectedFiles, name =>
            Assert.Equal(firstOutputs.Outputs[name].Bytes, repeatedOutputs.Outputs[name].Bytes));
        Assert.True(first.DerivedTables.SequenceEqual(physicallyReordered.DerivedTables));
        var derived = Assert.Single(first.DerivedTables);
        Assert.Equal(2, derived.Rows);
        Assert.Equal(
            "a54c41dc8b9381f6f10ff1d88ae0a80f80d8daac0eff51cbd1031c08781771cf",
            derived.LogicalSha256);
    }

    [Fact]
    public async Task ExportAsync_VerifiesRetainedSha256ForNonNullEmptyText()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) "
            + "VALUES (1, 'empty-hash', '', 'salt');");
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            session.RunExportAsync(async (context, token) =>
            {
                await using var corrupt = context.Connection.CreateCommand();
                corrupt.Transaction = context.Transaction;
                corrupt.CommandText =
                    "UPDATE scratch.validated_fields SET content_sha256 = zeroblob(32) "
                    + "WHERE table_name = $table AND column_name = $column "
                    + "AND length_bytes = 0 AND source_rowid = ("
                    + "SELECT rowid FROM source.Accounts WHERE Username = 'empty-hash');";
                corrupt.Parameters.Add("$table", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
                    Encoding.UTF8.GetBytes("Accounts");
                corrupt.Parameters.Add("$column", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
                    Encoding.UTF8.GetBytes("PasswordHash");
                Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(token));
                return await new TransferV3SqliteTableExporter()
                    .ExportAsync(context, limits, outputs, token);
            }));

        Assert.Equal("row-stream-read", exception.Code);
        Assert.DoesNotContain("empty-hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_StreamsMultiMiBTextInBoundedSlicesWithHonestMetrics()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var payload = new string('x', 2 * TransferV3Limits.MaxDecodedChunkBytes + 123);
        await source.InsertValidQueueItemAsync(id);
        await source.InsertQueueContentsAsync(id, payload);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 4 * 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var result = await ExportAsync(source, limits, outputs);

        var frames = ParseFrames(outputs.Outputs[ExpectedFiles[7]].Bytes);
        Assert.Single(frames.OfType<TransferV3ChunkedRowStartFrame>());
        var chunks = frames.OfType<TransferV3FieldChunkFrame>()
            .Where(frame => frame.Field == 1)
            .ToArray();
        Assert.Equal(
            [
                TransferV3Limits.MaxDecodedChunkBytes,
                TransferV3Limits.MaxDecodedChunkBytes,
                124,
            ],
            chunks.Select(frame => frame.Data.Length));
        var encoded = Concatenate(chunks.Select(frame => frame.Data));
        Assert.Equal(1, encoded[0]);
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)),
            SHA256.HashData(encoded.AsSpan(1)));

        Assert.Equal(result.Tables.Sum(table => table.Rows), result.Metrics.TransferredRows);
        Assert.Equal(result.Tables.Sum(table => table.DecodedBytes), result.Metrics.DecodedBytes);
        Assert.Equal(result.DerivedTables.Sum(table => table.Rows), result.Metrics.DerivedRows);
        Assert.True(result.Metrics.TextPayloadBytes >= Encoding.UTF8.GetByteCount(payload));
        Assert.True(result.Metrics.TextSlices >= 3);
        Assert.InRange(result.Metrics.MaxMetadataRowsBuffered, 1, 256);
        Assert.Equal(
            TransferV3Limits.MaxDecodedChunkBytes,
            result.Metrics.MaxSqliteSliceBytesObserved);
        var largestFrame = TransferV3FrameCodec.SerializeMeasured(chunks[0]);
        try
        {
            Assert.Equal(
                largestFrame.Metrics.MaxManagedBufferBytesObserved,
                result.Metrics.MaxTransferOwnedManagedBufferBytesObserved);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(largestFrame.Bytes);
        }
        Assert.Equal(0, result.Metrics.MaxTransferOwnedNativeBufferBytesObserved);
        Assert.Equal(
            TransferV3Limits.MaxDecodedChunkBytes,
            result.Metrics.MaxRowCodecReadChunkBytesObserved);
        Assert.Equal(
            TransferV3Limits.MaxDecodedChunkBytes,
            result.Metrics.MaxRowCodecWrittenChunkBytesObserved);
    }

    [Fact]
    public async Task ExportAsync_MetricsIncludeReviewedContractBindingBuffers()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);
        var contractBytes = JsonSerializer.SerializeToUtf8Bytes(
            TransferV3SourceContract.LoadEmbedded());

        try
        {
            var result = await ExportAsync(source, limits, outputs);

            Assert.True(
                result.Metrics.MaxTransferOwnedManagedBufferBytesObserved
                >= contractBytes.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contractBytes);
        }
    }

    [Fact]
    public async Task ExportAsync_UsesMaximalBytePrefixAndAllowsOnlyOversizedSingleton()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES
                (1, 'a', 'h', 's'),
                (1, 'b', 'h', 's'),
                (1, 'c', 'hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh', 's');
            """);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 10,
            maxBatchBytes: 22);

        var result = await ExportAsync(source, limits, outputs);

        var frames = ParseFrames(outputs.Outputs[ExpectedFiles[0]].Bytes);
        var batchEnds = frames.OfType<TransferV3BatchEndFrame>().ToArray();
        Assert.Equal([2, 1], batchEnds.Select(frame => frame.Rows));
        Assert.Equal([22L, 60L], batchEnds.Select(frame => frame.Bytes));
        Assert.Equal(2, result.Tables[0].Batches);
        Assert.Equal(3, result.Tables[0].Rows);
        Assert.Equal(82, result.Tables[0].DecodedBytes);
        Assert.True(batchEnds[1].Bytes > limits.MaxBatchBytes);
    }

    [Fact]
    public async Task ExportAsync_PagesMetadataAt256RowsAndCreatesContiguousRowLimitedBatches()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            WITH RECURSIVE numbers(value) AS (
                SELECT 0
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 299
            )
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt)
            SELECT 1, printf('user-%03d', value), 'h', 's' FROM numbers;
            """);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 100,
            maxBatchBytes: 4 * 1024 * 1024);

        var result = await ExportAsync(source, limits, outputs);

        var descriptor = result.Tables[0];
        Assert.Equal(300, descriptor.Rows);
        Assert.Equal(3, descriptor.Batches);
        Assert.Equal(256, result.Metrics.MaxMetadataRowsBuffered);
        var batchEnds = ParseFrames(outputs.Outputs[ExpectedFiles[0]].Bytes)
            .OfType<TransferV3BatchEndFrame>()
            .ToArray();
        Assert.Equal([0, 1, 2], batchEnds.Select(frame => frame.Batch));
        Assert.Equal([100, 100, 100], batchEnds.Select(frame => frame.Rows));
        Assert.Equal(
            batchEnds[0].Cursor,
            ParseFrames(outputs.Outputs[ExpectedFiles[0]].Bytes)
                .OfType<TransferV3BatchStartFrame>()
                .Single(frame => frame.Batch == 1)
                .After);
    }

    [Fact]
    public async Task ExportAsync_EmitsExactTypedFieldsForCurrentOperationalTables()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string queueId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.InsertValidQueueItemAsync(queueId);
        await InsertCurrentOperationalRowsAsync(source);
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 4,
            maxBatchBytes: 4 * 1024 * 1024);

        var result = await ExportAsync(source, limits, outputs);

        string[] operationalTables =
        [
            "ArrImportCommands",
            "QueuePriorityHints",
            "ArrDownloadCorrelations",
            "ArrDownloadLifecycleEvents",
            "ArrSearchNudgeCommands",
            "ImportReceipts",
            "WorkerJobs",
            "MaintenanceRuns",
            "RcloneInvalidationItems",
        ];
        foreach (var tableName in operationalTables)
        {
            var descriptor = Assert.Single(result.Tables.Where(table => table.Name == tableName));
            Assert.Equal(1, descriptor.Rows);
            var file = result.Tables.IndexOf(descriptor);
            Assert.Single(ParseFrames(outputs.Outputs[ExpectedFiles[file]].Bytes)
                .OfType<TransferV3ChunkedRowStartFrame>());
        }

        var contract = TransferV3SourceContract.LoadEmbedded();
        var decodedTables = new Dictionary<string, TransferV3DecodedField[]>(StringComparer.Ordinal);
        foreach (var tableName in operationalTables.Prepend("QueueItems"))
        {
            var tableIndex = contract.Tables
                .Select((table, index) => (table, index))
                .Single(value => value.table.Name == tableName)
                .index;
            decodedTables.Add(
                tableName,
                DecodeOnlyRow(contract.Tables[tableIndex], outputs.Outputs[ExpectedFiles[tableIndex]].Bytes));
        }

        var hints = decodedTables["QueuePriorityHints"];
        Assert.Equal(Guid.Parse(queueId), hints[0].Value);
        Assert.Equal(7, hints[1].Value);
        Assert.Equal(-3, hints[2].Value);
        Assert.Equal(true, hints[3].Value);
        Assert.Equal("[\"fast\"]", DecodeText(hints[4]));
        Assert.Equal(638_700_000_000_000_000L, hints[6].Value);

        var queue = decodedTables["QueueItems"];
        Assert.Equal(Guid.Parse(queueId), queue[0].Value);
        Assert.Equal(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            queue[2].Value);
        Assert.Equal(1L, queue[5].Value);
        Assert.True(queue[6].IsNull);

        var worker = decodedTables["WorkerJobs"];
        Assert.Equal(1, worker[1].Value);
        Assert.Equal(42L, worker[17].Value);
        Assert.Equal("{\"job\":1}", DecodeText(worker[13]));

        var rclone = decodedTables["RcloneInvalidationItems"];
        Assert.Equal(0L, rclone[2].Value);
        Assert.Equal(42L, rclone[7].Value);

        var observedKinds = operationalTables.Prepend("QueueItems")
            .SelectMany(tableName => contract.Tables.Single(table => table.Name == tableName).Columns)
            .Select(column => column.Kind)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(Enum.GetValues<TransferV3ColumnKind>().Order(), observedKinds);
    }

    [Theory]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.MetadataPageRead)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.TextSliceRead)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.BeforeRowWrite)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.BeforeDurableClose)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.DerivedTableRead)]
    public async Task ExportAsync_CancelsAtEveryReadWriteClosePhaseAndDisposesOutputs(
        int targetValue)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingTableOutputFactory();
        using var cancellation = new CancellationTokenSource();
        var target = (TransferV3SqliteTableExportFaultPoint)targetValue;
        var hooks = new TransferV3SqliteTableExporterHooks(point =>
        {
            if (point == target) cancellation.Cancel();
        });
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExportAsync(source, limits, outputs, hooks, cancellation.Token));

        Assert.NotEmpty(outputs.CreatedNames);
        Assert.All(outputs.Outputs.Values, output => Assert.True(output.Disposed));
    }

    [Theory]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.MetadataPageRead)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.TextSliceRead)]
    [InlineData((int)TransferV3SqliteTableExportFaultPoint.DerivedTableRead)]
    public async Task ExportAsync_RedactsInjectedReadFailures(
        int targetValue)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new RecordingTableOutputFactory();
        var target = (TransferV3SqliteTableExportFaultPoint)targetValue;
        var hooks = new TransferV3SqliteTableExporterHooks(point =>
        {
            if (point == target) throw new IOException("read-secret");
        });
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            ExportAsync(source, limits, outputs, hooks));

        Assert.Equal("injected-fault", exception.Code);
        Assert.DoesNotContain("read-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.All(outputs.Outputs.Values, output => Assert.True(output.Disposed));
    }

    [Theory]
    [InlineData("write", "output-write")]
    [InlineData("enospc", "output-write")]
    [InlineData("fsync", "output-durable-close")]
    public async Task ExportAsync_RedactsWriteEnospcAndFsyncFailures(
        string fault,
        string expectedCode)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new StageFaultOutputFactory(fault);
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            ExportAsync(source, limits, outputs));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("stage-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.InRange(outputs.Created, 1, 26);
    }

    [Fact]
    public async Task ExportAsync_ReservedConfigIsRejectedBeforeAnyOutputIsCreated()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "INSERT INTO ConfigItems(ConfigName, ConfigValue) "
            + "VALUES ('database.import-state', '{\"formatVersion\":3}');");
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
            ExportAsync(source, limits, outputs));

        Assert.Contains("code=reserved-config", exception.Message, StringComparison.Ordinal);
        Assert.Empty(outputs.CreatedNames);
    }

    [Fact]
    public async Task ExportAsync_RejectsCustomExclusionContractBeforeCreatingOutput()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var excluded = new List<string>();
        var contract = TransferV3SourceContract.LoadEmbedded() with
        {
            ExcludedConfigKeys = excluded,
        };
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator(contract)
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance);
        excluded.Add("api.key");
        var outputs = new RecordingTableOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            session.RunExportAsync((context, token) =>
                new TransferV3SqliteTableExporter().ExportAsync(context, limits, outputs, token)));

        Assert.Equal("contract-shape", exception.Code);
        Assert.Empty(outputs.CreatedNames);
    }

    [Fact]
    public async Task ExportAsync_CancellationPreservesSanitizedOutputCleanupEvidence()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var outputs = new CancellationCleanupFaultOutputFactory();
        var hooks = new TransferV3SqliteTableExporterHooks(point =>
        {
            if (point == TransferV3SqliteTableExportFaultPoint.MetadataPageRead)
                cancellation.Cancel();
        });
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExportAsync(source, limits, outputs, hooks, cancellation.Token));

        Assert.Equal(
            ["output-dispose"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                exception.Data["TransferV3CleanupCodes"]));
        Assert.DoesNotContain("cancel-dispose-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_ResultIsRejectedWhenSourceMutatesBeforeSessionFinalization()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var originalWrite = File.GetLastWriteTimeUtc(source.DatabasePath);
        var mutated = false;
        var outputs = new RecordingTableOutputFactory();
        var hooks = new TransferV3SqliteTableExporterHooks(point =>
        {
            if (mutated || point != TransferV3SqliteTableExportFaultPoint.BeforeDurableClose)
                return;
            mutated = true;
            File.SetLastWriteTimeUtc(source.DatabasePath, DateTime.UnixEpoch.AddDays(13));
        });
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        try
        {
            var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(() =>
                ExportAsync(source, limits, outputs, hooks));
            Assert.Contains("code=source-stability", exception.Message, StringComparison.Ordinal);
            Assert.True(mutated);
            Assert.Equal(27, outputs.CreatedNames.Length);
            Assert.All(outputs.Outputs.Values, output => Assert.True(output.DurablyCompleted));
        }
        finally
        {
            File.SetLastWriteTimeUtc(source.DatabasePath, originalWrite);
        }
    }

    [Fact]
    public async Task ExportAsync_PreservesStablePrimaryAndSanitizedCleanupEvidence()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputs = new WriteAndDisposeFaultOutputFactory();
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 4 * 1024 * 1024);

        var exception = await Assert.ThrowsAsync<TransferV3TableExportException>(() =>
            ExportAsync(source, limits, outputs));

        Assert.Equal("output-write", exception.Code);
        Assert.True(exception.CleanupCodes.SequenceEqual(["output-dispose"]));
        Assert.DoesNotContain("write-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dispose-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.False(outputs.FaultingOutput.DurablyCompleted);
    }

    [Fact]
    public void ExporterSource_UsesOnlyChunkedRowsBoundedSqliteSlicesAndWholePageRedaction()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqliteTableExporter.cs"));

        Assert.Contains("WriteChunkedRowAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteRowAsync(", source, StringComparison.Ordinal);
        Assert.Contains("substr(CAST(", source, StringComparison.Ordinal);
        Assert.Contains("CommandBehavior.SequentialAccess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.GetString(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonDocument", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3ManifestCodec", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest.json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3Snapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlitePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DatabasePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobRootPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public ", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var bufferedRow in page)", source, StringComparison.Ordinal);
        Assert.Contains("bufferedRow.ClearSensitive();", source, StringComparison.Ordinal);

        var export = typeof(TransferV3SqliteTableExporter).GetMethod(
            "ExportAsync",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(export);
        Assert.False(export.IsPublic);
        Assert.False(typeof(TransferV3SqliteTableExporter).IsPublic);
        Assert.False(typeof(ITransferV3TableOutputFactory).IsPublic);
        Assert.False(typeof(ITransferV3TableOutput).IsPublic);
        Assert.False(typeof(TransferV3SqliteTableExportResult).IsPublic);
        Assert.False(typeof(TransferV3TableExportException).IsPublic);
        Assert.Equal(
            [
                typeof(TransferV3SqliteExportContext),
                typeof(TransferV3Limits),
                typeof(ITransferV3TableOutputFactory),
                typeof(CancellationToken),
            ],
            export.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(Task<TransferV3SqliteTableExportResult>), export.ReturnType);
    }

    private static string HealthRowsSql(bool reverse)
    {
        const string first =
            "('11111111-1111-1111-1111-111111111111', 86400, "
            + "'00000000-0000-0000-0000-000000000002', '/first', 0, 0, NULL)";
        const string second =
            "('22222222-2222-2222-2222-222222222222', 172800, "
            + "'00000000-0000-0000-0000-000000000002', '/second', 1, 2, NULL)";
        var values = reverse ? $"{second}, {first}" : $"{first}, {second}";
        return
            "INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message) "
            + $"VALUES {values};";
    }

    private static async Task InsertCurrentOperationalRowsAsync(
        TransferV3ValidationSource source)
    {
        await source.ExecuteAsync(
            """
            INSERT INTO HistoryItems(
                Id, CreatedAt, FileName, JobName, Category, DownloadStatus,
                TotalSegmentBytes, DownloadTimeSeconds)
            VALUES (
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '2026-01-01 00:00:00',
                'history.nzb', 'history', 'movies', 1, 1, 1);

            INSERT INTO ArrImportCommands(
                Id, HistoryItemId, Category, RequiredInvalidationPathsJson,
                Status, Attempts, CreatedAt, UpdatedAt, NextAttemptAt, ResultsJson)
            VALUES (
                '10000000-0000-0000-0000-000000000001',
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'movies', '["/movies"]',
                0, 1, 638700000000000000, 638700000000000001,
                638700000000000002, '{}');

            INSERT INTO QueuePriorityHints(
                QueueItemId, Score, EffectivePriority, ApplyToScheduling,
                ReasonsJson, Source, ComputedAt, ExpiresAt)
            VALUES (
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 7, -3, 1,
                '["fast"]', 'test', 638700000000000000, 638700000000000100);

            INSERT INTO ArrDownloadCorrelations(
                Id, QueueItemId, HistoryItemId, ArrApp, InstanceKey, InstanceHost,
                DownloadId, QueueRecordId, MediaKey, MovieId, EpisodeIdsJson,
                IsUpgrade, IsDuplicate, CreatedAt, UpdatedAt, LastSeenAt, Source, ManualLock)
            VALUES (
                '10000000-0000-0000-0000-000000000002',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                'radarr', 'main', 'http://arr', 'download', 9, 'movie:9', 9, '[9]',
                1, 0, 638700000000000000, 638700000000000001,
                638700000000000002, 'queue', 0);

            INSERT INTO ArrDownloadLifecycleEvents(
                Id, QueueItemId, HistoryItemId, ArrApp, InstanceKey,
                DownloadId, MediaKey, State, StateReason, CreatedAt)
            VALUES (
                '10000000-0000-0000-0000-000000000003',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                'radarr', 'main', 'download', 'movie:9', 'imported', NULL,
                638700000000000000);

            INSERT INTO ArrSearchNudgeCommands(
                Id, ArrApp, InstanceKey, InstanceHost, CommandName, CommandId,
                TargetsJson, Mode, Status, CooldownKey, Score, ReasonsJson,
                CreatedAt, CompletedAt, NextAllowedAt)
            VALUES (
                '10000000-0000-0000-0000-000000000004',
                'radarr', 'main', 'http://arr', 'MoviesSearch', 12,
                '[9]', 'automatic', 'completed', 'movie:9', 7, '["missing"]',
                638700000000000000, 638700000000000001, 638700000000000100);

            INSERT INTO ImportReceipts(
                Id, DavItemId, HistoryItemId, State, CreatedAt, UpdatedAt,
                ImportedAt, Detail)
            VALUES (
                '10000000-0000-0000-0000-000000000005',
                '00000000-0000-0000-0000-000000000002',
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                2, 638700000000000000, 638700000000000001,
                638700000000000002, 'visible');

            INSERT INTO WorkerJobs(
                Id, Kind, Status, TargetId, Priority, Attempts, CreatedAt,
                UpdatedAt, AvailableAt, PayloadJson, LeaseGeneration)
            VALUES (
                '10000000-0000-0000-0000-000000000006', 1, 0,
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 5, 1,
                638700000000000000, 638700000000000001,
                638700000000000002, '{"job":1}', 42);

            INSERT INTO MaintenanceRuns(
                Id, Kind, Status, RequestedBy, CreatedAt, UpdatedAt,
                ProgressCurrent, ProgressTotal, Message)
            VALUES (
                '10000000-0000-0000-0000-000000000007', 1, 0, 'test',
                638700000000000000, 638700000000000001, 3, 9, 'queued');

            INSERT INTO RcloneInvalidationItems(
                Id, Path, CreatedAt, NextAttemptAt, LastAttemptAt,
                Attempts, LastError, Revision)
            VALUES (
                '10000000-0000-0000-0000-000000000008', '/movies',
                0, 1, NULL, 2, NULL, 42);
            """);
    }

    private static async Task<TransferV3SqliteTableExportResult> ExportAsync(
        TransferV3ValidationSource source,
        TransferV3Limits limits,
        ITransferV3TableOutputFactory outputs,
        TransferV3SqliteTableExporterHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        var provenance = CaptureProvenance(source);
        await using var session = await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(MaxRowsPerBatch: 2),
                provenance,
                cancellationToken);
        return await session.RunExportAsync(
            (context, token) => new TransferV3SqliteTableExporter(hooks)
                .ExportAsync(context, limits, outputs, token),
            cancellationToken);
    }

    private static TransferV3SourceProvenance CaptureProvenance(
        TransferV3ValidationSource source)
    {
        using var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        return new TransferV3SourceProvenance(
            database.Identity,
            blobs.Identity,
            TimeZoneInfo.Utc.Id);
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }

    private static IReadOnlyList<TransferV3Frame> ParseFrames(byte[] bytes)
    {
        var frames = new List<TransferV3Frame>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            frames.Add(TransferV3FrameCodec.ParseCanonical(bytes.AsMemory(start, index - start)));
            start = index + 1;
        }

        Assert.Equal(bytes.Length, start);
        return frames;
    }

    private static byte[] Concatenate(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        using var output = new MemoryStream();
        foreach (var chunk in chunks) output.Write(chunk.Span);
        return output.ToArray();
    }

    private static void MutateAllContractAndValidationCollections(
        TransferV3SourceContract contract,
        TransferV3ValidatedSource validation)
    {
        var migrations = Mutable(contract.Migrations);
        foreach (var migration in migrations)
            Mutable(migration.AllowedProductVersions).Clear();

        var tables = contract.Tables.Concat(contract.DerivedTables).ToArray();
        foreach (var table in tables)
        {
            var columns = Mutable(table.Columns);
            foreach (var column in columns)
                Mutable(column.AllowedIntegers).Clear();

            var uniqueKeys = Mutable(table.UniqueKeys);
            foreach (var uniqueKey in uniqueKeys)
            {
                Mutable(uniqueKey.Columns).Clear();
                Mutable(uniqueKey.Components).Clear();
            }

            var references = Mutable(table.References);
            foreach (var reference in references)
            {
                Mutable(reference.Columns).Clear();
                Mutable(reference.PrincipalTables).Clear();
                Mutable(reference.PrincipalColumns).Clear();
                if (reference.PolymorphicCases is not null)
                    Mutable(reference.PolymorphicCases).Clear();
            }

            if (table.MetadataRule is { } metadata)
            {
                Mutable(metadata.Subtypes).Clear();
                foreach (var domain in metadata.TypeDomains)
                    Mutable(domain.SubTypes).Clear();
                Mutable(metadata.TypeDomains).Clear();
            }

            columns.Clear();
            Mutable(table.Keyset).Clear();
            uniqueKeys.Clear();
            references.Clear();
        }

        foreach (var table in validation.Tables)
            Mutable(table.Keyset).Clear();
        Mutable(validation.Tables).Clear();
        Mutable(validation.InformationalReferences).Clear();
        Mutable(contract.DerivedExcludedTables).Clear();
        Mutable(contract.ExcludedConfigKeys).Clear();
        Mutable(contract.Bootstrap.Config).Clear();
        Mutable(contract.Bootstrap.Roots).Clear();
        migrations.Clear();
        Mutable(contract.Tables).Clear();
        Mutable(contract.DerivedTables).Clear();
    }

    private static IList<T> Mutable<T>(IReadOnlyList<T> values) =>
        Assert.IsAssignableFrom<IList<T>>(values);

    private static TransferV3DecodedField[] DecodeOnlyRow(
        TransferV3TableContract table,
        byte[] bytes)
    {
        var frames = ParseFrames(bytes);
        var row = Assert.Single(frames.OfType<TransferV3ChunkedRowStartFrame>());
        Assert.Equal(table.Columns.Count, row.Fields);
        var chunks = frames.OfType<TransferV3FieldChunkFrame>()
            .Where(frame => frame.Cursor == row.Cursor)
            .ToArray();
        return table.Columns.Select((column, index) =>
                TransferV3RowCodec.DecodeField(
                    column,
                    Concatenate(chunks
                        .Where(frame => frame.Field == index)
                        .OrderBy(frame => frame.Chunk)
                        .Select(frame => frame.Data))))
            .ToArray();
    }

    private static string DecodeText(TransferV3DecodedField field) =>
        Encoding.UTF8.GetString(Assert.IsType<byte[]>(field.Value));

    private sealed class RecordingTableOutputFactory : ITransferV3TableOutputFactory
    {
        private readonly List<string> _createdNames = [];
        private readonly Dictionary<string, RecordingTableOutput> _outputs =
            new(StringComparer.Ordinal);

        internal ImmutableArray<string> CreatedNames => [.. _createdNames];
        internal IReadOnlyDictionary<string, RecordingTableOutput> Outputs => _outputs;

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = new RecordingTableOutput();
            if (!_outputs.TryAdd(fileName, output))
                throw new IOException("duplicate-output");
            _createdNames.Add(fileName);
            return ValueTask.FromResult<ITransferV3TableOutput>(output);
        }
    }

    private sealed class FirstBoundaryMutatingTableOutputFactory(
        string boundary,
        Action mutation)
        : ITransferV3TableOutputFactory
    {
        private readonly List<string> _createdNames = [];

        internal bool Mutated { get; private set; }
        internal ImmutableArray<string> CreatedNames => [.. _createdNames];

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (boundary == "create") MutateOnce();
            _createdNames.Add(fileName);
            return ValueTask.FromResult<ITransferV3TableOutput>(
                new BoundaryMutatingTableOutput(boundary, MutateOnce));
        }

        private void MutateOnce()
        {
            if (Mutated) return;
            mutation();
            Mutated = true;
        }
    }

    private sealed class BoundaryMutatingTableOutput(
        string boundary,
        Action mutateOnce) : ITransferV3TableOutput
    {
        private readonly Stream _stream = boundary == "write"
            ? new BoundaryMutatingWriteStream(mutateOnce)
            : new MemoryStream();

        public Stream Stream
        {
            get
            {
                if (boundary == "stream") mutateOnce();
                return _stream;
            }
        }

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BoundaryMutatingWriteStream(Action mutateOnce) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            mutateOnce();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class HostileBoundaryOutputFactory(string stage)
        : ITransferV3TableOutputFactory
    {
        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == "create") throw HostileFailure();
            if (stage == "null")
                return ValueTask.FromResult<ITransferV3TableOutput>(null!);
            return ValueTask.FromResult<ITransferV3TableOutput>(
                new HostileBoundaryTableOutput(stage));
        }
    }

    private sealed class HostileBoundaryTableOutput(string stage) : ITransferV3TableOutput
    {
        private readonly Stream _stream = stage == "write"
            ? new HostileWriteStream()
            : new MemoryStream();

        public Stream Stream => stage == "stream" ? throw HostileFailure() : _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == "durable") throw HostileFailure();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (stage == "dispose") throw HostileFailure();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostileWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(HostileFailure());
    }

    private sealed class RetryDisposeTableOutputFactory : ITransferV3TableOutputFactory
    {
        internal RetryDisposeTableOutput FirstOutput { get; } = new();

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ITransferV3TableOutput>(FirstOutput);
        }
    }

    private sealed class RetryDisposeTableOutput : ITransferV3TableOutput
    {
        private readonly MemoryStream _stream = new();

        internal int DisposeCalls { get; private set; }
        internal bool ResourceOpen { get; private set; } = true;
        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (DisposeCalls == 1) throw HostileFailure();
            ResourceOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private static TransferV3TableExportException HostileFailure() =>
        new("/private/path/secret");

    private sealed class RecordingTableOutput : ITransferV3TableOutput
    {
        private readonly MemoryStream _stream = new();

        internal byte[] Bytes => _stream.ToArray();
        internal bool DurablyCompleted { get; private set; }
        internal bool StreamClosed { get; private set; }
        internal bool Disposed { get; private set; }
        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurablyCompleted = true;
            _stream.Dispose();
            StreamClosed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WriteAndDisposeFaultOutputFactory : ITransferV3TableOutputFactory
    {
        internal WriteAndDisposeFaultTableOutput FaultingOutput { get; } = new();

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ITransferV3TableOutput>(
                fileName == ExpectedFiles[1]
                    ? FaultingOutput
                    : new RecordingTableOutput());
        }
    }

    private sealed class CancellationCleanupFaultOutputFactory : ITransferV3TableOutputFactory
    {
        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ITransferV3TableOutput>(
                new CancellationCleanupFaultOutput());
        }
    }

    private sealed class CancellationCleanupFaultOutput : ITransferV3TableOutput
    {
        private readonly MemoryStream _stream = new();

        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unexpected-complete");
        }

        public ValueTask DisposeAsync() =>
            ValueTask.FromException(new IOException("cancel-dispose-secret"));
    }

    private sealed class WriteAndDisposeFaultTableOutput : ITransferV3TableOutput
    {
        private readonly FailAfterWritesStream _stream = new(successfulWrites: 4);

        internal bool DurablyCompleted { get; private set; }
        public Stream Stream => _stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DurablyCompleted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() =>
            ValueTask.FromException(new IOException("dispose-secret"));
    }

    private sealed class StageFaultOutputFactory(string fault) : ITransferV3TableOutputFactory
    {
        internal int Created { get; private set; }

        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created++;
            if (fault == "fsync" && fileName == ExpectedFiles[0])
            {
                return ValueTask.FromResult<ITransferV3TableOutput>(
                    new StageFaultTableOutput(new MemoryStream(), failDurableClose: true));
            }

            if (fault is "write" or "enospc" && fileName == ExpectedFiles[1])
            {
                var message = fault == "enospc"
                    ? "ENOSPC stage-secret"
                    : "write stage-secret";
                return ValueTask.FromResult<ITransferV3TableOutput>(
                    new StageFaultTableOutput(
                        new FailAfterWritesStream(successfulWrites: 4, failureMessage: message),
                        failDurableClose: false));
            }

            return ValueTask.FromResult<ITransferV3TableOutput>(new RecordingTableOutput());
        }
    }

    private sealed class StageFaultTableOutput(
        Stream stream,
        bool failDurableClose) : ITransferV3TableOutput
    {
        public Stream Stream => stream;

        public ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failDurableClose)
                return ValueTask.FromException(new IOException("fsync stage-secret"));
            stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailAfterWritesStream(
        int successfulWrites,
        string failureMessage = "write-secret") : MemoryStream
    {
        private int _writes;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writes++ >= successfulWrites)
                return ValueTask.FromException(new IOException(failureMessage));
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}

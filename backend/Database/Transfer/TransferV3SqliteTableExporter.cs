using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Transfer;

internal interface ITransferV3TableOutputFactory
{
    ValueTask<ITransferV3TableOutput> CreateAsync(
        string fileName,
        CancellationToken cancellationToken);
}

internal interface ITransferV3TableOutput : IAsyncDisposable
{
    Stream Stream { get; }

    // On success this durably flushes and closes Stream before returning.
    // DisposeAsync is idempotent residual cleanup for both success and failure.
    ValueTask CompleteDurablyAsync(CancellationToken cancellationToken);
}

internal enum TransferV3SqliteTableExportFaultPoint
{
    MetadataPageRead,
    TextSliceRead,
    BeforeRowWrite,
    BeforeDurableClose,
    DerivedTableRead,
}

internal enum TransferV3SensitiveBufferKind
{
    FixedField,
    CachedText,
}

internal sealed record TransferV3SqliteTableExporterHooks(
    Action<TransferV3SqliteTableExportFaultPoint>? BeforeFaultPoint = null,
    Action<TransferV3SensitiveBufferKind, bool>? AfterSensitiveBufferCleared = null);

// Buffer maxima are the largest individual transfer-owned buffers observed.
// They exclude SQLite engine-owned expression/page memory and do not claim to be
// an aggregate managed-heap high-water mark.
internal sealed record TransferV3SqliteTableExportMetrics(
    long TransferredRows,
    long DerivedRows,
    long DecodedBytes,
    long TextFields,
    long TextPayloadBytes,
    long TextSlices,
    int MaxMetadataRowsBuffered,
    int MaxSqliteSliceBytesObserved,
    int MaxTransferOwnedManagedBufferBytesObserved,
    int MaxTransferOwnedNativeBufferBytesObserved,
    int MaxRowCodecReadChunkBytesObserved,
    int MaxRowCodecWrittenChunkBytesObserved);

internal sealed record TransferV3SqliteTableExportResult
{
    internal TransferV3SqliteTableExportResult(
        IEnumerable<TransferV3ManifestTable> tables,
        IEnumerable<TransferV3ManifestDerivedTable> derivedTables,
        TransferV3SqliteTableExportMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(derivedTables);
        Tables = tables.ToImmutableArray();
        DerivedTables = derivedTables.ToImmutableArray();
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    internal ImmutableArray<TransferV3ManifestTable> Tables { get; }

    internal ImmutableArray<TransferV3ManifestDerivedTable> DerivedTables { get; }

    internal TransferV3SqliteTableExportMetrics Metrics { get; }
}

internal sealed class TransferV3SqliteTableExporter
{
    private const int MetadataPageRows = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly TransferV3SqliteTableExporterHooks? _hooks;

    internal TransferV3SqliteTableExporter(TransferV3SqliteTableExporterHooks? hooks = null)
    {
        _hooks = hooks;
    }

    internal async Task<TransferV3SqliteTableExportResult> ExportAsync(
        TransferV3SqliteExportContext context,
        TransferV3Limits limits,
        ITransferV3TableOutputFactory outputFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(outputFactory);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metrics = new MutableMetrics();
            var inputs = BindReviewedInputs(
                context.Contract,
                context.Validation,
                metrics);
            var contract = inputs.Contract;
            var validation = inputs.Validation;
            var fieldMetrics = new TransferV3FieldStreamMetrics();
            var tables = ImmutableArray.CreateBuilder<TransferV3ManifestTable>(
                contract.Tables.Count);

            for (var index = 0; index < contract.Tables.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tables.Add(await ExportTableAsync(
                        context.Connection,
                        context.Transaction,
                        contract,
                        contract.Tables[index],
                        validation.Tables[index],
                        index,
                        limits,
                        outputFactory,
                        metrics,
                        fieldMetrics,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            var derived = ImmutableArray.CreateBuilder<TransferV3ManifestDerivedTable>(
                contract.DerivedTables.Count);
            foreach (var table in contract.DerivedTables)
            {
                derived.Add(await ComputeDerivedTableAsync(
                        context.Connection,
                        context.Transaction,
                        table,
                        metrics,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            ValidateFinalDescriptors(contract, tables, derived);

            return new TransferV3SqliteTableExportResult(
                tables,
                derived,
                metrics.Snapshot(fieldMetrics));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve sanitized cleanup evidence attached by the table scope.
            throw;
        }
        catch (TransferV3TableExportException)
        {
            throw;
        }
        catch (TransferV3RowFormatException exception)
        {
            throw Failure($"row-{exception.Code}");
        }
        catch
        {
            throw Failure("export-failed");
        }
    }

    private async Task<TransferV3ManifestTable> ExportTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        TransferV3TableContract table,
        TransferV3ValidatedTable validation,
        int tableIndex,
        TransferV3Limits limits,
        ITransferV3TableOutputFactory outputFactory,
        MutableMetrics metrics,
        TransferV3FieldStreamMetrics fieldMetrics,
        CancellationToken cancellationToken)
    {
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"table-{tableIndex + 1:000}-{table.Name}.jsonl");
        ITransferV3TableOutput? output = null;
        TransferV3JsonlWriter? writer = null;
        var completed = false;
        Exception? primaryFailure = null;
        try
        {
            output = await CreateOutputAsync(outputFactory, fileName, cancellationToken)
                .ConfigureAwait(false);
            Stream outputStream;
            try
            {
                outputStream = output.Stream;
                if (outputStream is null || !outputStream.CanWrite)
                    throw Failure("output-shape");
                writer = new TransferV3JsonlWriter(
                    outputStream,
                    table.Name,
                    limits,
                    metrics.ObserveManagedBuffer);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                throw Failure("output-shape");
            }
            await WriteOutputAsync(
                    () => writer.WriteTableHeaderAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            long afterOrdinal = 0;
            long exportedRows = 0;
            long exportedBytes = 0;
            var batchOpen = false;
            var batchIndex = 0;
            var batchRows = 0;
            long batchBytes = 0;
            string? previousBatchCursor = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await ReadMetadataPageAsync(
                        connection,
                        transaction,
                        table,
                        afterOrdinal,
                        metrics,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0) break;
                metrics.ObserveMetadataPage(page.Count);

                try
                {
                    foreach (var row in page)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var expectedOrdinal = checked(afterOrdinal + 1);
                            if (row.Ordinal != expectedOrdinal
                                || row.Ordinal > validation.RowCount)
                            {
                                throw Failure("ordinal-coverage");
                            }

                            var prepared = await PrepareRowAsync(
                                    connection,
                                    transaction,
                                    contract,
                                    table,
                                    row,
                                    limits,
                                    metrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            row.Prepared = prepared;

                            long combinedBytes;
                            try
                            {
                                combinedBytes = checked(batchBytes + prepared.EncodedBytes);
                            }
                            catch (OverflowException)
                            {
                                throw Failure("batch-bytes");
                            }

                            if (batchOpen
                                && (batchRows >= limits.MaxBatchRows
                                    || combinedBytes > limits.MaxBatchBytes))
                            {
                                previousBatchCursor = await EndBatchAsync(
                                        writer,
                                        batchRows,
                                        batchBytes,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                batchOpen = false;
                                batchRows = 0;
                                batchBytes = 0;
                                batchIndex = checked(batchIndex + 1);
                            }

                            if (!batchOpen)
                            {
                                await WriteOutputAsync(
                                        () => writer.StartBatchAsync(
                                            batchIndex,
                                            previousBatchCursor,
                                            cancellationToken),
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                batchOpen = true;
                            }

                            InvokeHook(
                                TransferV3SqliteTableExportFaultPoint.BeforeRowWrite,
                                cancellationToken);
                            var actualRowBytes = await WriteChunkedRowAsync(
                                    connection,
                                    transaction,
                                    writer,
                                    table,
                                    row,
                                    prepared,
                                    limits,
                                    metrics,
                                    fieldMetrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (actualRowBytes != prepared.EncodedBytes)
                                throw Failure("row-bytes");

                            batchRows = checked(batchRows + 1);
                            batchBytes = checked(batchBytes + actualRowBytes);
                            exportedRows = checked(exportedRows + 1);
                            exportedBytes = checked(exportedBytes + actualRowBytes);
                            metrics.ObserveTransferredRow(actualRowBytes);
                            afterOrdinal = row.Ordinal;
                        }
                        finally
                        {
                            row.ClearSensitive();
                        }
                    }
                }
                finally
                {
                    foreach (var bufferedRow in page)
                        bufferedRow.ClearSensitive();
                }
            }

            if (exportedRows != validation.RowCount)
                throw Failure("row-coverage");
            if (batchOpen)
            {
                _ = await EndBatchAsync(
                        writer,
                        batchRows,
                        batchBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var tableEnd = await WriteOutputAsync(
                    () => writer.EndTableAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (tableEnd.Rows != exportedRows || tableEnd.Bytes != exportedBytes)
                throw Failure("table-totals");
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;

            InvokeHook(
                TransferV3SqliteTableExportFaultPoint.BeforeDurableClose,
                cancellationToken);
            try
            {
                await output.CompleteDurablyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                throw Failure("output-durable-close");
            }

            try
            {
                await output.DisposeAsync().ConfigureAwait(false);
                output = null;
            }
            catch
            {
                throw Failure("output-dispose");
            }

            completed = true;
            return new TransferV3ManifestTable(
                table.Name,
                fileName,
                tableEnd.Batches,
                tableEnd.Rows,
                tableEnd.Bytes,
                tableEnd.Sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var failure = new OperationCanceledException(cancellationToken);
            primaryFailure = failure;
            throw failure;
        }
        catch (TransferV3TableExportException exception)
        {
            primaryFailure = exception;
            throw;
        }
        catch (TransferV3RowFormatException exception)
        {
            var failure = Failure($"row-{exception.Code}");
            primaryFailure = failure;
            throw failure;
        }
        catch
        {
            var failure = Failure(completed ? "output-state" : "table-export");
            primaryFailure = failure;
            throw failure;
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    RecordCleanupFailure(primaryFailure, "writer-dispose");
                }
            }

            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    RecordCleanupFailure(primaryFailure, "output-dispose");
                }
            }
        }
    }

    private static async ValueTask<ITransferV3TableOutput> CreateOutputAsync(
        ITransferV3TableOutputFactory outputFactory,
        string fileName,
        CancellationToken cancellationToken)
    {
        ITransferV3TableOutput? output;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            output = await outputFactory.CreateAsync(fileName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("output-create");
        }

        return output ?? throw Failure("output-shape");
    }

    private static async ValueTask WriteOutputAsync(
        Func<ValueTask> write,
        CancellationToken cancellationToken)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("output-write");
        }
    }

    private static async ValueTask<T> WriteOutputAsync<T>(
        Func<ValueTask<T>> write,
        CancellationToken cancellationToken)
    {
        try
        {
            return await write().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("output-write");
        }
    }

    private static async Task<string> EndBatchAsync(
        TransferV3JsonlWriter writer,
        int expectedRows,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var frame = await WriteOutputAsync(
                () => writer.EndBatchAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (frame.Rows != expectedRows || frame.Bytes != expectedBytes)
            throw Failure("batch-totals");
        return frame.Cursor;
    }

    private async Task<long> WriteChunkedRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3JsonlWriter writer,
        TransferV3TableContract table,
        RowMetadata row,
        PreparedRow prepared,
        TransferV3Limits limits,
        MutableMetrics metrics,
        TransferV3FieldStreamMetrics fieldMetrics,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteOutputAsync(
                    () => writer.StartChunkedRowAsync(
                        prepared.Cursor,
                        table.Columns.Count,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            long actualBytes = 0;
            for (var index = 0; index < table.Columns.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = table.Columns[index];
                var cell = row.Cells[index];
                if (column.Kind != TransferV3ColumnKind.Text)
                {
                    var encoded = prepared.FixedFields[index]
                                  ?? throw Failure("fixed-field");
                    await WriteOutputAsync(
                            () => writer.WriteFieldChunkAsync(
                                index,
                                encoded,
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    actualBytes = checked(actualBytes + encoded.Length);
                    continue;
                }

                IAsyncEnumerable<ReadOnlyMemory<byte>> chunks;
                if (cell.IsNull)
                {
                    chunks = EmptyChunks();
                }
                else if (prepared.CachedTextFields[index] is { } cached)
                {
                    chunks = OneChunk(cached);
                }
                else
                {
                    chunks = ReadTextChunksAsync(
                        connection,
                        transaction,
                        table.Name,
                        column.Name,
                        row.SourceRowId,
                        cell.TextBytes,
                        cell.ContentSha256!,
                        metrics,
                        cancellationToken);
                }

                var field = await TransferV3RowCodec.EncodeTextFieldAsync(
                        column,
                        cell.IsNull,
                        cell.IsNull ? 0 : cell.TextBytes,
                        limits.MaxFieldBytes,
                        chunks,
                        (chunk, token) => WriteOutputAsync(
                            () => writer.WriteFieldChunkAsync(index, chunk, token),
                            token),
                        fieldMetrics,
                        cancellationToken)
                    .ConfigureAwait(false);
                actualBytes = checked(actualBytes + field.EncodedBytes);
                metrics.ObserveTextField(field.PayloadBytes);
            }

            await WriteOutputAsync(
                    () => writer.EndChunkedRowAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return actualBytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3TableExportException)
        {
            throw;
        }
        catch (TransferV3RowFormatException)
        {
            throw;
        }
        catch
        {
            throw Failure("output-write");
        }
    }

    private async Task<PreparedRow> PrepareRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3SourceContract contract,
        TransferV3TableContract table,
        RowMetadata row,
        TransferV3Limits limits,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        var fixedFields = new byte[]?[table.Columns.Count];
        var cachedTextFields = new byte[]?[table.Columns.Count];
        var cursorValues = new object?[table.Columns.Count];
        long encodedBytes = 0;
        try
        {
            for (var index = 0; index < table.Columns.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var column = table.Columns[index];
                var cell = row.Cells[index];
                var isKey = table.Keyset.Any(value =>
                    string.Equals(value.Column, column.Name, StringComparison.Ordinal));

                if (cell.IsNull)
                {
                    if (isKey) throw Failure("key-null");
                    if (column.Kind == TransferV3ColumnKind.Text)
                    {
                        _ = TransferV3RowCodec.PlanField(
                            isNull: true,
                            payloadBytes: 0,
                            limits.MaxFieldBytes);
                        encodedBytes = checked(encodedBytes + 1);
                    }
                    else
                    {
                        var encoded = TransferV3RowCodec.EncodeField(column, null);
                        RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                        fixedFields[index] = encoded;
                        encodedBytes = checked(encodedBytes + encoded.Length);
                    }
                    continue;
                }

                switch (column.Kind)
                {
                    case TransferV3ColumnKind.Text:
                        {
                            _ = TransferV3RowCodec.PlanField(
                                isNull: false,
                                cell.TextBytes,
                                limits.MaxFieldBytes);
                            encodedBytes = checked(encodedBytes + checked(cell.TextBytes + 1));
                            if (!isKey) break;
                            if (cell.TextBytes > TransferV3CursorCodec.MaxTextBytes)
                                throw Failure("cursor-text-size");
                            var raw = await ReadTextFullyAsync(
                                    connection,
                                    transaction,
                                    table.Name,
                                    column.Name,
                                    row.SourceRowId,
                                    cell,
                                    TransferV3CursorCodec.MaxTextBytes,
                                    metrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            cachedTextFields[index] = raw;
                            cursorValues[index] = DecodeStrictText(raw);
                            break;
                        }
                    case TransferV3ColumnKind.Uuid:
                        {
                            var raw = await ReadTextFullyAsync(
                                    connection,
                                    transaction,
                                    table.Name,
                                    column.Name,
                                    row.SourceRowId,
                                    cell,
                                    maximumBytes: 36,
                                    metrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            try
                            {
                                var text = DecodeStrictText(raw);
                                if (text.Length != 36 || !Guid.TryParseExact(text, "D", out var uuid))
                                    throw Failure("uuid-format");
                                var encoded = TransferV3RowCodec.EncodeField(column, uuid);
                                RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                                fixedFields[index] = encoded;
                                encodedBytes = checked(encodedBytes + encoded.Length);
                                if (isKey) cursorValues[index] = uuid;
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(raw);
                            }
                            break;
                        }
                    case TransferV3ColumnKind.LocalWallTimestamp:
                        {
                            var raw = await ReadTextFullyAsync(
                                    connection,
                                    transaction,
                                    table.Name,
                                    column.Name,
                                    row.SourceRowId,
                                    cell,
                                    maximumBytes: 27,
                                    metrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            try
                            {
                                var encoded = TransferV3RowCodec.EncodeField(column, raw);
                                RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                                fixedFields[index] = encoded;
                                encodedBytes = checked(encodedBytes + encoded.Length);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(raw);
                            }
                            if (isKey) throw Failure("key-kind");
                            break;
                        }
                    case TransferV3ColumnKind.Boolean:
                        {
                            var integer = cell.Integer ?? throw Failure("integer-null");
                            if (integer is not 0 and not 1) throw Failure("boolean-domain");
                            var encoded = TransferV3RowCodec.EncodeField(column, integer == 1);
                            RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                            fixedFields[index] = encoded;
                            encodedBytes = checked(encodedBytes + encoded.Length);
                            if (isKey) cursorValues[index] = integer;
                            break;
                        }
                    case TransferV3ColumnKind.EnumInt32:
                    case TransferV3ColumnKind.Int32:
                        {
                            var integer = cell.Integer ?? throw Failure("integer-null");
                            int value;
                            try
                            {
                                value = checked((int)integer);
                            }
                            catch (OverflowException)
                            {
                                throw Failure("int32-range");
                            }
                            var encoded = TransferV3RowCodec.EncodeField(column, value);
                            RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                            fixedFields[index] = encoded;
                            encodedBytes = checked(encodedBytes + encoded.Length);
                            if (isKey) cursorValues[index] = integer;
                            break;
                        }
                    case TransferV3ColumnKind.Int64:
                    case TransferV3ColumnKind.Instant:
                        {
                            var integer = cell.Integer ?? throw Failure("integer-null");
                            var encoded = TransferV3RowCodec.EncodeField(column, integer);
                            RequireFieldLimit(encoded.Length, limits.MaxFieldBytes);
                            fixedFields[index] = encoded;
                            encodedBytes = checked(encodedBytes + encoded.Length);
                            if (isKey) cursorValues[index] = integer;
                            break;
                        }
                    default:
                        throw Failure("column-kind");
                }
            }

            var components = new TransferV3CursorComponent[table.Keyset.Count];
            for (var index = 0; index < table.Keyset.Count; index++)
            {
                var key = table.Keyset[index];
                var columnIndex = FindColumnIndex(table, key.Column);
                var column = table.Columns[columnIndex];
                var value = cursorValues[columnIndex];
                components[index] = column.Kind switch
                {
                    TransferV3ColumnKind.Uuid when value is Guid uuid =>
                        TransferV3CursorComponent.FromGuid(uuid),
                    TransferV3ColumnKind.Text when value is string text =>
                        TransferV3CursorComponent.FromText(text),
                    TransferV3ColumnKind.Boolean
                        or TransferV3ColumnKind.EnumInt32
                        or TransferV3ColumnKind.Int32
                        or TransferV3ColumnKind.Int64
                        or TransferV3ColumnKind.Instant when value is long integer =>
                        TransferV3CursorComponent.FromInt64(integer),
                    _ => throw Failure("cursor-kind"),
                };
            }

            if (string.Equals(table.Name, "ConfigItems", StringComparison.Ordinal)
                && cursorValues[FindColumnIndex(table, "ConfigName")] is string configName
                && contract.ExcludedConfigKeys.Contains(configName, StringComparer.Ordinal))
            {
                throw Failure("reserved-config");
            }

            return new PreparedRow(
                TransferV3CursorCodec.Encode(components),
                fixedFields,
                cachedTextFields,
                encodedBytes);
        }
        catch
        {
            foreach (var value in fixedFields)
            {
                if (value is not null) CryptographicOperations.ZeroMemory(value);
            }
            foreach (var value in cachedTextFields)
            {
                if (value is not null) CryptographicOperations.ZeroMemory(value);
            }
            ReportClearedBuffers(
                fixedFields,
                TransferV3SensitiveBufferKind.FixedField);
            ReportClearedBuffers(
                cachedTextFields,
                TransferV3SensitiveBufferKind.CachedText);
            throw;
        }
    }

    private void ReportClearedBuffers(
        IEnumerable<byte[]?> buffers,
        TransferV3SensitiveBufferKind kind)
    {
        if (_hooks?.AfterSensitiveBufferCleared is not { } report) return;
        foreach (var buffer in buffers)
        {
            if (buffer is null) continue;
            var cleared = buffer.All(value => value == 0);
            try
            {
                report(kind, cleared);
            }
            catch
            {
                // Test/diagnostic hooks cannot replace the sanitized primary failure.
            }
        }
    }

    private async Task<byte[]> ReadTextFullyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long sourceRowId,
        CellMetadata cell,
        int maximumBytes,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (cell.IsNull
            || cell.ContentSha256 is not { Length: 32 }
            || cell.TextBytes < 0
            || cell.TextBytes > maximumBytes)
        {
            throw Failure("bounded-text-size");
        }

        var result = new byte[checked((int)cell.TextBytes)];
        metrics.ObserveManagedBuffer(result.Length);
        try
        {
            var written = 0;
            await foreach (var chunk in ReadTextChunksAsync(
                               connection,
                               transaction,
                               table,
                               column,
                               sourceRowId,
                               cell.TextBytes,
                               cell.ContentSha256,
                               metrics,
                               cancellationToken))
            {
                chunk.CopyTo(result.AsMemory(written));
                written = checked(written + chunk.Length);
            }

            if (written != result.Length)
                throw Failure("text-length");
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadTextChunksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long sourceRowId,
        long expectedBytes,
        byte[] expectedSha256,
        MutableMetrics metrics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < expectedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(
                TransferV3Limits.MaxDecodedChunkBytes,
                expectedBytes - offset));
            var chunk = await ReadTextSliceAsync(
                    connection,
                    transaction,
                    table,
                    column,
                    sourceRowId,
                    checked(offset + 1),
                    count,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (chunk.Length != count) throw Failure("text-slice-length");
                metrics.ObserveSlice(chunk.Length);
                hasher.AppendData(chunk);
                offset = checked(offset + chunk.Length);
                yield return chunk;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
            }
        }

        var actual = hasher.GetHashAndReset();
        try
        {
            if (offset != expectedBytes
                || expectedSha256.Length != actual.Length
                || !CryptographicOperations.FixedTimeEquals(expectedSha256, actual))
            {
                throw Failure("text-integrity");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private async Task<byte[]> ReadTextSliceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long sourceRowId,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        InvokeHook(
            TransferV3SqliteTableExportFaultPoint.TextSliceRead,
            cancellationToken);
        byte[]? bytes = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT substr(CAST({QuoteIdentifier(column)} AS BLOB), $offset, $count) "
                + $"FROM source.{QuoteIdentifier(table)} "
                + $"WHERE rowid = $rowid AND typeof({QuoteIdentifier(column)}) = 'text';";
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$count", count);
            command.Parameters.AddWithValue("$rowid", sourceRowId);
            await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.IsDBNull(0))
            {
                throw Failure("text-source");
            }
            bytes = reader.GetFieldValue<byte[]>(0);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Failure("text-source");
            }
            var result = bytes;
            bytes = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3TableExportException)
        {
            throw;
        }
        catch
        {
            throw Failure("source-read");
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<List<RowMetadata>> ReadMetadataPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        long afterOrdinal,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        InvokeHook(
            TransferV3SqliteTableExportFaultPoint.MetadataPageRead,
            cancellationToken);
        var rows = new List<RowMetadata>(MetadataPageRows);
        CellMetadata[]? activeCells = null;
        try
        {
            await using var command = BuildMetadataPageCommand(
                connection,
                transaction,
                table,
                afterOrdinal);
            await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,
                    cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = 0;
                var scanOrdinal = reader.GetInt64(ordinal++);
                var sourceRowId = reader.GetInt64(ordinal++);
                activeCells = new CellMetadata[table.Columns.Count];
                for (var index = 0; index < table.Columns.Count; index++)
                {
                    var column = table.Columns[index];
                    var storage = reader.GetFieldValue<string>(ordinal++);
                    if (storage == "null")
                    {
                        if (!column.Nullable) throw Failure("required-null");
                        if (column.RawStorageClass == "text")
                        {
                            if (!reader.IsDBNull(ordinal++)
                                || !reader.IsDBNull(ordinal++)
                                || !reader.IsDBNull(ordinal++))
                            {
                                throw Failure("validated-field-null");
                            }
                        }
                        else
                        {
                            if (!reader.IsDBNull(ordinal++))
                                throw Failure("integer-null");
                        }
                        activeCells[index] = CellMetadata.Null;
                        continue;
                    }

                    if (!string.Equals(storage, column.RawStorageClass, StringComparison.Ordinal))
                        throw Failure("storage-class");
                    if (column.RawStorageClass == "text")
                    {
                        if (reader.IsDBNull(ordinal)
                            || reader.IsDBNull(ordinal + 1)
                            || reader.IsDBNull(ordinal + 2))
                        {
                            throw Failure("validated-field-missing");
                        }
                        var sourceBytes = reader.GetInt64(ordinal++);
                        var retainedBytes = reader.GetInt64(ordinal++);
                        var digest = reader.GetFieldValue<byte[]>(ordinal++);
                        if (sourceBytes < 0
                            || sourceBytes != retainedBytes
                            || digest.Length != 32)
                        {
                            CryptographicOperations.ZeroMemory(digest);
                            throw Failure("validated-field-shape");
                        }
                        metrics.ObserveManagedBuffer(digest.Length);
                        activeCells[index] = new CellMetadata(
                            IsNull: false,
                            Integer: null,
                            TextBytes: sourceBytes,
                            ContentSha256: digest);
                    }
                    else if (column.RawStorageClass == "integer")
                    {
                        if (reader.IsDBNull(ordinal)) throw Failure("integer-null");
                        activeCells[index] = new CellMetadata(
                            IsNull: false,
                            Integer: reader.GetInt64(ordinal++),
                            TextBytes: 0,
                            ContentSha256: null);
                    }
                    else
                    {
                        throw Failure("storage-contract");
                    }
                }
                rows.Add(new RowMetadata(scanOrdinal, sourceRowId, activeCells));
                activeCells = null;
            }
            return rows;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearMetadataRows(rows, activeCells);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3TableExportException)
        {
            ClearMetadataRows(rows, activeCells);
            throw;
        }
        catch
        {
            ClearMetadataRows(rows, activeCells);
            throw Failure("source-metadata");
        }
    }

    private static SqliteCommand BuildMetadataPageCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        long afterOrdinal)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var select = new List<string>
        {
            "o.ordinal",
            "o.source_rowid",
        };
        var textIndex = 0;
        foreach (var column in table.Columns)
        {
            var quoted = QuoteIdentifier(column.Name);
            select.Add($"typeof(s.{quoted})");
            if (column.RawStorageClass == "text")
            {
                var parameter = $"$column{textIndex++}";
                select.Add($"CASE WHEN typeof(s.{quoted}) = 'text' THEN length(CAST(s.{quoted} AS BLOB)) END");
                select.Add(
                    "(SELECT vf.length_bytes FROM scratch.validated_fields AS vf "
                    + "WHERE vf.table_name = $table_blob "
                    + "AND vf.source_rowid = o.source_rowid "
                    + $"AND vf.column_name = {parameter})");
                select.Add(
                    "(SELECT vf.content_sha256 FROM scratch.validated_fields AS vf "
                    + "WHERE vf.table_name = $table_blob "
                    + "AND vf.source_rowid = o.source_rowid "
                    + $"AND vf.column_name = {parameter})");
                command.Parameters.Add(parameter, SqliteType.Blob).Value =
                    Encoding.UTF8.GetBytes(column.Name);
            }
            else
            {
                select.Add($"s.{quoted}");
            }
        }

        command.CommandText =
            $"SELECT {string.Join(", ", select)} "
            + "FROM scratch.scan_ordinals AS o "
            + $"JOIN source.{QuoteIdentifier(table.Name)} AS s ON s.rowid = o.source_rowid "
            + "WHERE o.table_name = $table_text AND o.ordinal > $after "
            + "ORDER BY o.ordinal LIMIT $limit;";
        command.Parameters.AddWithValue("$table_text", table.Name);
        command.Parameters.Add("$table_blob", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(table.Name);
        command.Parameters.AddWithValue("$after", afterOrdinal);
        command.Parameters.AddWithValue("$limit", MetadataPageRows);
        return command;
    }

    private async Task<TransferV3ManifestDerivedTable> ComputeDerivedTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferV3TableContract table,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(table.Name, "HealthCheckStats", StringComparison.Ordinal)
            || table.Columns.Count != 5
            || table.Keyset.Count != 4)
        {
            throw Failure("derived-contract");
        }

        InvokeHook(
            TransferV3SqliteTableExportFaultPoint.DerivedTableRead,
            cancellationToken);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT typeof(DateStartInclusive), DateStartInclusive, "
                + "typeof(DateEndExclusive), DateEndExclusive, "
                + "typeof(Result), Result, typeof(RepairStatus), RepairStatus, "
                + "typeof(Count), Count FROM source.HealthCheckStats "
                + "ORDER BY DateStartInclusive, DateEndExclusive, Result, RepairStatus;";
            long rows = 0;
            var int32 = new byte[sizeof(int)];
            var int64 = new byte[sizeof(long)];
            metrics.ObserveManagedBuffer(int64.Length);
            await using var reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = new long[5];
                    for (var index = 0; index < values.Length; index++)
                    {
                        var storageOrdinal = index * 2;
                        var valueOrdinal = storageOrdinal + 1;
                        if (!string.Equals(
                                reader.GetFieldValue<string>(storageOrdinal),
                                "integer",
                                StringComparison.Ordinal)
                            || reader.IsDBNull(valueOrdinal))
                        {
                            throw Failure("derived-storage");
                        }
                        values[index] = reader.GetInt64(valueOrdinal);
                    }

                    var encodedFields = new byte[table.Columns.Count][];
                    for (var index = 0; index < table.Columns.Count; index++)
                    {
                        encodedFields[index] = table.Columns[index].Kind switch
                        {
                            TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 =>
                                TransferV3RowCodec.EncodeField(
                                    table.Columns[index],
                                    checked((int)values[index])),
                            TransferV3ColumnKind.Instant =>
                                TransferV3RowCodec.EncodeField(table.Columns[index], values[index]),
                            _ => throw Failure("derived-kind"),
                        };
                    }

                    var cursor = TransferV3CursorCodec.Encode(
                        TransferV3CursorComponent.FromInt64(values[0]),
                        TransferV3CursorComponent.FromInt64(values[1]),
                        TransferV3CursorComponent.FromInt64(values[2]),
                        TransferV3CursorComponent.FromInt64(values[3]));
                    var cursorBytes = Encoding.ASCII.GetBytes(cursor);
                    BinaryPrimitives.WriteInt32BigEndian(int32, cursorBytes.Length);
                    hash.AppendData(int32);
                    hash.AppendData(cursorBytes);
                    BinaryPrimitives.WriteInt32BigEndian(int32, encodedFields.Length);
                    hash.AppendData(int32);
                    foreach (var field in encodedFields)
                    {
                        BinaryPrimitives.WriteInt64BigEndian(int64, field.Length);
                        hash.AppendData(int64);
                        hash.AppendData(field);
                    }
                    rows = checked(rows + 1);
                }

                var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                metrics.ObserveDerivedRows(rows);
                return new TransferV3ManifestDerivedTable(table.Name, rows, digest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(int32);
                CryptographicOperations.ZeroMemory(int64);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3TableExportException)
        {
            throw;
        }
        catch (TransferV3RowFormatException)
        {
            throw;
        }
        catch (OverflowException)
        {
            throw Failure("derived-range");
        }
        catch
        {
            throw Failure("derived-read");
        }
    }

    private void InvokeHook(
        TransferV3SqliteTableExportFaultPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _hooks?.BeforeFaultPoint?.Invoke(point);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("injected-fault");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static FrozenInputs BindReviewedInputs(
        TransferV3SourceContract suppliedContract,
        TransferV3ValidatedSource suppliedValidation,
        MutableMetrics metrics)
    {
        byte[]? reviewedBytes = null;
        byte[]? suppliedBytes = null;
        try
        {
            var reviewed = TransferV3SourceContract.LoadEmbedded();
            reviewedBytes = JsonSerializer.SerializeToUtf8Bytes(reviewed);
            metrics.ObserveManagedBuffer(reviewedBytes.Length);
            suppliedBytes = JsonSerializer.SerializeToUtf8Bytes(suppliedContract);
            metrics.ObserveManagedBuffer(suppliedBytes.Length);
            if (!reviewedBytes.AsSpan().SequenceEqual(suppliedBytes))
                throw Failure("contract-shape");

            var contract = FreezeContract(reviewed);
            var validation = FreezeValidation(suppliedValidation);
            ValidateSessionShape(contract, validation);
            return new FrozenInputs(contract, validation);
        }
        catch
        {
            throw Failure("contract-shape");
        }
        finally
        {
            if (reviewedBytes is not null)
                CryptographicOperations.ZeroMemory(reviewedBytes);
            if (suppliedBytes is not null)
                CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }

    private static TransferV3SourceContract FreezeContract(TransferV3SourceContract source) =>
        new(
            source.FormatVersion,
            source.Provider,
            source.Context,
            source.HistoryTable,
            source.SourceModeledTableCount,
            source.SourceModeledColumnCount,
            source.DerivedExcludedTables.ToImmutableArray(),
            source.ExcludedConfigKeys.ToImmutableArray(),
            source.SourceSchemaSha256,
            source.MigrationSourceContractSha256,
            source.Migrations.Select(migration => new TransferV3MigrationContract(
                    migration.Id,
                    migration.IntroducedProductVersion,
                    migration.AllowedProductVersions.ToImmutableArray()))
                .ToImmutableArray(),
            source.Tables.Select(FreezeTable).ToImmutableArray(),
            source.DerivedTables.Select(FreezeTable).ToImmutableArray(),
            new TransferV3BootstrapContract(
                source.Bootstrap.Config
                    .Select(value => value with { })
                    .ToImmutableArray(),
                source.Bootstrap.Roots
                    .Select(value => value with { })
                    .ToImmutableArray()),
            source.Blobs with { });

    private static TransferV3TableContract FreezeTable(TransferV3TableContract table) =>
        new(
            table.Name,
            table.Columns.Select(column => new TransferV3ColumnContract(
                    column.Name,
                    column.DeclaredType,
                    column.RawStorageClass,
                    column.Nullable,
                    column.Kind,
                    column.InstantEncoding,
                    column.UuidRole,
                    column.MaxRunes,
                    column.AllowedIntegers.ToImmutableArray()))
                .ToImmutableArray(),
            table.Keyset.Select(value => value with { }).ToImmutableArray(),
            table.UniqueKeys.Select(unique => new TransferV3UniqueKeyContract(
                    unique.Name,
                    unique.Columns.ToImmutableArray(),
                    unique.Components.Select(value => value with { }).ToImmutableArray()))
                .ToImmutableArray(),
            table.References.Select(reference => new TransferV3ReferenceContract(
                    reference.Name,
                    reference.Columns.ToImmutableArray(),
                    reference.PrincipalTables.ToImmutableArray(),
                    reference.PrincipalColumns.ToImmutableArray(),
                    reference.Policy,
                    reference.Rationale,
                    reference.DiscriminatorColumn,
                    reference.PolymorphicCases?.Select(value => value with { }).ToImmutableArray()))
                .ToImmutableArray(),
            table.MetadataRule is null
                ? null
                : new TransferV3MetadataRuleContract(
                    table.MetadataRule.TypeColumn,
                    table.MetadataRule.SubTypeColumn,
                    table.MetadataRule.FileBlobColumn,
                    table.MetadataRule.Subtypes
                        .Select(value => value with { })
                        .ToImmutableArray(),
                    table.MetadataRule.TypeDomains
                        .Select(domain => new TransferV3TypeSubtypeDomainContract(
                            domain.Type,
                            domain.SubTypes.ToImmutableArray()))
                        .ToImmutableArray()));

    private static TransferV3ValidatedSource FreezeValidation(
        TransferV3ValidatedSource validation) =>
        new(
            validation.ContractSha256,
            validation.Tables.Select(table => new TransferV3ValidatedTable(
                    table.Name,
                    table.RowCount,
                    table.Keyset.Select(value => value with { }).ToImmutableArray(),
                    table.SqliteOrderExpression,
                    table.PostgreSqlOrderExpression))
                .ToImmutableArray(),
            validation.InformationalReferences
                .Select(value => value with { })
                .ToImmutableArray(),
            validation.Blobs with { },
            validation.MaxObservedRowsPerBatch,
            validation.MaxObservedBytesPerBatch,
            validation.MaxObservedIoBufferBytes);

    private static void ValidateSessionShape(
        TransferV3SourceContract contract,
        TransferV3ValidatedSource validation)
    {
        if (contract.Tables.Count != 27
            || contract.Tables.Sum(table => table.Columns.Count) != 235
            || contract.DerivedTables.Count != 1
            || validation.Tables.Count != contract.Tables.Count
            || !string.Equals(
                validation.ContractSha256,
                contract.ComputeSha256(),
                StringComparison.Ordinal)
            || validation.MaxObservedRowsPerBatch < 0
            || validation.MaxObservedBytesPerBatch < 0
            || validation.MaxObservedIoBufferBytes <= 0
            || validation.Blobs.Count < 0
            || validation.Blobs.TotalBytes < 0
            || !IsCanonicalDigest(validation.Blobs.Sha256))
        {
            throw Failure("contract-shape");
        }

        for (var index = 0; index < contract.Tables.Count; index++)
        {
            var expected = contract.Tables[index];
            var actual = validation.Tables[index];
            if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
                || actual.RowCount < 0
                || !expected.Keyset.SequenceEqual(actual.Keyset)
                || !string.Equals(
                    actual.SqliteOrderExpression,
                    BuildOrderExpression(expected.Keyset, sqlite: true),
                    StringComparison.Ordinal)
                || !string.Equals(
                    actual.PostgreSqlOrderExpression,
                    BuildOrderExpression(expected.Keyset, sqlite: false),
                    StringComparison.Ordinal))
            {
                throw Failure("contract-shape");
            }
        }

        var expectedReferences = contract.Tables
            .SelectMany(table => table.References)
            .Where(reference => reference.Policy is
                TransferV3ReferencePolicy.InformationalDigest
                or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
            .Select(reference => reference.Name);
        if (!expectedReferences.SequenceEqual(
                validation.InformationalReferences.Select(value => value.Name),
                StringComparer.Ordinal)
            || validation.InformationalReferences.Any(value =>
                value.UnresolvedCount < 0 || !IsCanonicalDigest(value.UnresolvedSha256)))
        {
            throw Failure("contract-shape");
        }
    }

    private static string BuildOrderExpression(
        IReadOnlyList<TransferV3KeyComponentContract> keyset,
        bool sqlite) =>
        string.Join(", ", keyset.Select(component =>
            $"{QuoteIdentifier(component.Column)}"
            + ((sqlite ? component.SqliteCollation : component.PostgreSqlCollation) is { } collation
               && collation != "none"
                ? $" COLLATE {QuoteIdentifier(collation)}"
                : "")));

    private static bool IsCanonicalDigest(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateFinalDescriptors(
        TransferV3SourceContract contract,
        ImmutableArray<TransferV3ManifestTable>.Builder tables,
        ImmutableArray<TransferV3ManifestDerivedTable>.Builder derived)
    {
        if (tables.Count != contract.Tables.Count
            || derived.Count != contract.DerivedTables.Count)
        {
            throw Failure("descriptor-shape");
        }

        for (var index = 0; index < tables.Count; index++)
        {
            var expected = contract.Tables[index];
            var expectedFile = string.Create(
                CultureInfo.InvariantCulture,
                $"table-{index + 1:000}-{expected.Name}.jsonl");
            var actual = tables[index];
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(actual.File, expectedFile, StringComparison.Ordinal)
                || actual.Batches < 0
                || actual.Rows < 0
                || actual.DecodedBytes < 0
                || !IsCanonicalDigest(actual.Sha256))
            {
                throw Failure("descriptor-shape");
            }
        }

        for (var index = 0; index < derived.Count; index++)
        {
            var actual = derived[index];
            if (!string.Equals(
                    actual.Name,
                    contract.DerivedTables[index].Name,
                    StringComparison.Ordinal)
                || actual.Rows < 0
                || !IsCanonicalDigest(actual.LogicalSha256))
            {
                throw Failure("descriptor-shape");
            }
        }
    }

    private static void RequireFieldLimit(int encodedBytes, long maxFieldBytes)
    {
        if (encodedBytes <= 0 || encodedBytes > maxFieldBytes)
            throw Failure("field-size");
    }

    private static int FindColumnIndex(TransferV3TableContract table, string name)
    {
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        throw Failure("key-column");
    }

    private static string DecodeStrictText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Failure("utf8");
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> OneChunk(byte[] bytes)
    {
        await Task.CompletedTask;
        if (bytes.Length != 0) yield return bytes;
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static TransferV3TableExportException Failure(string code) => new(code);

    private static void RecordCleanupFailure(Exception? primaryFailure, string code)
    {
        if (primaryFailure is TransferV3TableExportException tableFailure)
        {
            tableFailure.RecordCleanupFailure(code);
            return;
        }

        if (primaryFailure is not null)
        {
            var existing = primaryFailure.Data["TransferV3CleanupCodes"]
                as IReadOnlyList<string>;
            var codes = ImmutableArray.CreateBuilder<string>((existing?.Count ?? 0) + 1);
            if (existing is not null) codes.AddRange(existing);
            if (!codes.Contains(code, StringComparer.Ordinal)) codes.Add(code);
            primaryFailure.Data["TransferV3CleanupCodes"] = codes.ToImmutable();
            return;
        }

        throw Failure(code);
    }

    private static void ClearMetadataRows(
        IEnumerable<RowMetadata> rows,
        IEnumerable<CellMetadata?>? activeCells)
    {
        foreach (var row in rows) row.ClearSensitive();
        if (activeCells is null) return;
        foreach (var cell in activeCells)
        {
            if (cell?.ContentSha256 is not null)
                CryptographicOperations.ZeroMemory(cell.ContentSha256);
        }
    }

    private sealed record FrozenInputs(
        TransferV3SourceContract Contract,
        TransferV3ValidatedSource Validation);

    private sealed class RowMetadata(long ordinal, long sourceRowId, CellMetadata[] cells)
    {
        internal long Ordinal { get; } = ordinal;
        internal long SourceRowId { get; } = sourceRowId;
        internal CellMetadata[] Cells { get; } = cells;
        internal PreparedRow? Prepared { get; set; }

        internal void ClearSensitive()
        {
            Prepared?.ClearSensitive();
            foreach (var cell in Cells)
            {
                if (cell.ContentSha256 is not null)
                    CryptographicOperations.ZeroMemory(cell.ContentSha256);
            }
        }
    }

    private sealed record CellMetadata(
        bool IsNull,
        long? Integer,
        long TextBytes,
        byte[]? ContentSha256)
    {
        internal static readonly CellMetadata Null = new(true, null, 0, null);
    }

    private sealed class PreparedRow(
        string cursor,
        byte[]?[] fixedFields,
        byte[]?[] cachedTextFields,
        long encodedBytes)
    {
        internal string Cursor { get; } = cursor;
        internal byte[]?[] FixedFields { get; } = fixedFields;
        internal byte[]?[] CachedTextFields { get; } = cachedTextFields;
        internal long EncodedBytes { get; } = encodedBytes;

        internal void ClearSensitive()
        {
            foreach (var value in FixedFields)
            {
                if (value is not null) CryptographicOperations.ZeroMemory(value);
            }
            foreach (var value in CachedTextFields)
            {
                if (value is not null) CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private sealed class MutableMetrics
    {
        private long _transferredRows;
        private long _derivedRows;
        private long _decodedBytes;
        private long _textFields;
        private long _textPayloadBytes;
        private long _textSlices;
        private int _maxMetadataRows;
        private int _maxSqliteSliceBytes;
        private int _maxManagedBufferBytes;

        internal void ObserveMetadataPage(int rows) =>
            _maxMetadataRows = Math.Max(_maxMetadataRows, rows);

        internal void ObserveSlice(int bytes)
        {
            _textSlices = checked(_textSlices + 1);
            _maxSqliteSliceBytes = Math.Max(_maxSqliteSliceBytes, bytes);
            ObserveManagedBuffer(bytes);
        }

        internal void ObserveManagedBuffer(int bytes) =>
            _maxManagedBufferBytes = Math.Max(_maxManagedBufferBytes, bytes);

        internal void ObserveTextField(long payloadBytes)
        {
            _textFields = checked(_textFields + 1);
            _textPayloadBytes = checked(_textPayloadBytes + payloadBytes);
        }

        internal void ObserveTransferredRow(long bytes)
        {
            _transferredRows = checked(_transferredRows + 1);
            _decodedBytes = checked(_decodedBytes + bytes);
        }

        internal void ObserveDerivedRows(long rows) =>
            _derivedRows = checked(_derivedRows + rows);

        internal TransferV3SqliteTableExportMetrics Snapshot(
            TransferV3FieldStreamMetrics fieldMetrics) =>
            new(
                _transferredRows,
                _derivedRows,
                _decodedBytes,
                _textFields,
                _textPayloadBytes,
                _textSlices,
                _maxMetadataRows,
                _maxSqliteSliceBytes,
                Math.Max(_maxManagedBufferBytes, fieldMetrics.MaxOwnedBufferBytesObserved),
                MaxTransferOwnedNativeBufferBytesObserved: 0,
                fieldMetrics.MaxReadChunkBytesObserved,
                fieldMetrics.MaxWrittenChunkBytesObserved);
    }
}

internal sealed class TransferV3TableExportException : IOException
{
    private readonly List<string> _cleanupCodes = [];

    internal TransferV3TableExportException(string code)
        : base($"Transfer-v3 SQLite table export failed ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }

    internal ImmutableArray<string> CleanupCodes => [.. _cleanupCodes];

    internal void RecordCleanupFailure(string code)
    {
        if (!_cleanupCodes.Contains(code, StringComparer.Ordinal))
            _cleanupCodes.Add(code);
    }
}

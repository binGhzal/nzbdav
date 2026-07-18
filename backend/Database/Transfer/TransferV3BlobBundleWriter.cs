using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3BlobBundleFaultPoint
{
    AfterFirstContentChunk,
}

internal sealed record TransferV3BlobBundleWriterHooks(
    Action<TransferV3BlobBundleFaultPoint>? AfterFaultPoint = null);

internal sealed record TransferV3BlobBundleMetrics(
    long Rows,
    long ContentBytes,
    long SourceReadOperations,
    int MaxSourceBufferBytesObserved,
    int MaxDecodedChunkBytesObserved);

internal sealed record TransferV3BlobBundleExportResult(
    TransferV3ManifestBlobs Blobs,
    TransferV3BlobBundleMetrics Metrics);

internal sealed class TransferV3BlobBundleWriter
{
    private const string TableName = "Blobs";
    private const string FileName = "Blobs.jsonl";
    private const int DescriptorBytes = sizeof(long) + 32;
    private const int MaximumFieldCount = 1024;
    private readonly TransferV3BlobBundleWriterHooks? _hooks;

    internal TransferV3BlobBundleWriter(TransferV3BlobBundleWriterHooks? hooks = null)
    {
        _hooks = hooks;
    }

    internal async Task<TransferV3BlobBundleExportResult> ExportAsync(
        TransferV3SqliteExportContext context,
        TransferV3Limits limits,
        ITransferV3TableOutputFactory outputFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(outputFactory);
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.MaxFieldBytes < DescriptorBytes)
        {
            throw Failure("field-size");
        }

        ITransferV3TableOutput? output = null;
        TransferV3JsonlWriter? writer = null;
        TransferV3BlobBundleExportResult? result = null;
        Exception? primary = null;
        var cleanupCodes = new List<string>();
        try
        {
            output = await outputFactory.CreateAsync(FileName, cancellationToken)
                .ConfigureAwait(false);
            if (output is null)
            {
                throw Failure("output-shape");
            }

            Stream stream;
            try
            {
                stream = output.Stream;
            }
            catch
            {
                throw Failure("output-shape");
            }
            if (stream is null || !stream.CanWrite)
            {
                throw Failure("output-shape");
            }

            writer = new TransferV3JsonlWriter(stream, TableName, limits);
            result = await WriteBundleAsync(context, limits, writer, cancellationToken)
                .ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;

            try
            {
                await output.CompleteDurablyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            primary = exception;
        }
        catch (TransferV3BlobBundleExportException exception)
        {
            primary = exception;
        }
        catch
        {
            primary = Failure("export");
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
                    TransferV3Posix.AddCleanupCode(cleanupCodes, "writer-dispose-failed");
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
                    TransferV3Posix.AddCleanupCode(cleanupCodes, "output-dispose-failed");
                }
            }
        }

        if (primary is not null)
        {
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
        }
        if (cleanupCodes.Count > 0)
        {
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(Failure("cleanup"), cleanupCodes);
        }
        return result ?? throw Failure("result");
    }

    private async Task<TransferV3BlobBundleExportResult> WriteBundleAsync(
        TransferV3SqliteExportContext context,
        TransferV3Limits limits,
        TransferV3JsonlWriter writer,
        CancellationToken cancellationToken)
    {
        var blobSource = context.BlobSource;
        blobSource.VerifyUnchanged();
        await writer.WriteTableHeaderAsync(cancellationToken).ConfigureAwait(false);

        using var inventoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[sizeof(long)];
        byte[]? sourceBuffer = null;
        var metrics = new MutableMetrics();
        long rows = 0;
        long totalBytes = 0;
        var batchOpen = false;
        var batchIndex = 0;
        var batchRows = 0;
        long batchBytes = 0;
        string? previousBatchCursor = null;

        try
        {
            await using var command = context.Connection.CreateCommand();
            command.Transaction = context.Transaction;
            command.CommandText =
                "SELECT b.normalized_uuid, b.first_name, b.second_name, "
                + "b.length_bytes, b.content_sha256, b.file_fingerprint, "
                + "f.fingerprint, s.fingerprint "
                + "FROM scratch.blob_inventory AS b "
                + "JOIN scratch.blob_first_shards AS f ON f.first_name = b.first_name "
                + "JOIN scratch.blob_second_shards AS s "
                + "ON s.first_name = b.first_name AND s.second_name = b.second_name "
                + "ORDER BY b.normalized_uuid;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadInventoryRow(reader);
                var contentFields = CountContentFields(row.Length, limits.MaxFieldBytes);
                if (contentFields > MaximumFieldCount - 1)
                {
                    throw Failure("field-count");
                }

                long rowBytes;
                try
                {
                    rowBytes = checked(DescriptorBytes + row.Length);
                }
                catch (OverflowException)
                {
                    throw Failure("row-bytes");
                }

                if (batchOpen
                    && (batchRows >= limits.MaxBatchRows
                        || ExceedsBatchBudget(batchBytes, rowBytes, limits.MaxBatchBytes)))
                {
                    var batchEnd = await writer.EndBatchAsync(cancellationToken)
                        .ConfigureAwait(false);
                    previousBatchCursor = batchEnd.Cursor;
                    batchOpen = false;
                    batchRows = 0;
                    batchBytes = 0;
                    batchIndex = checked(batchIndex + 1);
                }

                if (!batchOpen)
                {
                    await writer.StartBatchAsync(
                            batchIndex,
                            previousBatchCursor,
                            cancellationToken)
                        .ConfigureAwait(false);
                    batchOpen = true;
                }

                var cursor = TransferV3CursorCodec.Encode(
                    TransferV3CursorComponent.FromGuid(row.Id));
                await using var opened = OpenRetainedBlob(blobSource, row);
                await writer.StartChunkedRowAsync(
                        cursor,
                        checked((int)(1 + contentFields)),
                        cancellationToken)
                    .ConfigureAwait(false);

                var descriptor = new byte[DescriptorBytes];
                try
                {
                    BinaryPrimitives.WriteInt64BigEndian(descriptor, row.Length);
                    row.Digest.CopyTo(descriptor, sizeof(long));
                    metrics.ObserveDecodedChunk(descriptor.Length);
                    await writer.WriteFieldChunkAsync(0, descriptor, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(descriptor);
                }

                byte[] actualDigest;
                if (row.Length == 0)
                {
                    await writer.WriteFieldChunkAsync(
                            1,
                            ReadOnlyMemory<byte>.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                    metrics.ObserveDecodedChunk(0);
                    if (opened.Stream.ReadByte() != -1)
                    {
                        throw Failure("source-mutated");
                    }
                    actualDigest = SHA256.HashData(ReadOnlySpan<byte>.Empty);
                }
                else
                {
                    var requestedBufferBytes = checked((int)Math.Min(
                        TransferV3Limits.MaxDecodedChunkBytes,
                        Math.Min(row.Length, limits.MaxFieldBytes)));
                    if (sourceBuffer is null || sourceBuffer.Length < requestedBufferBytes)
                    {
                        if (sourceBuffer is not null)
                        {
                            CryptographicOperations.ZeroMemory(sourceBuffer);
                        }
                        sourceBuffer = GC.AllocateUninitializedArray<byte>(requestedBufferBytes);
                        metrics.ObserveSourceBuffer(sourceBuffer.Length);
                    }

                    actualDigest = await WriteContentAsync(
                            opened.Stream,
                            writer,
                            row.Length,
                            limits.MaxFieldBytes,
                            sourceBuffer,
                            metrics,
                            cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (opened.Stream.ReadByte() != -1)
                    {
                        throw Failure("source-mutated");
                    }
                }

                opened.VerifyUnchanged();
                if (!CryptographicOperations.FixedTimeEquals(actualDigest, row.Digest))
                {
                    throw Failure("source-mutated");
                }

                await writer.EndChunkedRowAsync(cancellationToken).ConfigureAwait(false);
                inventoryHash.AppendData(row.NetworkId);
                BinaryPrimitives.WriteInt64BigEndian(lengthBytes, row.Length);
                inventoryHash.AppendData(lengthBytes);
                inventoryHash.AppendData(actualDigest);
                rows = checked(rows + 1);
                totalBytes = checked(totalBytes + row.Length);
                batchRows = checked(batchRows + 1);
                batchBytes = checked(batchBytes + rowBytes);
                metrics.ObserveRow(row.Length);
            }

            if (batchOpen)
            {
                _ = await writer.EndBatchAsync(cancellationToken).ConfigureAwait(false);
            }

            var tableEnd = await writer.EndTableAsync(cancellationToken).ConfigureAwait(false);
            blobSource.VerifyUnchanged();
            var inventoryDigest = inventoryHash.GetHashAndReset();
            var inventorySha256 = Convert.ToHexString(inventoryDigest).ToLowerInvariant();
            var expected = context.Validation.Blobs;
            if (rows != expected.Count
                || totalBytes != expected.TotalBytes
                || !string.Equals(inventorySha256, expected.Sha256, StringComparison.Ordinal)
                || tableEnd.Rows != rows
                || tableEnd.Bytes != checked(DescriptorBytes * rows + totalBytes))
            {
                throw Failure("inventory-mismatch");
            }

            return new TransferV3BlobBundleExportResult(
                new TransferV3ManifestBlobs(
                    TableName,
                    FileName,
                    tableEnd.Batches,
                    tableEnd.Rows,
                    tableEnd.Bytes,
                    tableEnd.Sha256,
                    rows,
                    totalBytes,
                    inventorySha256),
                metrics.Snapshot());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lengthBytes);
            if (sourceBuffer is not null)
            {
                CryptographicOperations.ZeroMemory(sourceBuffer);
            }
        }
    }

    private async Task<byte[]> WriteContentAsync(
        Stream source,
        TransferV3JsonlWriter writer,
        long length,
        long maxFieldBytes,
        byte[] buffer,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = length;
        var field = 1;
        var firstChunk = true;
        while (remaining > 0)
        {
            var fieldRemaining = Math.Min(maxFieldBytes, remaining);
            while (fieldRemaining > 0)
            {
                var chunkBytes = checked((int)Math.Min(
                    TransferV3Limits.MaxDecodedChunkBytes,
                    fieldRemaining));
                await ReadExactlyAsync(
                        source,
                        buffer.AsMemory(0, chunkBytes),
                        metrics,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    contentHash.AppendData(buffer, 0, chunkBytes);
                    metrics.ObserveDecodedChunk(chunkBytes);
                    await writer.WriteFieldChunkAsync(
                            field,
                            buffer.AsMemory(0, chunkBytes),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer.AsSpan(0, chunkBytes));
                }

                if (firstChunk)
                {
                    firstChunk = false;
                    _hooks?.AfterFaultPoint?.Invoke(
                        TransferV3BlobBundleFaultPoint.AfterFirstContentChunk);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                fieldRemaining -= chunkBytes;
                remaining -= chunkBytes;
            }
            field++;
        }
        return contentHash.GetHashAndReset();
    }

    private static async Task ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        MutableMetrics metrics,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        try
        {
            while (filled < destination.Length)
            {
                var read = await source.ReadAsync(destination[filled..], cancellationToken)
                    .ConfigureAwait(false);
                metrics.ObserveSourceRead(read);
                if (read == 0)
                {
                    throw Failure("source-mutated");
                }
                filled += read;
            }
        }
        catch
        {
            if (filled > 0)
            {
                CryptographicOperations.ZeroMemory(destination.Span[..filled]);
            }
            throw;
        }
    }

    private static RetainedBlob OpenRetainedBlob(
        TransferV3BlobSourceGuard source,
        InventoryRow row)
    {
        try
        {
            var first = TransferV3Posix.OpenDirectoryAt(source.RootHandle, row.FirstName);
            try
            {
                RequireFingerprint(first, row.FirstFingerprint);
                var second = TransferV3Posix.OpenDirectoryAt(first, row.SecondName);
                try
                {
                    RequireFingerprint(second, row.SecondFingerprint);
                    var file = TransferV3Posix.OpenReadOnlyRegularFileAt(
                        second,
                        row.Id.ToString("D"));
                    try
                    {
                        RequireFingerprint(file, row.FileFingerprint);
                        if (TransferV3Posix.GetFingerprint(file).Size != row.Length)
                        {
                            throw Failure("source-mutated");
                        }
                        return new RetainedBlob(first, second, file, row);
                    }
                    catch
                    {
                        file.Dispose();
                        throw;
                    }
                }
                catch
                {
                    second.Dispose();
                    throw;
                }
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }
        catch (TransferV3BlobBundleExportException)
        {
            throw;
        }
        catch
        {
            throw Failure("source-mutated");
        }
    }

    private static InventoryRow ReadInventoryRow(SqliteDataReader reader)
    {
        try
        {
            var networkId = ReadFixedBlob(reader, 0, 16);
            var firstBytes = ReadFixedBlob(reader, 1, 2);
            var secondBytes = ReadFixedBlob(reader, 2, 2);
            var length = reader.GetInt64(3);
            var digest = ReadFixedBlob(reader, 4, 32);
            var fileFingerprint = ReadFixedBlob(reader, 5, 56);
            var firstFingerprint = ReadFixedBlob(reader, 6, 56);
            var secondFingerprint = ReadFixedBlob(reader, 7, 56);
            if (length < 0 || !IsLowerHexPair(firstBytes) || !IsLowerHexPair(secondBytes))
            {
                throw Failure("inventory-shape");
            }

            var id = new Guid(networkId, bigEndian: true);
            var normalized = id.ToString("N");
            var firstName = Encoding.ASCII.GetString(firstBytes);
            var secondName = Encoding.ASCII.GetString(secondBytes);
            if (!string.Equals(firstName, normalized[..2], StringComparison.Ordinal)
                || !string.Equals(secondName, normalized.Substring(2, 2), StringComparison.Ordinal))
            {
                throw Failure("inventory-shape");
            }

            return new InventoryRow(
                id,
                networkId,
                firstName,
                secondName,
                length,
                digest,
                fileFingerprint,
                firstFingerprint,
                secondFingerprint);
        }
        catch (TransferV3BlobBundleExportException)
        {
            throw;
        }
        catch
        {
            throw Failure("inventory-shape");
        }
    }

    private static byte[] ReadFixedBlob(SqliteDataReader reader, int ordinal, int bytes)
    {
        var value = reader.GetFieldValue<byte[]>(ordinal);
        if (value.Length != bytes)
        {
            throw Failure("inventory-shape");
        }
        return value;
    }

    private static bool IsLowerHexPair(ReadOnlySpan<byte> value) =>
        value.Length == 2
        && value[0] is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'
        && value[1] is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f';

    private static long CountContentFields(long length, long maxFieldBytes)
    {
        if (length == 0) return 1;
        var complete = length / maxFieldBytes;
        return length % maxFieldBytes == 0 ? complete : checked(complete + 1);
    }

    private static bool ExceedsBatchBudget(long current, long row, long maximum) =>
        row > maximum || current > maximum - row;

    private static void RequireFingerprint(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        byte[] expected)
    {
        var actual = TransferV3Posix.EncodeFingerprint(
            TransferV3Posix.GetFingerprint(handle));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw Failure("source-mutated");
        }
    }

    private static TransferV3BlobBundleExportException Failure(string code) => new(code);

    private sealed record InventoryRow(
        Guid Id,
        byte[] NetworkId,
        string FirstName,
        string SecondName,
        long Length,
        byte[] Digest,
        byte[] FileFingerprint,
        byte[] FirstFingerprint,
        byte[] SecondFingerprint);

    private sealed class RetainedBlob : IAsyncDisposable
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _first;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _second;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file;
        private readonly InventoryRow _row;

        internal RetainedBlob(
            Microsoft.Win32.SafeHandles.SafeFileHandle first,
            Microsoft.Win32.SafeHandles.SafeFileHandle second,
            Microsoft.Win32.SafeHandles.SafeFileHandle file,
            InventoryRow row)
        {
            _first = first;
            _second = second;
            _file = file;
            _row = row;
            Stream = new FileStream(file, FileAccess.Read, bufferSize: 1, isAsync: false);
        }

        internal FileStream Stream { get; }

        internal void VerifyUnchanged()
        {
            RequireFingerprint(Stream.SafeFileHandle, _row.FileFingerprint);
            RequireFingerprint(_second, _row.SecondFingerprint);
            RequireFingerprint(_first, _row.FirstFingerprint);
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
            _file.Dispose();
            _second.Dispose();
            _first.Dispose();
        }
    }

    private sealed class MutableMetrics
    {
        private long _rows;
        private long _contentBytes;
        private long _sourceReadOperations;
        private int _maxSourceBufferBytes;
        private int _maxDecodedChunkBytes;

        internal void ObserveSourceBuffer(int bytes) =>
            _maxSourceBufferBytes = Math.Max(_maxSourceBufferBytes, bytes);

        internal void ObserveDecodedChunk(int bytes) =>
            _maxDecodedChunkBytes = Math.Max(_maxDecodedChunkBytes, bytes);

        internal void ObserveSourceRead(int bytes)
        {
            _ = bytes;
            _sourceReadOperations = checked(_sourceReadOperations + 1);
        }

        internal void ObserveRow(long contentBytes)
        {
            _rows = checked(_rows + 1);
            _contentBytes = checked(_contentBytes + contentBytes);
        }

        internal TransferV3BlobBundleMetrics Snapshot() => new(
            _rows,
            _contentBytes,
            _sourceReadOperations,
            _maxSourceBufferBytes,
            _maxDecodedChunkBytes);
    }
}

internal sealed class TransferV3BlobBundleExportException : IOException
{
    internal TransferV3BlobBundleExportException(string code)
        : base($"Transfer-v3 blob bundle export failed ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }

    internal ImmutableArray<string> CleanupCodes =>
        Data["TransferV3CleanupCodes"] is IEnumerable<string> codes
            ? [.. codes]
            : [];
}

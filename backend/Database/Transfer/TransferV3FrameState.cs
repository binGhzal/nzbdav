using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3FrameState : IDisposable
{
    private const int MaxFieldCount = 1024;
    private static readonly byte[] LineFeed = [(byte)'\n'];

    private readonly TransferV3Limits _limits;
    private readonly Action<string, byte[]>? _observeFinalizedDigest;
    private readonly IncrementalHash _tableCanonicalHash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _table;
    private IncrementalHash? _batchCanonicalHash;
    private IncrementalHash? _rowHash;
    private bool _headerSeen;
    private bool _batchOpen;
    private bool _rowOpen;
    private bool _completed;
    private bool _disposed;
    private int _completedBatches;
    private long _totalRows;
    private long _totalBytes;
    private int _batchRows;
    private long _batchBytes;
    private bool _batchOverBudget;
    private string? _lastCursor;
    private string? _rowCursor;
    private int _rowFieldCount;
    private int _currentField = -1;
    private int _currentChunk = -1;
    private int _previousChunkBytes;
    private long _currentFieldBytes;
    private long _rowBytes;

    internal TransferV3FrameState(
        TransferV3Limits limits,
        string? expectedTable = null,
        Action<string, byte[]>? observeFinalizedDigest = null)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _observeFinalizedDigest = observeFinalizedDigest;
        if (expectedTable is not null)
        {
            TransferV3FrameCodec.ValidateTableName(expectedTable);
        }

        _table = expectedTable;
    }

    internal bool Completed => _completed;

    internal void AcceptHeader(TransferV3TableHeaderFrame frame)
    {
        EnsureUsable();
        if (_headerSeen || frame.Version != TransferV3FrameCodec.FormatVersion)
        {
            throw InvalidSequence("The table header is duplicate or has the wrong version.");
        }

        TransferV3FrameCodec.ValidateTableName(frame.Table);
        if (_table is not null && !string.Equals(_table, frame.Table, StringComparison.Ordinal))
        {
            throw InvalidSequence("The table header does not match the expected table.");
        }

        _table = frame.Table;
        _headerSeen = true;
    }

    internal void AcceptBatchStart(TransferV3BatchStartFrame frame)
    {
        EnsureBetweenBatches(frame.Table);
        if (frame.Batch != _completedBatches)
        {
            throw InvalidSequence("Batch indexes must be contiguous from zero.");
        }

        var expectedAfter = _completedBatches == 0 ? null : _lastCursor;
        if (!string.Equals(frame.After, expectedAfter, StringComparison.Ordinal))
        {
            throw InvalidSequence("The batch cursor does not continue the previous batch.");
        }

        if (frame.After is not null)
        {
            ValidateCursor(frame.After);
        }

        _batchCanonicalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _batchRows = 0;
        _batchBytes = 0;
        _batchOverBudget = false;
        _batchOpen = true;
    }

    internal void AcceptRow(TransferV3RowFrame frame)
    {
        EnsureInsideBatch(frame.Table);
        if (_rowOpen)
        {
            throw InvalidSequence("A normal row cannot appear inside a chunked row.");
        }

        ValidateNextCursor(frame.Cursor);
        if (frame.Data.Length > TransferV3Limits.MaxDecodedChunkBytes)
        {
            throw InvalidSequence("An inline row exceeds the one-MiB frame limit.");
        }

        if (frame.Data.Length > _limits.MaxBatchBytes
            || _batchRows >= _limits.MaxBatchRows
            || _batchOverBudget
            || checked(_batchBytes + frame.Data.Length) > _limits.MaxBatchBytes)
        {
            throw InvalidSequence("The inline row exceeds its batch row or byte budget.");
        }

        AddCompletedRow(frame.Cursor, frame.Data.Length, allowOversizedSingleton: false);
    }

    internal void AcceptChunkedRowStart(TransferV3ChunkedRowStartFrame frame)
    {
        EnsureInsideBatch(frame.Table);
        if (_rowOpen
            || _batchRows >= _limits.MaxBatchRows
            || _batchOverBudget
            || frame.Fields is <= 0 or > MaxFieldCount)
        {
            throw InvalidSequence("The chunked row start violates its sequence or row budget.");
        }

        ValidateNextCursor(frame.Cursor);
        _rowOpen = true;
        _rowCursor = frame.Cursor;
        _rowFieldCount = frame.Fields;
        _currentField = -1;
        _currentChunk = -1;
        _previousChunkBytes = 0;
        _currentFieldBytes = 0;
        _rowBytes = 0;
        _rowHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    internal void AcceptFieldChunk(TransferV3FieldChunkFrame frame)
    {
        EnsureInsideBatch(frame.Table);
        if (!_rowOpen || _rowHash is null || frame.Cursor != _rowCursor)
        {
            throw InvalidSequence("The field chunk has no matching open row.");
        }

        if (frame.Data.Length > TransferV3Limits.MaxDecodedChunkBytes)
        {
            throw InvalidSequence("A decoded field chunk exceeds one MiB.");
        }

        if (frame.Data.Length == 0 && frame.Chunk != 0)
        {
            throw InvalidSequence("Only chunk zero may represent an empty field.");
        }

        long nextFieldBytes;
        if (_currentField == -1)
        {
            if (frame.Field != 0 || frame.Chunk != 0)
            {
                throw InvalidSequence("Field chunks must begin at field and chunk zero.");
            }

            nextFieldBytes = frame.Data.Length;
        }
        else if (frame.Field == _currentField)
        {
            if (frame.Chunk != _currentChunk + 1
                || _previousChunkBytes != TransferV3Limits.MaxDecodedChunkBytes)
            {
                throw InvalidSequence("Chunks must be contiguous and every non-final chunk must be one MiB.");
            }

            nextFieldBytes = checked(_currentFieldBytes + frame.Data.Length);
        }
        else
        {
            if (frame.Field != _currentField + 1
                || frame.Field >= _rowFieldCount
                || frame.Chunk != 0)
            {
                throw InvalidSequence("Fields and their chunks must be contiguous.");
            }

            nextFieldBytes = frame.Data.Length;
        }

        if (nextFieldBytes > _limits.MaxFieldBytes)
        {
            throw InvalidSequence("A field exceeds the configured maximum field bytes.");
        }

        var nextRowBytes = checked(_rowBytes + frame.Data.Length);
        if (_batchRows > 0 && checked(_batchBytes + nextRowBytes) > _limits.MaxBatchBytes)
        {
            throw InvalidSequence("Only a singleton chunked row may exceed the batch byte budget.");
        }

        Span<byte> chunkHeader = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(chunkHeader, frame.Field);
        BinaryPrimitives.WriteInt32BigEndian(chunkHeader[4..], frame.Chunk);
        BinaryPrimitives.WriteInt32BigEndian(chunkHeader[8..], frame.Data.Length);
        _rowHash.AppendData(chunkHeader);
        _rowHash.AppendData(frame.Data.Span);

        if (frame.Field != _currentField)
        {
            _currentFieldBytes = 0;
        }

        _currentField = frame.Field;
        _currentChunk = frame.Chunk;
        _previousChunkBytes = frame.Data.Length;
        _currentFieldBytes = nextFieldBytes;
        _rowBytes = nextRowBytes;
    }

    internal TransferV3ChunkedRowEndFrame FinishChunkedRow()
    {
        EnsureUsable();
        if (!_batchOpen
            || !_rowOpen
            || _rowHash is null
            || _table is null
            || _rowCursor is null
            || _currentField != _rowFieldCount - 1)
        {
            throw InvalidSequence("The chunked row is incomplete.");
        }

        var frame = new TransferV3ChunkedRowEndFrame(
            _table,
            _rowCursor,
            _rowFieldCount,
            _rowBytes,
            FinalizeDigest(_rowHash, "row"));
        _rowHash.Dispose();
        _rowHash = null;
        _rowOpen = false;
        AddCompletedRow(_rowCursor, _rowBytes, allowOversizedSingleton: true);
        _rowCursor = null;
        return frame;
    }

    internal TransferV3BatchEndFrame FinishBatch()
    {
        EnsureUsable();
        if (!_batchOpen
            || _rowOpen
            || _batchCanonicalHash is null
            || _table is null
            || _batchRows == 0
            || _lastCursor is null)
        {
            throw InvalidSequence("The batch is empty or incomplete.");
        }

        var frame = new TransferV3BatchEndFrame(
            _table,
            _completedBatches,
            _batchRows,
            _batchBytes,
            _lastCursor,
            FinalizeDigest(_batchCanonicalHash, "batch"));
        _batchCanonicalHash.Dispose();
        _batchCanonicalHash = null;
        _batchOpen = false;
        _completedBatches++;
        _totalRows = checked(_totalRows + _batchRows);
        _totalBytes = checked(_totalBytes + _batchBytes);
        return frame;
    }

    internal TransferV3TableEndFrame FinishTable()
    {
        EnsureUsable();
        if (!_headerSeen || _batchOpen || _rowOpen || _table is null || _completed)
        {
            throw InvalidSequence("The table cannot end in its current state.");
        }

        _completed = true;
        return new TransferV3TableEndFrame(
            _table,
            _completedBatches,
            _totalRows,
            _totalBytes,
            FinalizeDigest(_tableCanonicalHash, "table"));
    }

    internal void AcceptParsed(TransferV3Frame frame, ReadOnlySpan<byte> canonicalLine)
    {
        switch (frame)
        {
            case TransferV3TableHeaderFrame header:
                AcceptHeader(header);
                break;
            case TransferV3BatchStartFrame batchStart:
                AcceptBatchStart(batchStart);
                break;
            case TransferV3RowFrame row:
                AcceptRow(row);
                break;
            case TransferV3ChunkedRowStartFrame rowStart:
                AcceptChunkedRowStart(rowStart);
                break;
            case TransferV3FieldChunkFrame chunk:
                AcceptFieldChunk(chunk);
                break;
            case TransferV3ChunkedRowEndFrame rowEnd:
                RequireEqual(FinishChunkedRow(), rowEnd);
                break;
            case TransferV3BatchEndFrame batchEnd:
                RequireEqual(FinishBatch(), batchEnd);
                break;
            case TransferV3TableEndFrame tableEnd:
                RequireEqual(FinishTable(), tableEnd);
                break;
            default:
                throw InvalidSequence("The frame type is unknown.");
        }

        RecordCanonicalLine(frame, canonicalLine);
    }

    // Batch hashes cover batch-start plus every data frame, all with their LF,
    // and deliberately exclude batch-end. Table hashes cover the header, those
    // same batch/data frames, and each verified batch-end line; table-end is
    // excluded to avoid self-reference.
    internal void RecordCanonicalLine(
        TransferV3Frame frame,
        ReadOnlySpan<byte> canonicalLine)
    {
        EnsureUsable();
        switch (frame)
        {
            case TransferV3TableHeaderFrame:
                AppendCanonicalLine(_tableCanonicalHash, canonicalLine);
                break;
            case TransferV3BatchStartFrame:
            case TransferV3RowFrame:
            case TransferV3ChunkedRowStartFrame:
            case TransferV3FieldChunkFrame:
            case TransferV3ChunkedRowEndFrame:
                if (_batchCanonicalHash is null)
                {
                    throw InvalidSequence("A canonical batch/data line has no matching batch hash.");
                }

                AppendCanonicalLine(_batchCanonicalHash, canonicalLine);
                AppendCanonicalLine(_tableCanonicalHash, canonicalLine);
                break;
            case TransferV3BatchEndFrame:
                AppendCanonicalLine(_tableCanonicalHash, canonicalLine);
                break;
            case TransferV3TableEndFrame:
                break;
            default:
                throw InvalidSequence("The canonical frame type is unknown.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _rowHash?.Dispose();
        _batchCanonicalHash?.Dispose();
        _tableCanonicalHash.Dispose();
        _disposed = true;
    }

    private void AddCompletedRow(
        string cursor,
        long bytes,
        bool allowOversizedSingleton)
    {
        if (_batchCanonicalHash is null || _batchRows >= _limits.MaxBatchRows)
        {
            throw InvalidSequence("The batch row budget is exhausted.");
        }

        var nextBytes = checked(_batchBytes + bytes);
        if (nextBytes > _limits.MaxBatchBytes)
        {
            if (!allowOversizedSingleton || _batchRows != 0)
            {
                throw InvalidSequence("The batch byte budget is exhausted.");
            }

            _batchOverBudget = true;
        }

        _batchRows++;
        _batchBytes = nextBytes;
        _lastCursor = cursor;
    }

    private static void AppendCanonicalLine(
        IncrementalHash hash,
        ReadOnlySpan<byte> canonicalLine)
    {
        hash.AppendData(canonicalLine);
        hash.AppendData(LineFeed);
    }

    private void ValidateNextCursor(string cursor)
    {
        ValidateCursor(cursor);
        if (_lastCursor is null)
        {
            return;
        }

        try
        {
            if (TransferV3CursorCodec.Compare(_lastCursor, cursor) >= 0)
            {
                throw InvalidSequence("Row cursors must increase strictly.");
            }
        }
        catch (FormatException exception)
        {
            throw InvalidSequence("Row cursor shapes do not match.", exception);
        }
    }

    private static void ValidateCursor(string cursor)
    {
        try
        {
            _ = TransferV3CursorCodec.Decode(cursor);
        }
        catch (FormatException exception)
        {
            throw InvalidSequence("The row cursor is malformed.", exception);
        }
    }

    private void EnsureBetweenBatches(string table)
    {
        EnsureUsable();
        VerifyTable(table);
        if (!_headerSeen || _batchOpen || _rowOpen || _completed)
        {
            throw InvalidSequence("A batch cannot begin in the current state.");
        }
    }

    private void EnsureInsideBatch(string table)
    {
        EnsureUsable();
        VerifyTable(table);
        if (!_headerSeen || !_batchOpen || _completed)
        {
            throw InvalidSequence("The frame must be inside an open batch.");
        }
    }

    private void VerifyTable(string table)
    {
        if (_table is null || !string.Equals(_table, table, StringComparison.Ordinal))
        {
            throw InvalidSequence("The frame table does not match its header.");
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void RequireEqual<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw InvalidSequence("The frame count, cursor, byte count, or digest does not match.");
        }
    }

    private static string ToDigest(ReadOnlySpan<byte> digest) =>
        Convert.ToHexStringLower(digest);

    private string FinalizeDigest(IncrementalHash hash, string kind)
    {
        var digest = hash.GetHashAndReset();
        try
        {
            _observeFinalizedDigest?.Invoke(kind, digest);
            return ToDigest(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static InvalidOperationException InvalidSequence(
        string message,
        Exception? inner = null) => new(message, inner);
}

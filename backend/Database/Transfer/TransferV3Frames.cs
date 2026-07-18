namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3Limits
{
    internal const int MaxDecodedChunkBytes = 1024 * 1024;
    internal const long MaxAllowedFieldBytes = 16L * 1024 * 1024 * 1024;

    internal TransferV3Limits(
        long maxFieldBytes,
        int maxBatchRows = 1000,
        long maxBatchBytes = 16L * 1024 * 1024)
    {
        if (maxFieldBytes is <= 0 or > MaxAllowedFieldBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFieldBytes));
        }

        if (maxBatchRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchRows));
        }

        if (maxBatchBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchBytes));
        }

        MaxFieldBytes = maxFieldBytes;
        MaxBatchRows = maxBatchRows;
        MaxBatchBytes = maxBatchBytes;
    }

    internal long MaxFieldBytes { get; }
    internal int MaxBatchRows { get; }
    internal long MaxBatchBytes { get; }
    internal int MaxEncodedFrameBytes { get; } = checked(
        TransferV3CursorCodec.MaxEncodedChars
        + Base64UrlEncodedLength(MaxDecodedChunkBytes)
        + 4096);

    // This is an accounting bound for the encoded line, its canonical
    // re-encoding, and one decoded payload. It is deliberately not described as
    // a process-allocation ceiling: JSON metadata, buffer capacities, and runtime
    // overhead are outside this counter.
    internal long MaxAccountedBytesPerDispatch =>
        checked(2L * MaxEncodedFrameBytes + MaxDecodedChunkBytes);

    internal static int Base64UrlEncodedLength(int decodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
        var fullGroups = decodedBytes / 3;
        var remainder = decodedBytes % 3;
        return checked(fullGroups * 4 + (remainder == 0 ? 0 : remainder + 1));
    }
}

internal sealed record TransferV3BufferMetrics(
    long Frames,
    int MaxEncodedFrameBytesObserved,
    int MaxDecodedPayloadBytesObserved,
    int MaxFramesDispatchedConcurrently,
    long MaxAccountedBytesPerDispatch);

internal abstract record TransferV3Frame(string Table);
internal sealed record TransferV3TableHeaderFrame(int Version, string Table) : TransferV3Frame(Table);
internal sealed record TransferV3BatchStartFrame(string Table, int Batch, string? After) : TransferV3Frame(Table);
internal sealed record TransferV3RowFrame(string Table, string Cursor, ReadOnlyMemory<byte> Data) : TransferV3Frame(Table);
internal sealed record TransferV3ChunkedRowStartFrame(string Table, string Cursor, int Fields) : TransferV3Frame(Table);
internal sealed record TransferV3FieldChunkFrame(string Table, string Cursor, int Field, int Chunk, ReadOnlyMemory<byte> Data) : TransferV3Frame(Table);
internal sealed record TransferV3ChunkedRowEndFrame(string Table, string Cursor, int Fields, long Bytes, string Sha256) : TransferV3Frame(Table);
internal sealed record TransferV3BatchEndFrame(string Table, int Batch, int Rows, long Bytes, string Cursor, string Sha256) : TransferV3Frame(Table);
internal sealed record TransferV3TableEndFrame(string Table, int Batches, long Rows, long Bytes, string Sha256) : TransferV3Frame(Table);

internal interface ITransferV3FrameObserver
{
    // Observe stages at most the current bounded batch. A batch must not become
    // externally visible before CommitBatch is called with its already verified
    // batch-end frame. Earlier committed batches remain published if a later
    // batch or table finalization fails.
    void Observe(TransferV3Frame frame);

    void CommitBatch(TransferV3BatchEndFrame batchEnd);

    // CompleteTable is called only after table-end counts/digest verify and the
    // parser confirms physical EOF. Implementations must make this transition
    // atomic with respect to a thrown failure.
    void CompleteTable(TransferV3TableEndFrame tableEnd);

    // Abort rolls back any current staged batch and reports the table failure;
    // it must not undo earlier batches that CommitBatch already published.
    void Abort(Exception failure);
}

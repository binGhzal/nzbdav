using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3JsonlWriter : IAsyncDisposable
{
    private static readonly byte[] LineFeed = [(byte)'\n'];
    private readonly Stream _destination;
    private readonly string _table;
    private readonly TransferV3Limits _limits;
    private readonly TransferV3FrameState _state;
    private readonly Action<int>? _observeManagedBuffer;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private string? _openRowCursor;
    private int _nextChunkIndex;
    private int _currentField = -1;
    private bool _faulted;
    private bool _completed;
    private bool _disposed;

    internal TransferV3JsonlWriter(
        Stream destination,
        string table,
        TransferV3Limits limits,
        Action<int>? observeManagedBuffer = null)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The JSONL destination must be writable.", nameof(destination));
        }

        TransferV3FrameCodec.ValidateTableName(table);
        _table = table;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _observeManagedBuffer = observeManagedBuffer;
        _state = new TransferV3FrameState(limits, table);
    }

    internal ValueTask WriteTableHeaderAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            var frame = new TransferV3TableHeaderFrame(TransferV3FrameCodec.FormatVersion, _table);
            _state.AcceptHeader(frame);
            return frame;
        }, cancellationToken);
    }

    internal ValueTask StartBatchAsync(
        int batch,
        string? after,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            var frame = new TransferV3BatchStartFrame(_table, batch, after);
            _state.AcceptBatchStart(frame);
            return frame;
        }, cancellationToken);
    }

    internal ValueTask WriteRowAsync(
        string cursor,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            var frame = new TransferV3RowFrame(_table, cursor, data);
            _state.AcceptRow(frame);
            return frame;
        }, cancellationToken);
    }

    internal ValueTask StartChunkedRowAsync(
        string cursor,
        int fieldCount,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            var frame = new TransferV3ChunkedRowStartFrame(_table, cursor, fieldCount);
            _state.AcceptChunkedRowStart(frame);
            _openRowCursor = cursor;
            _currentField = -1;
            _nextChunkIndex = 0;
            return frame;
        }, cancellationToken);
    }

    internal ValueTask WriteFieldChunkAsync(
        int fieldIndex,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            if (_openRowCursor is null)
            {
                throw new InvalidOperationException("No chunked row is open.");
            }

            var chunkIndex = fieldIndex == _currentField ? _nextChunkIndex : 0;
            var frame = new TransferV3FieldChunkFrame(
                _table,
                _openRowCursor,
                fieldIndex,
                chunkIndex,
                data);
            _state.AcceptFieldChunk(frame);
            _currentField = fieldIndex;
            _nextChunkIndex = chunkIndex + 1;
            return frame;
        }, cancellationToken);
    }

    internal ValueTask EndChunkedRowAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            var frame = _state.FinishChunkedRow();
            _openRowCursor = null;
            return frame;
        }, cancellationToken);
    }

    internal ValueTask<TransferV3BatchEndFrame> EndBatchAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteReturningAsync(_state.FinishBatch, cancellationToken);

    internal ValueTask<TransferV3TableEndFrame> EndTableAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteReturningAsync(_state.FinishTable, cancellationToken);

    // State is intentionally advanced before a frame is written. Any validation,
    // cancellation, serialization, or destination failure therefore faults the
    // writer permanently; continuing could otherwise publish frames for state
    // that was only partially written.
    private async ValueTask ExecuteAsync(
        Func<TransferV3Frame> createFrame,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteReturningAsync(createFrame, cancellationToken);
    }

    private async ValueTask<TFrame> ExecuteReturningAsync<TFrame>(
        Func<TFrame> createFrame,
        CancellationToken cancellationToken)
        where TFrame : TransferV3Frame
    {
        var acquired = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            acquired = true;
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var frame = createFrame();
            var serialized = TransferV3FrameCodec.SerializeMeasured(frame);
            var bytes = serialized.Bytes;
            try
            {
                _observeManagedBuffer?.Invoke(serialized.Metrics.Base64Utf16Bytes);
                _observeManagedBuffer?.Invoke(
                    serialized.Metrics.ArrayBufferWriterCapacityBytes);
                _observeManagedBuffer?.Invoke(serialized.Metrics.SerializedBytes);
                if (bytes.Length > _limits.MaxEncodedFrameBytes)
                {
                    throw new InvalidOperationException(
                        "The encoded JSONL frame exceeds its fixed limit.");
                }

                _state.RecordCanonicalLine(frame, bytes);
                await WriteFrameAsync(bytes, cancellationToken);
                if (frame is TransferV3TableEndFrame)
                {
                    _completed = true;
                    _state.Dispose();
                }

                return frame;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception primary)
        {
            if (!acquired)
            {
                await _operationGate.WaitAsync(CancellationToken.None);
                acquired = true;
            }

            Exception? cleanupFailure = null;
            if (!_disposed && !_completed && !_faulted)
            {
                _faulted = true;
                try
                {
                    _state.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "The JSONL writer failed and its hash-state cleanup also failed.",
                    primary,
                    cleanupFailure);
            }

            throw;
        }
        finally
        {
            if (acquired)
            {
                _operationGate.Release();
            }
        }
    }

    private async ValueTask WriteFrameAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await _destination.WriteAsync(bytes, cancellationToken);
        await _destination.WriteAsync(LineFeed, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The JSONL writer is terminally faulted.");
        }

        if (_completed)
        {
            throw new InvalidOperationException("The JSONL table is already complete.");
        }
    }
}

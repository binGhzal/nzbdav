using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3Utf8LineReader : IDisposable
{
    private readonly Stream _source;
    private readonly int _maxLineBytes;
    private readonly ArrayBufferWriter<byte> _lineBuffer;
    private readonly byte[] _readBuffer = new byte[16 * 1024];
    private int _readOffset;
    private int _readCount;
    private bool _endOfStream;
    private bool _disposed;

    internal TransferV3Utf8LineReader(Stream source, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        _source = source;
        _maxLineBytes = maxLineBytes;
        // A fixed-capacity writer prevents a growth operation from abandoning
        // an uncleared backing array containing an earlier part of the line.
        _lineBuffer = new ArrayBufferWriter<byte>(maxLineBytes);
    }

    internal async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            while (true)
            {
                if (_readOffset == _readCount)
                {
                    if (_endOfStream)
                    {
                        if (_lineBuffer.WrittenCount == 0)
                        {
                            return null;
                        }

                        throw new FormatException("Every JSONL frame must end with LF.");
                    }

                    _readCount = await _source.ReadAsync(_readBuffer, cancellationToken);
                    _readOffset = 0;
                    if (_readCount == 0)
                    {
                        _endOfStream = true;
                        continue;
                    }
                }

                var available = _readBuffer.AsSpan(_readOffset, _readCount - _readOffset);
                var lineFeed = available.IndexOf((byte)'\n');
                var bytesToCopy = lineFeed >= 0 ? lineFeed : available.Length;
                if (_lineBuffer.WrittenCount > _maxLineBytes - bytesToCopy)
                {
                    throw new FormatException("A JSONL frame exceeds the fixed encoded-frame limit.");
                }

                if (bytesToCopy > 0)
                {
                    var consumed = available[..bytesToCopy];
                    consumed.CopyTo(_lineBuffer.GetSpan(bytesToCopy));
                    _lineBuffer.Advance(bytesToCopy);
                    CryptographicOperations.ZeroMemory(consumed);
                }

                _readOffset += bytesToCopy;
                if (lineFeed < 0)
                {
                    continue;
                }

                _readBuffer[_readOffset] = 0;
                _readOffset++; // Consume LF.
                return _lineBuffer.WrittenSpan.ToArray();
            }
        }
        finally
        {
            ClearWriter(_lineBuffer);
            _lineBuffer.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_readBuffer);
        ClearWriter(_lineBuffer, clearCapacity: true);
        _lineBuffer.Clear();
        _readOffset = 0;
        _readCount = 0;
        _endOfStream = true;
        _disposed = true;
    }

    private static void ClearWriter(
        ArrayBufferWriter<byte> writer,
        bool clearCapacity = false)
    {
        if (MemoryMarshal.TryGetArray(writer.WrittenMemory, out var segment)
            && segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(
                clearCapacity ? segment.Array.AsSpan() : segment.AsSpan());
        }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3LogicalType : byte
{
    Uuid = 0x01,
    Int32 = 0x02,
    Int64 = 0x03,
    Boolean = 0x04,
    Text = 0x05,
    LocalWallTimestamp = 0x06,
}

internal sealed class TransferV3LogicalRowHasher : IDisposable
{
    internal const int ScratchBufferBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private Decoder? _textDecoder;
    private long _expectedTextBytes;
    private long _writtenTextBytes;
    private bool _textOpen;
    private bool _faulted;
    private bool _disposed;

    internal int PeakScratchBufferBytes { get; private set; }

    internal void AppendNull(TransferV3LogicalType type)
    {
        EnsureReadyForValue();
        ValidateType(type);
        AppendHeader(type, isPresent: false, payloadLength: 0);
    }

    internal void AppendUuid(Guid value)
    {
        EnsureReadyForValue();
        Span<byte> payload = stackalloc byte[16];
        if (!value.TryWriteBytes(payload, bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode the UUID value.");
        }

        AppendValue(TransferV3LogicalType.Uuid, payload);
    }

    internal void AppendInt32(int value)
    {
        EnsureReadyForValue();
        Span<byte> payload = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(payload, value);
        AppendValue(TransferV3LogicalType.Int32, payload);
    }

    internal void AppendInt64(long value)
    {
        EnsureReadyForValue();
        Span<byte> payload = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(payload, value);
        AppendValue(TransferV3LogicalType.Int64, payload);
    }

    internal void AppendBoolean(bool value)
    {
        EnsureReadyForValue();
        Span<byte> payload = stackalloc byte[1];
        payload[0] = value ? (byte)0x01 : (byte)0x00;
        AppendValue(TransferV3LogicalType.Boolean, payload);
    }

    internal void AppendText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureReadyForValue();
        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Logical text values may not contain NUL.", nameof(value));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Logical text must be valid Unicode.", nameof(value), exception);
        }

        BeginText(byteCount);
        var encoder = StrictUtf8.GetEncoder();
        var scratch = new byte[ScratchBufferBytes];
        PeakScratchBufferBytes = Math.Max(PeakScratchBufferBytes, scratch.Length);
        var remaining = value.AsSpan();
        try
        {
            while (!remaining.IsEmpty)
            {
                encoder.Convert(
                    remaining,
                    scratch,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                if (charsUsed == 0 && bytesUsed == 0)
                {
                    throw new InvalidOperationException("UTF-8 encoding made no progress.");
                }

                AppendTextUtf8Chunk(scratch.AsSpan(0, bytesUsed));
                remaining = remaining[charsUsed..];
            }

            EndText();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    internal void BeginText(long utf8ByteLength)
    {
        EnsureReadyForValue();
        if (utf8ByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8ByteLength));
        }

        AppendHeader(
            TransferV3LogicalType.Text,
            isPresent: true,
            checked((ulong)utf8ByteLength));
        _textDecoder = StrictUtf8.GetDecoder();
        _expectedTextBytes = utf8ByteLength;
        _writtenTextBytes = 0;
        _textOpen = true;
    }

    internal void AppendTextUtf8Chunk(ReadOnlySpan<byte> bytes)
    {
        EnsureUsable();
        if (!_textOpen || _textDecoder is null)
        {
            throw new InvalidOperationException("No streaming text value is open.");
        }

        if (bytes.IndexOf((byte)0) >= 0)
        {
            _faulted = true;
            throw new ArgumentException("Logical text values may not contain NUL.", nameof(bytes));
        }

        if (bytes.Length > _expectedTextBytes - _writtenTextBytes)
        {
            _faulted = true;
            throw new ArgumentException("The text value exceeds its declared UTF-8 length.", nameof(bytes));
        }

        Span<char> characters = stackalloc char[512];
        var remaining = bytes;
        try
        {
            while (!remaining.IsEmpty)
            {
                _textDecoder.Convert(
                    remaining,
                    characters,
                    flush: false,
                    out var bytesUsed,
                    out _,
                    out _);
                if (bytesUsed == 0)
                {
                    throw new ArgumentException("The UTF-8 decoder made no progress.", nameof(bytes));
                }

                remaining = remaining[bytesUsed..];
            }
        }
        catch (DecoderFallbackException exception)
        {
            _faulted = true;
            throw new ArgumentException("Logical text contains invalid UTF-8.", nameof(bytes), exception);
        }

        _hash.AppendData(bytes);
        _writtenTextBytes += bytes.Length;
    }

    internal void EndText()
    {
        EnsureUsable();
        if (!_textOpen || _textDecoder is null)
        {
            throw new InvalidOperationException("No streaming text value is open.");
        }

        if (_writtenTextBytes != _expectedTextBytes)
        {
            _faulted = true;
            throw new InvalidOperationException("The text value did not match its declared UTF-8 length.");
        }

        Span<char> characters = stackalloc char[2];
        try
        {
            _textDecoder.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out _,
                out var completed);
            if (!completed)
            {
                _faulted = true;
                throw new InvalidOperationException("The UTF-8 text value is incomplete.");
            }
        }
        catch (DecoderFallbackException exception)
        {
            _faulted = true;
            throw new ArgumentException("Logical text contains incomplete UTF-8.", exception);
        }

        _textDecoder = null;
        _textOpen = false;
    }

    internal void AppendLocalTimestamp(DateTime value)
    {
        EnsureReadyForValue();
        var text = TransferV3LocalTimestamp.Format(value);
        Span<byte> payload = stackalloc byte[TransferV3LocalTimestamp.EncodedLength];
        var written = Encoding.ASCII.GetBytes(text, payload);
        if (written != payload.Length)
        {
            throw new InvalidOperationException("The local timestamp encoding length changed.");
        }

        AppendValue(TransferV3LogicalType.LocalWallTimestamp, payload);
    }

    internal byte[] GetHashAndReset()
    {
        EnsureReadyForValue();
        return _hash.GetHashAndReset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hash.Dispose();
        _disposed = true;
    }

    private void AppendValue(TransferV3LogicalType type, ReadOnlySpan<byte> payload)
    {
        AppendHeader(type, isPresent: true, checked((ulong)payload.Length));
        _hash.AppendData(payload);
    }

    private void AppendHeader(
        TransferV3LogicalType type,
        bool isPresent,
        ulong payloadLength)
    {
        ValidateType(type);
        Span<byte> header = stackalloc byte[10];
        header[0] = (byte)type;
        header[1] = isPresent ? (byte)0x01 : (byte)0x00;
        BinaryPrimitives.WriteUInt64BigEndian(header[2..], payloadLength);
        _hash.AppendData(header);
    }

    private void EnsureReadyForValue()
    {
        EnsureUsable();
        if (_textOpen)
        {
            throw new InvalidOperationException("Finish the streaming text value first.");
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The logical-row hash is faulted.");
        }
    }

    private static void ValidateType(TransferV3LogicalType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}

internal static class TransferV3LocalTimestamp
{
    internal const int EncodedLength = 26;
    private const string ExactFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff";

    internal static string Format(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Transfer v3 local-wall timestamps must have DateTimeKind.Unspecified.",
                nameof(value));
        }

        if (value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentException(
                "Transfer v3 timestamps must have exact microsecond precision.",
                nameof(value));
        }

        return value.ToString(ExactFormat, CultureInfo.InvariantCulture);
    }

    internal static DateTime Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != EncodedLength
            || !DateTime.TryParseExact(
                value,
                ExactFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new FormatException("The transfer timestamp is not canonical local-wall text.");
        }

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (!string.Equals(Format(parsed), value, StringComparison.Ordinal))
        {
            throw new FormatException("The transfer timestamp is not canonical local-wall text.");
        }

        return parsed;
    }
}

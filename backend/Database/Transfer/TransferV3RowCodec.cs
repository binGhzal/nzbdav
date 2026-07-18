using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NzbWebDAV.Database.Transfer;

internal sealed record TransferV3DecodedField(bool IsNull, object? Value);

internal sealed record TransferV3StreamedFieldResult(
    bool IsNull,
    long EncodedBytes,
    long PayloadBytes,
    int Chunks);

internal sealed class TransferV3FieldStreamMetrics
{
    internal long BytesRead { get; private set; }
    internal long BytesWritten { get; private set; }
    internal long ChunksRead { get; private set; }
    internal long ChunksWritten { get; private set; }
    internal int MaxReadChunkBytesObserved { get; private set; }
    internal int MaxWrittenChunkBytesObserved { get; private set; }
    internal int MaxOwnedBufferBytesObserved { get; private set; }

    internal void ObserveRead(int bytes)
    {
        BytesRead = checked(BytesRead + bytes);
        ChunksRead = checked(ChunksRead + 1);
        MaxReadChunkBytesObserved = Math.Max(MaxReadChunkBytesObserved, bytes);
    }

    internal void ObserveWritten(int bytes)
    {
        BytesWritten = checked(BytesWritten + bytes);
        ChunksWritten = checked(ChunksWritten + 1);
        MaxWrittenChunkBytesObserved = Math.Max(MaxWrittenChunkBytesObserved, bytes);
    }

    internal void ObserveOwnedBuffer(int bytes) =>
        MaxOwnedBufferBytesObserved = Math.Max(MaxOwnedBufferBytesObserved, bytes);
}

internal readonly record struct TransferV3FieldPlan(long EncodedBytes, int ChunkCount)
{
    internal int GetChunkBytes(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= ChunkCount)
        {
            throw TransferV3RowCodec.Failure("chunk-index");
        }

        var offset = checked((long)chunkIndex * TransferV3Limits.MaxDecodedChunkBytes);
        return checked((int)Math.Min(
            TransferV3Limits.MaxDecodedChunkBytes,
            EncodedBytes - offset));
    }
}

internal static class TransferV3RowCodec
{
    internal const int MaxWholeFieldBytes = TransferV3Limits.MaxDecodedChunkBytes;

    private const byte NullMarker = 0x00;
    private const byte ValueMarker = 0x01;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] LocalWallFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    internal static byte[] EncodeField(TransferV3ColumnContract column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (value is null)
        {
            if (!column.Nullable)
            {
                throw Failure("required-null");
            }

            return [NullMarker];
        }

        return column.Kind switch
        {
            TransferV3ColumnKind.Uuid when value is Guid uuid => EncodeUuid(uuid),
            TransferV3ColumnKind.Boolean when value is bool boolean => EncodeBoolean(boolean),
            TransferV3ColumnKind.EnumInt32 when value is int integer =>
                EncodeInt32(column, integer),
            TransferV3ColumnKind.Int32 when value is int integer =>
                EncodeInt32(column, integer),
            TransferV3ColumnKind.Int64 when value is long integer => EncodeInt64(integer),
            TransferV3ColumnKind.Text => EncodeText(column, TextBytes(value)),
            TransferV3ColumnKind.LocalWallTimestamp when value is DateTime timestamp =>
                EncodeLocalWall(timestamp),
            TransferV3ColumnKind.LocalWallTimestamp =>
                EncodeLocalWall(ParseLocalWall(TextBytes(value))),
            TransferV3ColumnKind.Instant when value is long integer =>
                EncodeInstant(column, integer),
            _ => throw Failure("value-type"),
        };
    }

    internal static TransferV3DecodedField DecodeField(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> encoded)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (encoded.Length > MaxWholeFieldBytes)
        {
            throw Failure("field-stream-required");
        }

        if (encoded.IsEmpty)
        {
            throw Failure("field-empty");
        }

        if (encoded[0] == NullMarker)
        {
            if (encoded.Length != 1 || !column.Nullable)
            {
                throw Failure("null-shape");
            }

            return new TransferV3DecodedField(true, null);
        }

        if (encoded[0] != ValueMarker)
        {
            throw Failure("field-marker");
        }

        var payload = encoded[1..];
        object value = column.Kind switch
        {
            TransferV3ColumnKind.Uuid => DecodeUuid(payload),
            TransferV3ColumnKind.Boolean => DecodeBoolean(payload),
            TransferV3ColumnKind.EnumInt32 => DecodeInt32(column, payload),
            TransferV3ColumnKind.Int32 => DecodeInt32(column, payload),
            TransferV3ColumnKind.Int64 => DecodeInt64(payload),
            TransferV3ColumnKind.Text => DecodeText(column, payload),
            TransferV3ColumnKind.LocalWallTimestamp => DecodeLocalWall(payload),
            TransferV3ColumnKind.Instant => DecodeInstant(column, payload),
            _ => throw Failure("column-kind"),
        };
        return new TransferV3DecodedField(false, value);
    }

    // The sink must consume the chunk before its ValueTask completes. The
    // memory aliases one bounded reusable buffer and is cleared after return.
    internal static async ValueTask<TransferV3StreamedFieldResult> EncodeTextFieldAsync(
        TransferV3ColumnContract column,
        bool isNull,
        long payloadBytes,
        long maxFieldBytes,
        IAsyncEnumerable<ReadOnlyMemory<byte>> payloadChunks,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeChunkAsync,
        TransferV3FieldStreamMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(payloadChunks);
        ArgumentNullException.ThrowIfNull(writeChunkAsync);
        ArgumentNullException.ThrowIfNull(metrics);
        cancellationToken.ThrowIfCancellationRequested();

        if (column.Kind != TransferV3ColumnKind.Text)
        {
            throw Failure("column-kind");
        }

        if (isNull && !column.Nullable)
        {
            throw Failure("required-null");
        }

        var plan = PlanField(isNull, payloadBytes, maxFieldBytes);
        var requestedBufferBytes = checked((int)Math.Min(
            TransferV3Limits.MaxDecodedChunkBytes,
            plan.EncodedBytes));
        var (buffer, pooled) = RentBoundedBuffer(requestedBufferBytes);
        metrics.ObserveOwnedBuffer(buffer.Length);
        var validator = isNull ? null : new StreamingUtf8Validator(column.MaxRunes);
        if (validator is not null)
        {
            metrics.ObserveOwnedBuffer(StreamingUtf8Validator.BufferBytes);
        }

        var payloadRead = 0L;
        var buffered = 1;
        var chunksWritten = 0;
        buffer[0] = isNull ? NullMarker : ValueMarker;

        try
        {
            await using var source = GetRedactingChunkEnumerator(
                payloadChunks,
                cancellationToken);
            while (await source.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = source.Current;
                metrics.ObserveRead(chunk.Length);
                if (chunk.Length > TransferV3Limits.MaxDecodedChunkBytes)
                {
                    throw Failure("chunk-size");
                }

                if (chunk.IsEmpty)
                {
                    continue;
                }

                if (isNull)
                {
                    throw Failure("null-shape");
                }

                long nextPayloadBytes;
                try
                {
                    nextPayloadBytes = checked(payloadRead + chunk.Length);
                }
                catch (OverflowException)
                {
                    throw Failure("field-length");
                }

                if (nextPayloadBytes > payloadBytes)
                {
                    throw Failure("field-length");
                }

                validator!.Append(chunk.Span);
                var sourceOffset = 0;
                while (sourceOffset < chunk.Length)
                {
                    var copied = Math.Min(
                        requestedBufferBytes - buffered,
                        chunk.Length - sourceOffset);
                    chunk.Span.Slice(sourceOffset, copied).CopyTo(buffer.AsSpan(buffered));
                    sourceOffset += copied;
                    buffered += copied;
                    if (buffered == requestedBufferBytes)
                    {
                        await WriteChunkRedactedAsync(
                            writeChunkAsync,
                            buffer.AsMemory(0, buffered),
                            metrics,
                            cancellationToken).ConfigureAwait(false);
                        chunksWritten++;
                        buffered = 0;
                    }
                }

                payloadRead = nextPayloadBytes;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (payloadRead != payloadBytes)
            {
                throw Failure("field-length");
            }

            validator?.Complete();
            if (buffered > 0)
            {
                await WriteChunkRedactedAsync(
                    writeChunkAsync,
                    buffer.AsMemory(0, buffered),
                    metrics,
                    cancellationToken).ConfigureAwait(false);
                chunksWritten++;
            }

            return new TransferV3StreamedFieldResult(
                isNull,
                plan.EncodedBytes,
                payloadBytes,
                chunksWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            buffer.AsSpan().Clear();
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
        }
    }

    internal static async ValueTask<TransferV3StreamedFieldResult> DecodeTextFieldAsync(
        TransferV3ColumnContract column,
        long encodedBytes,
        long maxFieldBytes,
        IAsyncEnumerable<ReadOnlyMemory<byte>> encodedChunks,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writePayloadChunkAsync,
        TransferV3FieldStreamMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(encodedChunks);
        ArgumentNullException.ThrowIfNull(writePayloadChunkAsync);
        ArgumentNullException.ThrowIfNull(metrics);
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = CreateTextFieldDecoder(column, encodedBytes, maxFieldBytes, metrics);

        try
        {
            await using var source = GetRedactingChunkEnumerator(
                encodedChunks,
                cancellationToken);
            while (await source.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = decoder.Append(source.Current, cancellationToken);
                if (!payload.IsEmpty)
                {
                    await WriteChunkRedactedAsync(
                        writePayloadChunkAsync,
                        payload,
                        metrics,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return decoder.Complete(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    internal static TransferV3TextFieldDecoder CreateTextFieldDecoder(
        TransferV3ColumnContract column,
        long encodedBytes,
        long maxFieldBytes,
        TransferV3FieldStreamMetrics metrics) =>
        new(column, encodedBytes, maxFieldBytes, metrics);

    internal static TransferV3TextFieldDecoder CreateTextFieldDecoder(
        TransferV3ColumnContract column,
        long maxFieldBytes,
        TransferV3FieldStreamMetrics metrics) =>
        new(column, expectedEncodedBytes: null, maxFieldBytes, metrics);

    internal static TransferV3FieldPlan PlanField(
        bool isNull,
        long payloadBytes,
        long maxFieldBytes)
    {
        if (payloadBytes < 0
            || maxFieldBytes is <= 0 or > TransferV3Limits.MaxAllowedFieldBytes
            || (isNull && payloadBytes != 0))
        {
            throw Failure("field-size");
        }

        long encodedBytes;
        try
        {
            encodedBytes = isNull ? 1 : checked(payloadBytes + 1);
        }
        catch (OverflowException)
        {
            throw Failure("field-size");
        }

        if (encodedBytes > maxFieldBytes)
        {
            throw Failure("field-size");
        }

        var chunkCount = checked((int)(
            (encodedBytes + TransferV3Limits.MaxDecodedChunkBytes - 1)
            / TransferV3Limits.MaxDecodedChunkBytes));
        return new TransferV3FieldPlan(encodedBytes, chunkCount);
    }

    internal static TransferV3RowFormatException Failure(string code) => new(code);

    private static byte[] EncodeUuid(Guid value)
    {
        var encoded = NewFixedField(16);
        if (!value.TryWriteBytes(encoded.AsSpan(1), bigEndian: true, out var written)
            || written != 16)
        {
            throw Failure("uuid-encode");
        }

        return encoded;
    }

    private static byte[] EncodeBoolean(bool value) => [ValueMarker, value ? (byte)1 : (byte)0];

    private static byte[] EncodeInt32(TransferV3ColumnContract column, int value)
    {
        if (column.Kind == TransferV3ColumnKind.EnumInt32
            && !column.AllowedIntegers.Contains(value))
        {
            throw Failure("enum-domain");
        }

        var encoded = NewFixedField(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(1), value);
        return encoded;
    }

    private static byte[] EncodeInt64(long value)
    {
        var encoded = NewFixedField(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(1), value);
        return encoded;
    }

    private static byte[] EncodeText(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> value)
    {
        EnsureWholeFieldSize(value.Length);
        ValidateText(column, value);
        var encoded = new byte[checked(value.Length + 1)];
        encoded[0] = ValueMarker;
        value.CopyTo(encoded.AsSpan(1));
        return encoded;
    }

    private static byte[] EncodeLocalWall(DateTime value)
    {
        ValidateLocalWall(value);
        var encoded = NewFixedField(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(1), value.Ticks);
        return encoded;
    }

    private static byte[] EncodeInstant(TransferV3ColumnContract column, long value)
    {
        ValidateInstant(column, value);
        return EncodeInt64(value);
    }

    private static Guid DecodeUuid(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 16);
        return new Guid(payload, bigEndian: true);
    }

    private static bool DecodeBoolean(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 1);
        return payload[0] switch
        {
            0 => false,
            1 => true,
            _ => throw Failure("boolean-domain"),
        };
    }

    private static int DecodeInt32(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(payload);
        if (column.Kind == TransferV3ColumnKind.EnumInt32
            && !column.AllowedIntegers.Contains(value))
        {
            throw Failure("enum-domain");
        }

        return value;
    }

    private static long DecodeInt64(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, sizeof(long));
        return BinaryPrimitives.ReadInt64BigEndian(payload);
    }

    private static byte[] DecodeText(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> payload)
    {
        ValidateText(column, payload);
        return payload.ToArray();
    }

    private static DateTime DecodeLocalWall(ReadOnlySpan<byte> payload)
    {
        var ticks = DecodeInt64(payload);
        DateTime value;
        try
        {
            value = new DateTime(ticks, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure("timestamp-range");
        }

        ValidateLocalWall(value);
        return value;
    }

    private static long DecodeInstant(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> payload)
    {
        var value = DecodeInt64(payload);
        ValidateInstant(column, value);
        return value;
    }

    private static ReadOnlySpan<byte> TextBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        Memory<byte> bytes => bytes.Span,
        ReadOnlyMemory<byte> bytes => bytes.Span,
        string text => EncodeString(text),
        _ => throw Failure("value-type"),
    };

    private static byte[] EncodeString(string value)
    {
        try
        {
            EnsureWholeFieldSize(StrictUtf8.GetByteCount(value));
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw Failure("utf8");
        }
    }

    private static void ValidateText(
        TransferV3ColumnContract column,
        ReadOnlySpan<byte> value)
    {
        var remaining = value;
        long runes = 0;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done || consumed <= 0)
            {
                throw Failure("utf8");
            }

            if (rune.Value == 0)
            {
                throw Failure("text-nul");
            }

            runes++;
            if (column.MaxRunes is { } maximum && runes > maximum)
            {
                throw Failure("text-runes");
            }

            remaining = remaining[consumed..];
        }
    }

    private static DateTime ParseLocalWall(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 19 or > 27)
        {
            throw Failure("timestamp-format");
        }

        var synthetic = new TransferV3ColumnContract(
            string.Empty,
            "TEXT",
            "text",
            false,
            TransferV3ColumnKind.LocalWallTimestamp,
            TransferV3InstantEncoding.None,
            TransferV3UuidRole.None,
            value.Length,
            []);
        ValidateText(synthetic, value);

        string text;
        try
        {
            text = StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException)
        {
            throw Failure("timestamp-format");
        }

        if (!DateTime.TryParseExact(
                text,
                LocalWallFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw Failure("timestamp-format");
        }

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        ValidateLocalWall(parsed);
        return parsed;
    }

    private static void ValidateLocalWall(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified
            || value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw Failure("timestamp-domain");
        }
    }

    private static void ValidateInstant(TransferV3ColumnContract column, long value)
    {
        var valid = column.InstantEncoding switch
        {
            TransferV3InstantEncoding.UtcTicks =>
                value >= DateTime.MinValue.Ticks && value <= DateTime.MaxValue.Ticks,
            TransferV3InstantEncoding.UnixSeconds =>
                value >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
                && value <= DateTimeOffset.MaxValue.ToUnixTimeSeconds(),
            _ => false,
        };
        if (!valid)
        {
            throw Failure("instant-domain");
        }
    }

    private static byte[] NewFixedField(int payloadBytes)
    {
        var encoded = new byte[checked(payloadBytes + 1)];
        encoded[0] = ValueMarker;
        return encoded;
    }

    private static void RequireLength(ReadOnlySpan<byte> payload, int expected)
    {
        if (payload.Length != expected)
        {
            throw Failure("field-length");
        }
    }

    private static void EnsureWholeFieldSize(int payloadBytes)
    {
        if (payloadBytes >= MaxWholeFieldBytes)
        {
            throw Failure("field-stream-required");
        }
    }

    private static (byte[] Buffer, bool Pooled) RentBoundedBuffer(int requestedBytes)
    {
        var rented = ArrayPool<byte>.Shared.Rent(requestedBytes);
        if (rented.Length <= TransferV3Limits.MaxDecodedChunkBytes)
        {
            return (rented, true);
        }

        ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        return (GC.AllocateUninitializedArray<byte>(requestedBytes), false);
    }

    private static async ValueTask WriteChunkRedactedAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeChunkAsync,
        ReadOnlyMemory<byte> chunk,
        TransferV3FieldStreamMetrics metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writeChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            metrics.ObserveWritten(chunk.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("stream-write");
        }
    }

    private static RedactingChunkEnumerator GetRedactingChunkEnumerator(
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var enumerator = chunks.GetAsyncEnumerator(cancellationToken);
            return enumerator is null
                ? throw Failure("stream-read")
                : new RedactingChunkEnumerator(enumerator, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            throw Failure("stream-read");
        }
    }

    private sealed class RedactingChunkEnumerator(
        IAsyncEnumerator<ReadOnlyMemory<byte>> inner,
        CancellationToken cancellationToken) : IAsyncDisposable
    {
        internal ReadOnlyMemory<byte> Current
        {
            get
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return inner.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch
                {
                    throw Failure("stream-read");
                }
            }
        }

        internal async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                return await inner.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                throw Failure("stream-read");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                throw Failure("stream-read");
            }
        }
    }

    internal sealed class TransferV3TextFieldDecoder
    {
        private readonly TransferV3ColumnContract _column;
        private readonly long? _expectedEncodedBytes;
        private readonly long _maxFieldBytes;
        private readonly TransferV3FieldStreamMetrics _metrics;
        private readonly StreamingUtf8Validator _validator;
        private long _bytesRead;
        private long _payloadBytes;
        private int _chunks;
        private byte? _marker;
        private bool _completed;
        private bool _faulted;

        internal TransferV3TextFieldDecoder(
            TransferV3ColumnContract column,
            long? expectedEncodedBytes,
            long maxFieldBytes,
            TransferV3FieldStreamMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(metrics);
            if (column.Kind != TransferV3ColumnKind.Text)
            {
                throw Failure("column-kind");
            }

            if (maxFieldBytes is <= 0 or > TransferV3Limits.MaxAllowedFieldBytes
                || expectedEncodedBytes is <= 0)
            {
                throw Failure("field-size");
            }

            if (expectedEncodedBytes is { } exactBytes)
            {
                _ = PlanField(isNull: false, payloadBytes: exactBytes - 1, maxFieldBytes);
            }

            _column = column;
            _expectedEncodedBytes = expectedEncodedBytes;
            _maxFieldBytes = maxFieldBytes;
            _metrics = metrics;
            _validator = new StreamingUtf8Validator(column.MaxRunes);
            metrics.ObserveOwnedBuffer(StreamingUtf8Validator.BufferBytes);
        }

        // The returned payload aliases the caller-provided chunk so a frame
        // observer can hash or stage it synchronously without another buffer.
        internal ReadOnlyMemory<byte> Append(
            ReadOnlyMemory<byte> chunk,
            CancellationToken cancellationToken)
        {
            EnsureReady();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _metrics.ObserveRead(chunk.Length);
                if (chunk.IsEmpty
                    || chunk.Length > TransferV3Limits.MaxDecodedChunkBytes)
                {
                    throw Failure("chunk-size");
                }

                long nextBytesRead;
                try
                {
                    nextBytesRead = checked(_bytesRead + chunk.Length);
                }
                catch (OverflowException)
                {
                    throw Failure("field-length");
                }

                if (nextBytesRead > _maxFieldBytes)
                {
                    throw Failure("field-size");
                }

                if (_expectedEncodedBytes is { } expectedBytes
                    && nextBytesRead > expectedBytes)
                {
                    throw Failure("field-length");
                }

                var payloadOffset = 0;
                if (_marker is null)
                {
                    _marker = chunk.Span[0];
                    payloadOffset = 1;
                    if (_marker == NullMarker
                        && (!_column.Nullable
                            || (_expectedEncodedBytes is { } exactBytes && exactBytes != 1)))
                    {
                        throw Failure("null-shape");
                    }

                    if (_marker is not NullMarker and not ValueMarker)
                    {
                        throw Failure("field-marker");
                    }
                }

                var payload = chunk[payloadOffset..];
                if (_marker == NullMarker)
                {
                    if (!payload.IsEmpty)
                    {
                        throw Failure("null-shape");
                    }
                }
                else if (!payload.IsEmpty)
                {
                    _validator.Append(payload.Span);
                    _payloadBytes = checked(_payloadBytes + payload.Length);
                }

                _bytesRead = nextBytesRead;
                if (_chunks == int.MaxValue)
                {
                    throw Failure("chunk-count");
                }

                _chunks++;
                return payload;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        internal TransferV3StreamedFieldResult Complete(
            CancellationToken cancellationToken)
        {
            EnsureReady();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((_expectedEncodedBytes is { } expectedBytes
                        && _bytesRead != expectedBytes)
                    || _marker is null)
                {
                    throw Failure("field-length");
                }

                if (_marker == ValueMarker)
                {
                    _validator.Complete();
                }

                _completed = true;
                return new TransferV3StreamedFieldResult(
                    _marker == NullMarker,
                    _bytesRead,
                    _payloadBytes,
                    _chunks);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        private void EnsureReady()
        {
            if (_completed || _faulted)
            {
                throw Failure("field-state");
            }
        }
    }

    private sealed class StreamingUtf8Validator(int? maxRunes)
    {
        internal const int BufferBytes = 4;

        private readonly byte[] _pending = new byte[BufferBytes];
        private int _pendingCount;
        private long _runes;

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            while (_pendingCount > 0)
            {
                var status = Rune.DecodeFromUtf8(
                    _pending.AsSpan(0, _pendingCount),
                    out var rune,
                    out var consumed);
                if (status == OperationStatus.Done)
                {
                    if (consumed != _pendingCount)
                    {
                        throw Failure("utf8");
                    }

                    Accept(rune);
                    _pendingCount = 0;
                    break;
                }

                if (status != OperationStatus.NeedMoreData || bytes.IsEmpty)
                {
                    if (status != OperationStatus.NeedMoreData)
                    {
                        throw Failure("utf8");
                    }

                    return;
                }

                if (_pendingCount >= _pending.Length)
                {
                    throw Failure("utf8");
                }

                _pending[_pendingCount++] = bytes[0];
                bytes = bytes[1..];
            }

            while (!bytes.IsEmpty)
            {
                var nonAscii = bytes.IndexOfAnyExceptInRange((byte)1, (byte)0x7f);
                if (nonAscii < 0)
                {
                    AcceptAscii(bytes.Length);
                    return;
                }

                if (nonAscii > 0)
                {
                    AcceptAscii(nonAscii);
                    bytes = bytes[nonAscii..];
                }

                var status = Rune.DecodeFromUtf8(bytes, out var rune, out var consumed);
                if (status == OperationStatus.Done)
                {
                    if (consumed <= 0)
                    {
                        throw Failure("utf8");
                    }

                    Accept(rune);
                    bytes = bytes[consumed..];
                    continue;
                }

                if (status != OperationStatus.NeedMoreData || bytes.Length >= BufferBytes)
                {
                    throw Failure("utf8");
                }

                bytes.CopyTo(_pending);
                _pendingCount = bytes.Length;
                return;
            }
        }

        internal void Complete()
        {
            if (_pendingCount != 0)
            {
                throw Failure("utf8");
            }
        }

        private void Accept(Rune rune)
        {
            if (rune.Value == 0)
            {
                throw Failure("text-nul");
            }

            _runes++;
            if (maxRunes is { } maximum && _runes > maximum)
            {
                throw Failure("text-runes");
            }
        }

        private void AcceptAscii(int count)
        {
            _runes = checked(_runes + count);
            if (maxRunes is { } maximum && _runes > maximum)
            {
                throw Failure("text-runes");
            }
        }
    }
}

internal sealed class TransferV3RowFormatException : FormatException
{
    internal TransferV3RowFormatException(string code)
        : base($"Transfer-v3 row field rejected ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }
}

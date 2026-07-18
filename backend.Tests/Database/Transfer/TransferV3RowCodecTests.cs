using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3RowCodecTests
{
    [Fact]
    public void EveryTransferredColumnContract_HasAnExactRoundTripEncoding()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var columns = contract.Tables.SelectMany(table => table.Columns).ToArray();

        Assert.Equal(235, columns.Length);
        foreach (var column in columns)
        {
            var value = SampleValue(column);
            var encoded = TransferV3RowCodec.EncodeField(column, value);
            var decoded = TransferV3RowCodec.DecodeField(column, encoded);

            Assert.False(decoded.IsNull);
            AssertDecodedValue(column, value, decoded.Value);

            if (column.Nullable)
            {
                var nullBytes = TransferV3RowCodec.EncodeField(column, null);
                var decodedNull = TransferV3RowCodec.DecodeField(column, nullBytes);
                Assert.Equal(new byte[] { 0x00 }, nullBytes);
                Assert.True(decodedNull.IsNull);
                Assert.Null(decodedNull.Value);
            }
        }
    }

    [Fact]
    public void NullAndEmptyText_AreDifferentAndStrictUtf8IsPreservedExactly()
    {
        var column = Column(TransferV3ColumnKind.Text, nullable: true, maxRunes: 20);
        var empty = TransferV3RowCodec.EncodeField(column, ReadOnlyMemory<byte>.Empty);
        var unicode = "Straße/東京/😀"u8.ToArray();
        var encoded = TransferV3RowCodec.EncodeField(column, unicode.AsMemory());

        Assert.Equal(new byte[] { 0x00 }, TransferV3RowCodec.EncodeField(column, null));
        Assert.Equal(new byte[] { 0x01 }, empty);
        Assert.Equal(new byte[] { 0x01 }.Concat(unicode), encoded);
        Assert.Equal(unicode, Assert.IsType<byte[]>(
            TransferV3RowCodec.DecodeField(column, encoded).Value));

        foreach (var invalid in new[]
                 {
                     new byte[] { 0x01, 0xc3, 0x28 },
                     new byte[] { 0x01, 0x00 },
                     new byte[] { 0x02 },
                 })
        {
            AssertRowFailure(
                invalid[0] == 0x02
                    ? "field-marker"
                    : invalid.Contains((byte)0x00) ? "text-nul" : "utf8",
                () => TransferV3RowCodec.DecodeField(column, invalid));
        }

        AssertRowFailure("utf8", () => TransferV3RowCodec.EncodeField(
            column,
            new byte[] { 0xf0, 0x28, 0x8c, 0x28 }.AsMemory()));
    }

    [Fact]
    public void StringWithUnpairedSurrogate_IsRejectedAsStableRedactedUtf8Failure()
    {
        var column = Column(TransferV3ColumnKind.Text, maxRunes: 20);
        var invalid = "private-prefix-\ud800-private-suffix";

        var exception = Assert.Throws<TransferV3RowFormatException>(() =>
            TransferV3RowCodec.EncodeField(column, invalid));

        Assert.Equal("utf8", exception.Code);
        Assert.Equal("Transfer-v3 row field rejected (utf8).", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("d800", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("surrogate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UuidBooleanAndIntegerPayloads_UseNetworkAndBigEndianOrder()
    {
        var uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal(
            Convert.FromHexString("0100112233445566778899aabbccddeeff"),
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Uuid), uuid));
        Assert.Equal(
            new byte[] { 0x01, 0x00 },
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Boolean), false));
        Assert.Equal(
            new byte[] { 0x01, 0x01 },
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Boolean), true));
        Assert.Equal(
            Convert.FromHexString("0180000000"),
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Int32), int.MinValue));
        Assert.Equal(
            Convert.FromHexString("017fffffff"),
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Int32), int.MaxValue));
        Assert.Equal(
            Convert.FromHexString("018000000000000000"),
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Int64), long.MinValue));
        Assert.Equal(
            Convert.FromHexString("017fffffffffffffff"),
            TransferV3RowCodec.EncodeField(Column(TransferV3ColumnKind.Int64), long.MaxValue));

        var enumColumn = Column(
            TransferV3ColumnKind.EnumInt32,
            allowedIntegers: [-1, 2]);
        Assert.Equal(
            Convert.FromHexString("01ffffffff"),
            TransferV3RowCodec.EncodeField(enumColumn, -1));
        AssertRowFailure("enum-domain", () => TransferV3RowCodec.EncodeField(enumColumn, 1));
    }

    [Fact]
    public void LocalWallTimestamp_ParsesOnlyReviewedSourceFormatsAndWritesTicks()
    {
        var column = Column(TransferV3ColumnKind.LocalWallTimestamp);
        var expected = new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Unspecified)
            .AddTicks(1_234_560);
        var encodedWithoutFraction = TransferV3RowCodec.EncodeField(
            column,
            "2026-07-13 01:02:03"u8.ToArray().AsMemory());
        var encodedWithFraction = TransferV3RowCodec.EncodeField(
            column,
            "2026-07-13 01:02:03.1234560"u8.ToArray().AsMemory());

        Assert.Equal(9, encodedWithFraction.Length);
        Assert.Equal(expected.Ticks, BinaryPrimitives.ReadInt64BigEndian(encodedWithFraction.AsSpan(1)));
        Assert.Equal(
            new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Unspecified),
            TransferV3RowCodec.DecodeField(column, encodedWithoutFraction).Value);
        Assert.Equal(expected, TransferV3RowCodec.DecodeField(column, encodedWithFraction).Value);

        foreach (var invalid in new[]
                 {
                     (Value: "2026-07-13T01:02:03.123456", Code: "timestamp-format"),
                     (Value: "2026-07-13 01:02:03.1234567", Code: "timestamp-domain"),
                     (Value: "2026-07-13 01:02:03.123456Z", Code: "timestamp-format"),
                     (Value: "2026-02-30 01:02:03", Code: "timestamp-format"),
                 })
        {
            AssertRowFailure(invalid.Code, () => TransferV3RowCodec.EncodeField(
                column,
                Encoding.UTF8.GetBytes(invalid.Value).AsMemory()));
        }

        AssertRowFailure(
            "timestamp-domain",
            () => TransferV3RowCodec.EncodeField(column, DateTime.UtcNow));
        AssertRowFailure("timestamp-domain", () => TransferV3RowCodec.EncodeField(
            column,
            new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Unspecified).AddTicks(1)));
    }

    [Fact]
    public void InstantEncodings_KeepValidatedSourceIntegerAndEnforceTheirRanges()
    {
        var ticks = Column(
            TransferV3ColumnKind.Instant,
            instantEncoding: TransferV3InstantEncoding.UtcTicks);
        var seconds = Column(
            TransferV3ColumnKind.Instant,
            instantEncoding: TransferV3InstantEncoding.UnixSeconds);
        var minSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        var maxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

        Assert.Equal(0L, TransferV3RowCodec.DecodeField(
            ticks,
            TransferV3RowCodec.EncodeField(ticks, 0L)).Value);
        Assert.Equal(DateTime.MaxValue.Ticks, TransferV3RowCodec.DecodeField(
            ticks,
            TransferV3RowCodec.EncodeField(ticks, DateTime.MaxValue.Ticks)).Value);
        Assert.Equal(minSeconds, TransferV3RowCodec.DecodeField(
            seconds,
            TransferV3RowCodec.EncodeField(seconds, minSeconds)).Value);
        Assert.Equal(maxSeconds, TransferV3RowCodec.DecodeField(
            seconds,
            TransferV3RowCodec.EncodeField(seconds, maxSeconds)).Value);
        AssertRowFailure("instant-domain", () => TransferV3RowCodec.EncodeField(ticks, -1L));
        AssertRowFailure(
            "instant-domain",
            () => TransferV3RowCodec.EncodeField(seconds, checked(maxSeconds + 1)));
    }

    [Fact]
    public void FieldPlan_IncludesMarkerAndComputesBoundedChunksWithoutAllocatingGiantFields()
    {
        var oneChunk = TransferV3RowCodec.PlanField(
            isNull: false,
            payloadBytes: TransferV3Limits.MaxDecodedChunkBytes - 1L,
            maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes);
        var twoChunks = TransferV3RowCodec.PlanField(
            isNull: false,
            payloadBytes: TransferV3Limits.MaxDecodedChunkBytes,
            maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes);
        var giant = TransferV3RowCodec.PlanField(
            isNull: false,
            payloadBytes: TransferV3Limits.MaxAllowedFieldBytes - 1,
            maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes);

        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, oneChunk.EncodedBytes);
        Assert.Equal(1, oneChunk.ChunkCount);
        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, oneChunk.GetChunkBytes(0));
        Assert.Equal(2, twoChunks.ChunkCount);
        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, twoChunks.GetChunkBytes(0));
        Assert.Equal(1, twoChunks.GetChunkBytes(1));
        Assert.Equal(16_384, giant.ChunkCount);
        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, giant.GetChunkBytes(16_383));

        var nullField = TransferV3RowCodec.PlanField(true, 0, 1);
        Assert.Equal(1, nullField.EncodedBytes);
        Assert.Equal(1, nullField.GetChunkBytes(0));
        AssertRowFailure("field-size", () => TransferV3RowCodec.PlanField(true, 1, 10));
        AssertRowFailure("field-size", () => TransferV3RowCodec.PlanField(false, 10, 10));
        AssertRowFailure("chunk-index", () => giant.GetChunkBytes(giant.ChunkCount));
    }

    [Fact]
    public void Decoder_RejectsWrongLengthsDomainsMarkersAndRequiredNullsWithoutEchoingData()
    {
        var cases = new (TransferV3ColumnContract Column, byte[] Data, string Code)[]
        {
            (Column(TransferV3ColumnKind.Uuid), [0x01, 0xde, 0xad], "field-length"),
            (Column(TransferV3ColumnKind.Boolean), [0x01, 0x02], "boolean-domain"),
            (Column(TransferV3ColumnKind.Int32), [0x01, 0, 0, 0], "field-length"),
            (Column(TransferV3ColumnKind.Int64), [0x01, 0, 0, 0, 0, 0, 0, 0], "field-length"),
            (Column(TransferV3ColumnKind.Text), [0x00], "null-shape"),
            (Column(TransferV3ColumnKind.Text, nullable: true), [0x00, 0x01], "null-shape"),
            (Column(TransferV3ColumnKind.Text, nullable: true), [], "field-empty"),
        };

        foreach (var item in cases)
        {
            AssertRowFailure(
                item.Code,
                () => TransferV3RowCodec.DecodeField(item.Column, item.Data));
        }
    }

    [Fact]
    public async Task StreamingTextCodec_EmitsOneMarkerAndRoundTripsSplitUtf8InBoundedChunks()
    {
        var column = Column(TransferV3ColumnKind.Text);
        var first = Enumerable.Repeat((byte)'a', TransferV3Limits.MaxDecodedChunkBytes - 3)
            .Concat(new byte[] { 0xf0, 0x9f })
            .ToArray();
        var second = new byte[] { 0x98, 0x80, 0x01, (byte)'z' };
        var payload = first.Concat(second).ToArray();
        var encodedChunks = new List<byte[]>();
        var encodeMetrics = new TransferV3FieldStreamMetrics();

        var encoded = await TransferV3RowCodec.EncodeTextFieldAsync(
            column,
            isNull: false,
            payloadBytes: payload.LongLength,
            maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes,
            payloadChunks: Chunks(first, second),
            writeChunkAsync: Capture(encodedChunks),
            metrics: encodeMetrics,
            cancellationToken: CancellationToken.None);

        Assert.False(encoded.IsNull);
        Assert.Equal(payload.LongLength + 1, encoded.EncodedBytes);
        Assert.Equal(payload.LongLength, encoded.PayloadBytes);
        Assert.Equal(2, encoded.Chunks);
        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, encodedChunks[0].Length);
        Assert.Equal(4, encodedChunks[1].Length);
        Assert.Equal(0x01, encodedChunks[0][0]);
        Assert.Equal(
            new byte[] { 0x01 }.Concat(payload),
            encodedChunks.SelectMany(chunk => chunk));
        Assert.True(encodeMetrics.MaxOwnedBufferBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(encodeMetrics.MaxReadChunkBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(encodeMetrics.MaxWrittenChunkBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);

        var decodedPayload = new List<byte[]>();
        var decodeMetrics = new TransferV3FieldStreamMetrics();
        var decoded = await TransferV3RowCodec.DecodeTextFieldAsync(
            column,
            encodedBytes: encoded.EncodedBytes,
            maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes,
            encodedChunks: Chunks(
                [encodedChunks[0][0]],
                encodedChunks[0][1..],
                encodedChunks[1]),
            writePayloadChunkAsync: Capture(decodedPayload),
            metrics: decodeMetrics,
            cancellationToken: CancellationToken.None);

        Assert.False(decoded.IsNull);
        Assert.Equal(payload.LongLength, decoded.PayloadBytes);
        Assert.Equal(payload, decodedPayload.SelectMany(chunk => chunk));
        Assert.InRange(decodeMetrics.MaxOwnedBufferBytesObserved, 1, 4);
        Assert.True(decodeMetrics.MaxReadChunkBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);
    }

    [Fact]
    public async Task StreamingTextCodec_HandlesNullAndRejectsSplitUtf8AndLengthFailuresExactly()
    {
        var nullable = Column(TransferV3ColumnKind.Text, nullable: true);
        var required = Column(TransferV3ColumnKind.Text);
        var nullChunks = new List<byte[]>();
        var nullEncoded = await TransferV3RowCodec.EncodeTextFieldAsync(
            nullable,
            isNull: true,
            payloadBytes: 0,
            maxFieldBytes: 40,
            payloadChunks: Chunks(),
            writeChunkAsync: Capture(nullChunks),
            metrics: new TransferV3FieldStreamMetrics(),
            cancellationToken: CancellationToken.None);
        var nullDecoded = await TransferV3RowCodec.DecodeTextFieldAsync(
            nullable,
            encodedBytes: 1,
            maxFieldBytes: 40,
            encodedChunks: Chunks([0x00]),
            writePayloadChunkAsync: (_, _) => ValueTask.CompletedTask,
            metrics: new TransferV3FieldStreamMetrics(),
            cancellationToken: CancellationToken.None);

        Assert.True(nullEncoded.IsNull);
        Assert.True(nullDecoded.IsNull);
        Assert.Equal(new byte[] { 0x00 }, Assert.Single(nullChunks));

        await AssertRowFailureAsync(
            "utf8",
            () => TransferV3RowCodec.EncodeTextFieldAsync(
                required,
                false,
                3,
                40,
                Chunks([0xe2], [0x28, 0xa1]),
                (_, _) => ValueTask.CompletedTask,
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());
        await AssertRowFailureAsync(
            "utf8",
            () => TransferV3RowCodec.DecodeTextFieldAsync(
                required,
                4,
                40,
                Chunks([0x01, 0xe2], [0x28, 0xa1]),
                (_, _) => ValueTask.CompletedTask,
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());
        await AssertRowFailureAsync(
            "field-length",
            () => TransferV3RowCodec.EncodeTextFieldAsync(
                required,
                false,
                2,
                40,
                Chunks([(byte)'a']),
                (_, _) => ValueTask.CompletedTask,
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());
        await AssertRowFailureAsync(
            "null-shape",
            () => TransferV3RowCodec.DecodeTextFieldAsync(
                required,
                1,
                40,
                Chunks([0x00]),
                (_, _) => ValueTask.CompletedTask,
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public void IncrementalTextDecoder_CanBeDrivenDirectlyByASynchronousFrameObserver()
    {
        var column = Column(TransferV3ColumnKind.Text);
        var metrics = new TransferV3FieldStreamMetrics();
        var decoder = TransferV3RowCodec.CreateTextFieldDecoder(
            column,
            maxFieldBytes: 40,
            metrics: metrics);

        var firstPayload = decoder.Append(new byte[] { 0x01, 0xe2 }, CancellationToken.None);
        var secondPayload = decoder.Append(new byte[] { 0x82, 0xac }, CancellationToken.None);
        var result = decoder.Complete(CancellationToken.None);

        Assert.Equal(new byte[] { 0xe2 }, firstPayload.ToArray());
        Assert.Equal(new byte[] { 0x82, 0xac }, secondPayload.ToArray());
        Assert.False(result.IsNull);
        Assert.Equal(4, result.EncodedBytes);
        Assert.Equal(3, result.PayloadBytes);
        Assert.Equal(2, result.Chunks);
        Assert.Equal(4, metrics.BytesRead);
        Assert.True(metrics.MaxOwnedBufferBytesObserved <= 4);
        AssertRowFailure(
            "field-state",
            () => decoder.Append(new byte[] { (byte)'x' }, CancellationToken.None));

        var limited = TransferV3RowCodec.CreateTextFieldDecoder(column, 2, metrics);
        AssertRowFailure(
            "field-size",
            () => limited.Append(new byte[] { 0x01, (byte)'a', (byte)'b' }, CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        var cancelledDecoder = TransferV3RowCodec.CreateTextFieldDecoder(column, 40, metrics);
        cancelled.Cancel();
        var cancellation = Assert.Throws<OperationCanceledException>(() =>
            cancelledDecoder.Append(new byte[] { 0x01 }, cancelled.Token));
        Assert.Equal(cancelled.Token, cancellation.CancellationToken);
        AssertRowFailure(
            "field-state",
            () => cancelledDecoder.Append(new byte[] { 0x01 }, CancellationToken.None));
    }

    [Fact]
    public async Task StreamingTextCodec_CancelsAGiantLogicalFieldWithoutGiantAllocation()
    {
        var column = Column(TransferV3ColumnKind.Text);
        using var cancellation = new CancellationTokenSource();
        var metrics = new TransferV3FieldStreamMetrics();
        var chunksRead = 0;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TransferV3RowCodec.EncodeTextFieldAsync(
                column,
                isNull: false,
                payloadBytes: TransferV3Limits.MaxAllowedFieldBytes - 1,
                maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes,
                payloadChunks: CancellingGiantChunks(cancellation, () => chunksRead++),
                writeChunkAsync: (_, _) => ValueTask.CompletedTask,
                metrics: metrics,
                cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, chunksRead);
        Assert.Equal(2, metrics.ChunksRead);
        Assert.True(metrics.MaxOwnedBufferBytesObserved > 0);
        Assert.True(metrics.MaxOwnedBufferBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(metrics.BytesRead < TransferV3Limits.MaxAllowedFieldBytes);
    }

    [Fact]
    public async Task StreamingTextDecoder_CancelsAGiantLogicalFieldWithoutGiantAllocation()
    {
        var column = Column(TransferV3ColumnKind.Text);
        using var cancellation = new CancellationTokenSource();
        var metrics = new TransferV3FieldStreamMetrics();
        var chunksRead = 0;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TransferV3RowCodec.DecodeTextFieldAsync(
                column,
                encodedBytes: TransferV3Limits.MaxAllowedFieldBytes,
                maxFieldBytes: TransferV3Limits.MaxAllowedFieldBytes,
                encodedChunks: CancellingGiantEncodedChunks(
                    cancellation,
                    () => chunksRead++),
                writePayloadChunkAsync: (_, _) => ValueTask.CompletedTask,
                metrics: metrics,
                cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, chunksRead);
        Assert.Equal(2, metrics.ChunksRead);
        Assert.InRange(metrics.MaxOwnedBufferBytesObserved, 1, 4);
        Assert.True(metrics.MaxReadChunkBytesObserved
            <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(metrics.BytesRead < TransferV3Limits.MaxAllowedFieldBytes);
    }

    [Theory]
    [InlineData(StreamFailurePoint.GetAsyncEnumerator)]
    [InlineData(StreamFailurePoint.Current)]
    [InlineData(StreamFailurePoint.MoveNext)]
    [InlineData(StreamFailurePoint.Dispose)]
    public async Task StreamingTextEncoder_RedactsEverySourceEnumeratorFailure(
        StreamFailurePoint failurePoint)
    {
        var source = new FailingChunkEnumerable(
            failurePoint,
            new byte[] { (byte)'a' });

        await AssertRowFailureAsync(
            "stream-read",
            () => TransferV3RowCodec.EncodeTextFieldAsync(
                Column(TransferV3ColumnKind.Text),
                isNull: false,
                payloadBytes: 1,
                maxFieldBytes: 40,
                payloadChunks: source,
                writeChunkAsync: (_, _) => ValueTask.CompletedTask,
                metrics: new TransferV3FieldStreamMetrics(),
                cancellationToken: CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(StreamFailurePoint.GetAsyncEnumerator)]
    [InlineData(StreamFailurePoint.Current)]
    [InlineData(StreamFailurePoint.MoveNext)]
    [InlineData(StreamFailurePoint.Dispose)]
    public async Task StreamingTextDecoder_RedactsEverySourceEnumeratorFailure(
        StreamFailurePoint failurePoint)
    {
        var source = new FailingChunkEnumerable(
            failurePoint,
            new byte[] { 0x01, (byte)'a' });

        await AssertRowFailureAsync(
            "stream-read",
            () => TransferV3RowCodec.DecodeTextFieldAsync(
                Column(TransferV3ColumnKind.Text),
                encodedBytes: 2,
                maxFieldBytes: 40,
                encodedChunks: source,
                writePayloadChunkAsync: (_, _) => ValueTask.CompletedTask,
                metrics: new TransferV3FieldStreamMetrics(),
                cancellationToken: CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task StreamingTextCodec_RedactsSourceAndSinkFailuresAndWholeValueApisAreBounded()
    {
        var column = Column(TransferV3ColumnKind.Text);
        await AssertRowFailureAsync(
            "stream-read",
            () => TransferV3RowCodec.EncodeTextFieldAsync(
                column,
                false,
                2,
                40,
                FailingChunks(),
                (_, _) => ValueTask.CompletedTask,
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());
        await AssertRowFailureAsync(
            "stream-write",
            () => TransferV3RowCodec.EncodeTextFieldAsync(
                column,
                false,
                1,
                40,
                Chunks([(byte)'a']),
                (_, _) => throw new IOException("private sink path"),
                new TransferV3FieldStreamMetrics(),
                CancellationToken.None).AsTask());

        AssertRowFailure(
            "field-stream-required",
            () => TransferV3RowCodec.EncodeField(
                column,
                new byte[TransferV3Limits.MaxDecodedChunkBytes]));
        AssertRowFailure(
            "field-stream-required",
            () => TransferV3RowCodec.DecodeField(
                column,
                new byte[TransferV3Limits.MaxDecodedChunkBytes + 1]));
    }

    private static object SampleValue(TransferV3ColumnContract column) => column.Kind switch
    {
        TransferV3ColumnKind.Uuid => Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        TransferV3ColumnKind.Boolean => true,
        TransferV3ColumnKind.EnumInt32 => checked((int)column.AllowedIntegers[0]),
        TransferV3ColumnKind.Int32 => -123,
        TransferV3ColumnKind.Int64 => long.MinValue,
        TransferV3ColumnKind.Text => (ReadOnlyMemory<byte>)"sample"u8.ToArray(),
        TransferV3ColumnKind.LocalWallTimestamp =>
            (ReadOnlyMemory<byte>)"2026-07-13 01:02:03.123456"u8.ToArray(),
        TransferV3ColumnKind.Instant => column.InstantEncoding switch
        {
            TransferV3InstantEncoding.UtcTicks => 0L,
            TransferV3InstantEncoding.UnixSeconds => 0L,
            _ => throw new InvalidOperationException(),
        },
        _ => throw new InvalidOperationException(),
    };

    private static void AssertDecodedValue(
        TransferV3ColumnContract column,
        object expected,
        object? actual)
    {
        if (column.Kind == TransferV3ColumnKind.Text)
        {
            Assert.Equal(((ReadOnlyMemory<byte>)expected).ToArray(), Assert.IsType<byte[]>(actual));
            return;
        }

        if (column.Kind == TransferV3ColumnKind.LocalWallTimestamp)
        {
            Assert.Equal(
                new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Unspecified).AddTicks(1_234_560),
                actual);
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static TransferV3ColumnContract Column(
        TransferV3ColumnKind kind,
        bool nullable = false,
        TransferV3InstantEncoding instantEncoding = TransferV3InstantEncoding.None,
        int? maxRunes = null,
        IReadOnlyList<long>? allowedIntegers = null) =>
        new(
            "Synthetic",
            kind is TransferV3ColumnKind.Boolean or TransferV3ColumnKind.EnumInt32
                or TransferV3ColumnKind.Int32 or TransferV3ColumnKind.Int64
                or TransferV3ColumnKind.Instant ? "INTEGER" : "TEXT",
            kind is TransferV3ColumnKind.Boolean or TransferV3ColumnKind.EnumInt32
                or TransferV3ColumnKind.Int32 or TransferV3ColumnKind.Int64
                or TransferV3ColumnKind.Instant ? "integer" : "text",
            nullable,
            kind,
            instantEncoding,
            TransferV3UuidRole.None,
            maxRunes,
            allowedIntegers ?? []);

    private static Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> Capture(
        ICollection<byte[]> destination) =>
        (chunk, _) =>
        {
            destination.Add(chunk.ToArray());
            return ValueTask.CompletedTask;
        };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(
        params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> CancellingGiantChunks(
        CancellationTokenSource cancellation,
        Action observed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunk = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        for (var index = 0; index < 2; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed();
            yield return chunk;
            await Task.Yield();
        }

        cancellation.Cancel();
        yield break;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> CancellingGiantEncodedChunks(
        CancellationTokenSource cancellation,
        Action observed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        first[0] = 0x01;
        var subsequent = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        foreach (var chunk in new[] { first, subsequent })
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed();
            yield return chunk;
            await Task.Yield();
        }

        cancellation.Cancel();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> FailingChunks()
    {
        yield return new byte[] { (byte)'a' };
        await Task.Yield();
        throw new IOException("private source path");
    }

    public enum StreamFailurePoint
    {
        GetAsyncEnumerator,
        Current,
        MoveNext,
        Dispose,
    }

    private sealed class FailingChunkEnumerable(
        StreamFailurePoint failurePoint,
        ReadOnlyMemory<byte> chunk) :
        IAsyncEnumerable<ReadOnlyMemory<byte>>,
        IAsyncEnumerator<ReadOnlyMemory<byte>>
    {
        private bool _yielded;

        public ReadOnlyMemory<byte> Current =>
            failurePoint == StreamFailurePoint.Current
                ? throw new IOException("private current path")
                : chunk;

        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) =>
            failurePoint == StreamFailurePoint.GetAsyncEnumerator
                ? throw new IOException("private get-enumerator path")
                : this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (failurePoint == StreamFailurePoint.MoveNext)
            {
                return ValueTask.FromException<bool>(
                    new IOException("private move-next path"));
            }

            if (_yielded)
            {
                return ValueTask.FromResult(false);
            }

            _yielded = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() =>
            failurePoint == StreamFailurePoint.Dispose
                ? ValueTask.FromException(new IOException("private dispose path"))
                : ValueTask.CompletedTask;
    }

    private static void AssertRowFailure(string code, Action action)
    {
        var exception = Assert.Throws<TransferV3RowFormatException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal($"Transfer-v3 row field rejected ({code}).", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("dead", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("00112233", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertRowFailureAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<TransferV3RowFormatException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal($"Transfer-v3 row field rejected ({code}).", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

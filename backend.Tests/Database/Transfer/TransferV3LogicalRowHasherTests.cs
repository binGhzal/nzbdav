using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3LogicalRowHasherTests
{
    [Fact]
    public void Hash_UsesExplicitTypePresenceLengthAndCanonicalPayloadBytes()
    {
        using var hasher = new TransferV3LogicalRowHasher();
        var uuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var timestamp = new DateTime(2026, 7, 12, 13, 14, 15, 123, DateTimeKind.Unspecified)
            .AddTicks(4560);

        hasher.AppendUuid(uuid);
        hasher.AppendInt32(-2);
        hasher.AppendInt64(long.MinValue);
        hasher.AppendBoolean(true);
        hasher.AppendText("Straße/東京");
        hasher.AppendLocalTimestamp(timestamp);
        hasher.AppendNull(TransferV3LogicalType.Text);

        var expectedBytes = new List<byte>();
        Span<byte> uuidBytes = stackalloc byte[16];
        uuid.TryWriteBytes(uuidBytes, bigEndian: true, out _);
        AppendExpected(expectedBytes, TransferV3LogicalType.Uuid, uuidBytes);
        Span<byte> int32Bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(int32Bytes, -2);
        AppendExpected(expectedBytes, TransferV3LogicalType.Int32, int32Bytes);
        Span<byte> int64Bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(int64Bytes, long.MinValue);
        AppendExpected(expectedBytes, TransferV3LogicalType.Int64, int64Bytes);
        AppendExpected(expectedBytes, TransferV3LogicalType.Boolean, [0x01]);
        AppendExpected(
            expectedBytes,
            TransferV3LogicalType.Text,
            new UTF8Encoding(false, true).GetBytes("Straße/東京"));
        AppendExpected(
            expectedBytes,
            TransferV3LogicalType.LocalWallTimestamp,
            Encoding.ASCII.GetBytes("2026-07-12T13:14:15.123456"));
        AppendExpectedNull(expectedBytes, TransferV3LogicalType.Text);

        Assert.Equal(SHA256.HashData([.. expectedBytes]), hasher.GetHashAndReset());
    }

    [Fact]
    public void Hash_DistinguishesTypesNullsAndFieldBoundaries()
    {
        var int32 = Hash(hasher => hasher.AppendInt32(1));
        var int64 = Hash(hasher => hasher.AppendInt64(1));
        var nullText = Hash(hasher => hasher.AppendNull(TransferV3LogicalType.Text));
        var nullUuid = Hash(hasher => hasher.AppendNull(TransferV3LogicalType.Uuid));
        var oneText = Hash(hasher => hasher.AppendText("ab"));
        var twoTexts = Hash(hasher =>
        {
            hasher.AppendText("a");
            hasher.AppendText("b");
        });

        AssertDistinct(int32, int64, nullText, nullUuid, oneText, twoTexts);
    }

    [Fact]
    public void StreamingTextHash_IsIndependentOfTransportChunkBoundaries()
    {
        const string value = "zero/é/e\u0301/東京/😀/end";
        var utf8 = new UTF8Encoding(false, true).GetBytes(value);
        var expected = Hash(hasher => hasher.AppendText(value));

        using var chunked = new TransferV3LogicalRowHasher();
        chunked.BeginText(utf8.Length);
        foreach (var singleByte in utf8)
        {
            chunked.AppendTextUtf8Chunk([singleByte]);
        }

        chunked.EndText();

        Assert.Equal(expected, chunked.GetHashAndReset());
        Assert.True(chunked.PeakScratchBufferBytes <= TransferV3LogicalRowHasher.ScratchBufferBytes);
    }

    [Fact]
    public void StreamingText_RejectsInvalidUtf8NulAndLengthMismatch()
    {
        using var invalid = new TransferV3LogicalRowHasher();
        invalid.BeginText(2);
        invalid.AppendTextUtf8Chunk([0xc3]);
        Assert.Throws<ArgumentException>(() => invalid.AppendTextUtf8Chunk([0x28]));

        using var nul = new TransferV3LogicalRowHasher();
        nul.BeginText(1);
        Assert.Throws<ArgumentException>(() => nul.AppendTextUtf8Chunk([0x00]));

        using var shortValue = new TransferV3LogicalRowHasher();
        shortValue.BeginText(2);
        shortValue.AppendTextUtf8Chunk([(byte)'a']);
        Assert.Throws<InvalidOperationException>(() => shortValue.EndText());
        Assert.Throws<InvalidOperationException>(() => shortValue.GetHashAndReset());
    }

    [Fact]
    public void LocalTimestamp_IsExactOffsetFreeMicrosecondText()
    {
        var value = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("0001-01-01T00:00:00.000000", TransferV3LocalTimestamp.Format(value));
        Assert.Equal(value, TransferV3LocalTimestamp.Parse("0001-01-01T00:00:00.000000"));
        Assert.Equal(DateTimeKind.Unspecified, TransferV3LocalTimestamp.Parse(
            "9999-12-31T23:59:59.999999").Kind);
    }

    [Fact]
    public void LocalTimestamp_RejectsKindPrecisionOffsetsAndRangeMismatch()
    {
        Assert.Throws<ArgumentException>(() => TransferV3LocalTimestamp.Format(DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => TransferV3LocalTimestamp.Format(DateTime.Now));
        Assert.Throws<ArgumentException>(() => TransferV3LocalTimestamp.Format(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(1)));

        foreach (var invalid in new[]
                 {
                     "2026-01-01T00:00:00",
                     "2026-01-01T00:00:00.000000Z",
                     "2026-01-01T00:00:00.000000+04:00",
                     "2026-02-30T00:00:00.000000",
                     "0000-01-01T00:00:00.000000",
                     "2026-01-01t00:00:00.000000",
                     "2026-01-01T00:00:00.00000",
                 })
        {
            Assert.Throws<FormatException>(() => TransferV3LocalTimestamp.Parse(invalid));
        }
    }

    [Fact]
    public void AppendText_StreamsLargeValuesThroughFixedScratchBuffer()
    {
        var value = string.Concat(Enumerable.Repeat("éx", 2_000_000));
        using var hasher = new TransferV3LogicalRowHasher();

        hasher.AppendText(value);
        var digest = hasher.GetHashAndReset();

        Assert.Equal(32, digest.Length);
        Assert.True(hasher.PeakScratchBufferBytes <= TransferV3LogicalRowHasher.ScratchBufferBytes);
    }

    private static byte[] Hash(Action<TransferV3LogicalRowHasher> append)
    {
        using var hasher = new TransferV3LogicalRowHasher();
        append(hasher);
        return hasher.GetHashAndReset();
    }

    private static void AppendExpected(
        List<byte> bytes,
        TransferV3LogicalType type,
        ReadOnlySpan<byte> payload)
    {
        bytes.Add((byte)type);
        bytes.Add(0x01);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)payload.Length));
        bytes.AddRange(length.ToArray());
        bytes.AddRange(payload.ToArray());
    }

    private static void AppendExpectedNull(List<byte> bytes, TransferV3LogicalType type)
    {
        bytes.Add((byte)type);
        bytes.Add(0x00);
        bytes.AddRange(new byte[8]);
    }

    private static void AssertDistinct(params byte[][] values)
    {
        Assert.Equal(values.Length, values.Select(Convert.ToHexString).Distinct().Count());
    }
}

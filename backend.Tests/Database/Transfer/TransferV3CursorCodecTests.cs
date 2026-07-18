using System.Buffers.Binary;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3CursorCodecTests
{
    [Fact]
    public void Encode_UsesVersionedRfc4122NetworkOrderBytes()
    {
        var cursor = TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromGuid(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));

        Assert.Equal("AQEBAAAAEAARIjNEVWZ3iJmqu8zd7v8", cursor);
        Assert.DoesNotContain('=', cursor);
    }

    [Fact]
    public void RoundTrip_PreservesEveryCanonicalComponentType()
    {
        var expected = new[]
        {
            TransferV3CursorComponent.FromGuid(Guid.Empty),
            TransferV3CursorComponent.FromGuid(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            TransferV3CursorComponent.FromInt64(long.MinValue),
            TransferV3CursorComponent.FromInt64(-1),
            TransferV3CursorComponent.FromInt64(0),
            TransferV3CursorComponent.FromInt64(long.MaxValue),
            TransferV3CursorComponent.FromText(string.Empty),
            TransferV3CursorComponent.FromText("A"),
            TransferV3CursorComponent.FromText("Straße/東京/😀"),
            TransferV3CursorComponent.FromText("é"),
            TransferV3CursorComponent.FromText("e\u0301"),
        };

        var decoded = TransferV3CursorCodec.Decode(TransferV3CursorCodec.Encode(expected));

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Compare_UsesCanonicalComponentOrdering_NotBase64TextOrdering()
    {
        AssertCursorOrder(
            TransferV3CursorComponent.FromInt64(long.MinValue),
            TransferV3CursorComponent.FromInt64(-1),
            TransferV3CursorComponent.FromInt64(0),
            TransferV3CursorComponent.FromInt64(1),
            TransferV3CursorComponent.FromInt64(long.MaxValue));

        AssertCursorOrder(
            TransferV3CursorComponent.FromGuid(Guid.Parse("00112233-0000-0000-0000-000000000000")),
            TransferV3CursorComponent.FromGuid(Guid.Parse("01000000-0000-0000-0000-000000000000")),
            TransferV3CursorComponent.FromGuid(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")));

        AssertCursorOrder(
            TransferV3CursorComponent.FromText("A"),
            TransferV3CursorComponent.FromText("a"),
            TransferV3CursorComponent.FromText("aa"),
            TransferV3CursorComponent.FromText("e\u0301"),
            TransferV3CursorComponent.FromText("z"),
            TransferV3CursorComponent.FromText("é"));
    }

    [Fact]
    public void Compare_UsesLaterComponentsWhenPrefixesMatch()
    {
        var left = TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromText("same"),
            TransferV3CursorComponent.FromInt64(-1));
        var right = TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromText("same"),
            TransferV3CursorComponent.FromInt64(0));

        Assert.True(TransferV3CursorCodec.Compare(left, right) < 0);
        Assert.True(TransferV3CursorCodec.Compare(right, left) > 0);
        Assert.Equal(0, TransferV3CursorCodec.Compare(left, left));
    }

    [Theory]
    [MemberData(nameof(MalformedCursors))]
    public void Decode_RejectsMalformedOrNoncanonicalInput(string value)
    {
        Assert.Throws<FormatException>(() => TransferV3CursorCodec.Decode(value));
    }

    [Fact]
    public void Encode_RejectsEmptyOversizedAndUnsafeComponents()
    {
        Assert.Throws<ArgumentException>(() => TransferV3CursorCodec.Encode([]));
        Assert.Throws<ArgumentException>(() => TransferV3CursorCodec.Encode(
            Enumerable.Range(0, TransferV3CursorCodec.MaxComponentCount + 1)
                .Select(i => TransferV3CursorComponent.FromInt64(i))
                .ToArray()));
        Assert.Throws<ArgumentException>(() => TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromText("before\0after")));
        Assert.Throws<ArgumentException>(() => TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromText(
                new string('x', TransferV3CursorCodec.MaxTextBytes + 1))));
    }

    [Fact]
    public void Decode_RejectsDeclaredLengthBeforeAllocatingPayload()
    {
        var bytes = new byte[7];
        bytes[0] = TransferV3CursorCodec.FormatVersion;
        bytes[1] = 1;
        bytes[2] = (byte)TransferV3CursorComponentType.Text;
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(3),
            TransferV3CursorCodec.MaxTextBytes + 1u);

        Assert.Throws<FormatException>(() =>
            TransferV3CursorCodec.Decode(ToBase64Url(bytes)));
    }

    [Fact]
    public void Decode_FuzzCorpusNeverAcceptsCorruptCanonicalCursor()
    {
        var valid = TransferV3CursorCodec.Encode(
            TransferV3CursorComponent.FromGuid(Guid.Parse("12345678-1234-5678-9abc-def012345678")),
            TransferV3CursorComponent.FromInt64(-42),
            TransferV3CursorComponent.FromText("fuzz"));

        for (var i = 0; i < valid.Length; i++)
        {
            var replacement = valid[i] == 'A' ? 'B' : 'A';
            var corrupt = valid[..i] + replacement + valid[(i + 1)..];

            try
            {
                var decoded = TransferV3CursorCodec.Decode(corrupt);
                Assert.NotEqual(
                    TransferV3CursorCodec.Decode(valid),
                    decoded);
            }
            catch (FormatException)
            {
                // A structural rejection is also correct for a corrupt cursor.
            }
        }
    }

    public static IEnumerable<object[]> MalformedCursors()
    {
        yield return [""];
        yield return ["="];
        yield return ["AQ=="];
        yield return ["AQ+"];
        yield return ["AQ/"];
        yield return ["A"];
        yield return ["_x"]; // Decodes, but has non-zero unused bits and is noncanonical.
        yield return [ToBase64Url([0x02, 0x01])]; // Unknown version.
        yield return [ToBase64Url([0x01, 0x00])]; // Empty component count.
        yield return [ToBase64Url([0x01, 0x01, 0xff, 0, 0, 0, 0])]; // Unknown type.
        yield return [ToBase64Url([0x01, 0x01, 0x01, 0, 0, 0, 15, .. new byte[15]])];
        yield return [ToBase64Url([0x01, 0x01, 0x02, 0, 0, 0, 7, .. new byte[7]])];
        yield return [ToBase64Url([0x01, 0x01, 0x03, 0, 0, 0, 2, 0xc3, 0x28])];
        yield return [ToBase64Url([0x01, 0x01, 0x03, 0, 0, 0, 3, (byte)'a', 0, (byte)'b'])];
        yield return [ToBase64Url([0x01, 0x01, 0x03, 0, 0, 0, 1])]; // Truncated.
        yield return [ToBase64Url([0x01, 0x01, 0x03, 0, 0, 0, 1, (byte)'a', 0])]; // Trailing data.
        yield return [new string('A', TransferV3CursorCodec.MaxEncodedChars + 1)];
    }

    private static void AssertCursorOrder(params TransferV3CursorComponent[] components)
    {
        for (var i = 0; i < components.Length - 1; i++)
        {
            var left = TransferV3CursorCodec.Encode(components[i]);
            var right = TransferV3CursorCodec.Encode(components[i + 1]);
            Assert.True(
                TransferV3CursorCodec.Compare(left, right) < 0,
                $"Expected {components[i]} before {components[i + 1]}");
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

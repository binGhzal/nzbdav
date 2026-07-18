using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ParserBufferHardeningTests
{
    [Fact]
    public async Task Parser_ClearsDecodedRowAndFieldPayloadAliasesAfterSuccessfulDispatch()
    {
        var limits = new TransferV3Limits(1024);
        var rowPayload = "row-payload"u8.ToArray();
        var fieldPayload = "field-payload"u8.ToArray();
        await using var stream = new MemoryStream(
            await WriteInlineAndChunkedRowsAsync(limits, rowPayload, fieldPayload),
            writable: false);
        var observer = new AliasRetainingObserver();

        await TransferV3JsonlParser.ParseAsync(stream, limits, observer);

        Assert.Collection(
            observer.PayloadCopies,
            actual => Assert.Equal(rowPayload, actual),
            actual => Assert.Equal(fieldPayload, actual));
        Assert.All(observer.PayloadAliases, AssertZeroed);
    }

    [Fact]
    public async Task Parser_ClearsDecodedPayloadAliasWhenObserverRejectsTheFrame()
    {
        var limits = new TransferV3Limits(1024);
        var expected = "failure-payload"u8.ToArray();
        await using var stream = new MemoryStream(
            await WriteInlineRowAsync(limits, expected),
            writable: false);
        var observer = new AliasRetainingObserver(rejectPayload: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer));

        Assert.Equal("Injected observer rejection.", failure.Message);
        Assert.Equal(expected, observer.PayloadCopies.Single());
        Assert.All(observer.PayloadAliases, AssertZeroed);
        Assert.Equal(1, observer.Aborts);
    }

    [Fact]
    public async Task Utf8LineReader_ClearsConsumedBytesAndItsEntireReadBufferOnDispose()
    {
        await using var stream = new MemoryStream("first-secret\nsecond-secret\n"u8.ToArray());
        var reader = new TransferV3Utf8LineReader(stream, maxLineBytes: 1024);
        var readBuffer = (byte[])typeof(TransferV3Utf8LineReader)
            .GetField("_readBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader)!;
        var lineBuffer = (ArrayBufferWriter<byte>)typeof(TransferV3Utf8LineReader)
            .GetField("_lineBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader)!;

        var first = await reader.ReadLineAsync(CancellationToken.None);

        Assert.Equal("first-secret"u8.ToArray(), first);
        Assert.All(readBuffer.AsSpan(0, "first-secret\n"u8.Length).ToArray(), value =>
            Assert.Equal((byte)0, value));
        Assert.Contains(readBuffer, value => value != 0);
        Assert.True(MemoryMarshal.TryGetArray(
            lineBuffer.WrittenMemory,
            out ArraySegment<byte> lineBufferSegment));
        Assert.NotNull(lineBufferSegment.Array);
        AssertZeroed(lineBufferSegment.Array!);

        reader.Dispose();

        AssertZeroed(readBuffer);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            reader.ReadLineAsync(CancellationToken.None).AsTask());
        CryptographicOperations.ZeroMemory(first!);
    }

    [Fact]
    public void FrameCodec_ClearDecodedPayloadErasesRowAndFieldAliases()
    {
        var rowAlias = "row-secret"u8.ToArray();
        var fieldAlias = "field-secret"u8.ToArray();

        TransferV3FrameCodec.ClearDecodedPayload(new TransferV3RowFrame(
            "Items",
            Cursor(1),
            rowAlias));
        TransferV3FrameCodec.ClearDecodedPayload(new TransferV3FieldChunkFrame(
            "Items",
            Cursor(2),
            0,
            0,
            fieldAlias));

        AssertZeroed(rowAlias);
        AssertZeroed(fieldAlias);
    }

    [Theory]
    [InlineData("AR")]
    [InlineData("AQ==")]
    [InlineData("+Q")]
    public void FrameCodec_RejectsNoncanonicalPayloadBeforeItCanEscape(string payload)
    {
        var line = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"frame\":\"row\",\"table\":\"Items\",\"cursor\":\"{Cursor(1)}\",\"data\":\"{payload}\"}}");

        var failure = Assert.Throws<FormatException>(() =>
            TransferV3FrameCodec.ParseCanonical(line));

        Assert.Equal("The frame payload is not canonical Base64URL.", failure.Message);
        CryptographicOperations.ZeroMemory(line);
    }

    [Fact]
    public void ProductionSources_KeepErasureFinallyBlocksAtEveryParserOwnershipBoundary()
    {
        var root = FindRepositoryRoot();
        var lineReader = File.ReadAllText(Path.Combine(
            root,
            "backend",
            "Database",
            "Transfer",
            "TransferV3Utf8LineReader.cs"));
        var parser = File.ReadAllText(Path.Combine(
            root,
            "backend",
            "Database",
            "Transfer",
            "TransferV3JsonlParser.cs"));
        var codec = File.ReadAllText(Path.Combine(
            root,
            "backend",
            "Database",
            "Transfer",
            "TransferV3FrameCodec.cs"));

        Assert.Contains("finally", lineReader, StringComparison.Ordinal);
        Assert.Contains("ClearWriter(_lineBuffer)", lineReader, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(_readBuffer)", lineReader, StringComparison.Ordinal);
        Assert.Contains("using var lineReader", parser, StringComparison.Ordinal);
        Assert.Contains("ClearDecodedPayload(frame)", parser, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(line)", parser, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(trailing)", parser, StringComparison.Ordinal);
        Assert.Contains("if (!accepted)", codec, StringComparison.Ordinal);
        Assert.Contains("ClearDecodedPayload(frame)", codec, StringComparison.Ordinal);
        Assert.True(
            parser.IndexOf("state.AcceptParsed(frame, line)", StringComparison.Ordinal)
            < parser.IndexOf("ClearDecodedPayload(frame)", StringComparison.Ordinal));
        Assert.True(
            codec.IndexOf("if (!IsCanonicalBase64Url(value))", StringComparison.Ordinal)
            < codec.IndexOf("TransferV3CursorCodec.DecodeBase64Url(value)", StringComparison.Ordinal));
    }

    private static async Task<byte[]> WriteInlineRowAsync(
        TransferV3Limits limits,
        ReadOnlyMemory<byte> payload)
    {
        await using var stream = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(stream, "Items", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.WriteRowAsync(Cursor(1), payload);
        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static async Task<byte[]> WriteInlineAndChunkedRowsAsync(
        TransferV3Limits limits,
        ReadOnlyMemory<byte> rowPayload,
        ReadOnlyMemory<byte> fieldPayload)
    {
        await using var stream = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(stream, "Items", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.WriteRowAsync(Cursor(1), rowPayload);
        await writer.StartChunkedRowAsync(Cursor(2), fieldCount: 1);
        await writer.WriteFieldChunkAsync(fieldIndex: 0, fieldPayload);
        await writer.EndChunkedRowAsync();
        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static string Cursor(long value) => TransferV3CursorCodec.Encode(
        TransferV3CursorComponent.FromInt64(value));

    private static void AssertZeroed(byte[] bytes) =>
        Assert.All(bytes, value => Assert.Equal((byte)0, value));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "NzbWebDAV.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the NZBDav repository root above '{AppContext.BaseDirectory}'.");
    }

    private sealed class AliasRetainingObserver(bool rejectPayload = false)
        : ITransferV3FrameObserver
    {
        internal List<byte[]> PayloadAliases { get; } = [];

        internal List<byte[]> PayloadCopies { get; } = [];

        internal int Aborts { get; private set; }

        public void Observe(TransferV3Frame frame)
        {
            ReadOnlyMemory<byte> payload = frame switch
            {
                TransferV3RowFrame row => row.Data,
                TransferV3FieldChunkFrame chunk => chunk.Data,
                _ => default,
            };
            if (payload.IsEmpty)
                return;

            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                payload,
                out ArraySegment<byte> segment));
            Assert.Equal(0, segment.Offset);
            Assert.Equal(segment.Array!.Length, segment.Count);
            PayloadAliases.Add(segment.Array);
            PayloadCopies.Add(payload.ToArray());
            if (rejectPayload)
                throw new InvalidOperationException("Injected observer rejection.");
        }

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
        }

        public void Abort(Exception failure) => Aborts++;
    }
}

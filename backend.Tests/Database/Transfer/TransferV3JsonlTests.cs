using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3JsonlTests
{
    [Fact]
    public async Task Writer_EndOperationsReturnTheExactFramesWrittenToTheStream()
    {
        var limits = new TransferV3Limits(maxFieldBytes: 1024);
        await using var stream = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(stream, "DavItems", limits);

        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.WriteRowAsync(Cursor(1), new byte[] { 0x2a });
        var batchEnd = await writer.EndBatchAsync();
        var tableEnd = await writer.EndTableAsync();

        var lines = Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(TransferV3FrameCodec.Serialize(batchEnd), Encoding.UTF8.GetBytes(lines[^2]));
        Assert.Equal(TransferV3FrameCodec.Serialize(tableEnd), Encoding.UTF8.GetBytes(lines[^1]));
        Assert.Equal(1, batchEnd.Rows);
        Assert.Equal(1, batchEnd.Bytes);
        Assert.Equal(1, tableEnd.Batches);
        Assert.Equal(1, tableEnd.Rows);
        Assert.Equal(1, tableEnd.Bytes);
    }

    [Fact]
    public async Task WriterAndParser_RoundTripCanonicalFramesInFixedPropertyOrder()
    {
        var limits = new TransferV3Limits(maxFieldBytes: 2 * 1024 * 1024);
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "DavItems", limits);

        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, after: null);
        await writer.WriteRowAsync(Cursor(1), "hello"u8.ToArray());
        await writer.EndBatchAsync();
        await writer.EndTableAsync();

        var text = Encoding.UTF8.GetString(stream.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "{\"frame\":\"table\",\"version\":3,\"table\":\"DavItems\"}",
            lines[0]);
        Assert.Equal(
            "{\"frame\":\"batch-start\",\"table\":\"DavItems\",\"batch\":0,\"after\":null}",
            lines[1]);
        Assert.Equal(
            $"{{\"frame\":\"row\",\"table\":\"DavItems\",\"cursor\":\"{Cursor(1)}\",\"data\":\"aGVsbG8\"}}",
            lines[2]);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=\"", text, StringComparison.Ordinal);

        stream.Position = 0;
        var observer = new RecordingObserver();
        var metrics = await TransferV3JsonlParser.ParseAsync(stream, limits, observer);

        Assert.Collection(
            observer.Frames,
            frame => Assert.IsType<TransferV3TableHeaderFrame>(frame),
            frame => Assert.IsType<TransferV3BatchStartFrame>(frame),
            frame => Assert.Equal("hello"u8.ToArray(), Assert.IsType<TransferV3RowFrame>(frame).Data),
            frame => Assert.IsType<TransferV3BatchEndFrame>(frame),
            frame => Assert.IsType<TransferV3TableEndFrame>(frame));
        Assert.Equal(1, metrics.MaxFramesDispatchedConcurrently);
        Assert.True(metrics.MaxAccountedBytesPerDispatch <= limits.MaxAccountedBytesPerDispatch);
    }

    [Fact]
    public async Task Digests_CoverExactCanonicalLinesIncludingLfWithoutSelfReference()
    {
        var limits = new TransferV3Limits(maxFieldBytes: 1024);
        var text = Encoding.UTF8.GetString(await WriteTwoBatchesAsync(limits));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(8, lines.Length);
        Assert.Equal("batch-end", FrameKind(lines[3]));
        Assert.Equal("batch-end", FrameKind(lines[6]));
        Assert.Equal("table-end", FrameKind(lines[7]));

        Assert.Equal(HashCanonicalLines(lines[1], lines[2]), Digest(lines[3]));
        Assert.Equal(HashCanonicalLines(lines[4], lines[5]), Digest(lines[6]));
        Assert.Equal(
            HashCanonicalLines(lines[0], lines[1], lines[2], lines[3], lines[4], lines[5], lines[6]),
            Digest(lines[7]));
    }

    [Fact]
    public async Task Parser_StreamsMultipleBatchesWithoutBufferingTheirRows()
    {
        var limits = new TransferV3Limits(
            maxFieldBytes: 1024 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 1024);
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "QueueItems", limits);
        await writer.WriteTableHeaderAsync();

        for (var batch = 0; batch < 3; batch++)
        {
            await writer.StartBatchAsync(batch, batch == 0 ? null : Cursor(batch * 2));
            await writer.WriteRowAsync(Cursor(batch * 2 + 1), new byte[] { (byte)(batch * 2 + 1) });
            await writer.WriteRowAsync(Cursor(batch * 2 + 2), new byte[] { (byte)(batch * 2 + 2) });
            await writer.EndBatchAsync();
        }

        await writer.EndTableAsync();
        stream.Position = 0;
        var observer = new CountingObserver();

        var metrics = await TransferV3JsonlParser.ParseAsync(stream, limits, observer);

        Assert.Equal(6, observer.Rows);
        Assert.Equal(6, observer.PayloadBytes);
        Assert.Equal(1, metrics.MaxFramesDispatchedConcurrently);
        Assert.True(metrics.MaxDecodedPayloadBytesObserved <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(metrics.MaxAccountedBytesPerDispatch <= limits.MaxAccountedBytesPerDispatch);
    }

    [Fact]
    public async Task ChunkedGiantField_UsesOneMiBChunksAndSingletonOverBudgetBatch()
    {
        var limits = new TransferV3Limits(
            maxFieldBytes: 3 * 1024 * 1024,
            maxBatchRows: 1000,
            maxBatchBytes: 1024 * 1024);
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "QueueNzbContents", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.StartChunkedRowAsync(Cursor(1), fieldCount: 2);
        await writer.WriteFieldChunkAsync(fieldIndex: 0, ReadOnlyMemory<byte>.Empty);
        await writer.WriteFieldChunkAsync(fieldIndex: 1, new byte[TransferV3Limits.MaxDecodedChunkBytes]);
        await writer.WriteFieldChunkAsync(fieldIndex: 1, new byte[TransferV3Limits.MaxDecodedChunkBytes]);
        await writer.WriteFieldChunkAsync(fieldIndex: 1, new byte[127]);
        await writer.EndChunkedRowAsync();
        await writer.EndBatchAsync();
        await writer.EndTableAsync();

        stream.Position = 0;
        var observer = new CountingObserver();
        var metrics = await TransferV3JsonlParser.ParseAsync(stream, limits, observer);

        Assert.Equal(1, observer.Rows);
        Assert.Equal(2L * TransferV3Limits.MaxDecodedChunkBytes + 127, observer.PayloadBytes);
        Assert.Equal(TransferV3Limits.MaxDecodedChunkBytes, observer.MaxPayloadBytes);
        Assert.Equal(1, metrics.MaxFramesDispatchedConcurrently);
        Assert.True(metrics.MaxDecodedPayloadBytesObserved <= TransferV3Limits.MaxDecodedChunkBytes);
        Assert.True(metrics.MaxAccountedBytesPerDispatch <= limits.MaxAccountedBytesPerDispatch);
    }

    [Fact]
    public async Task Writer_RejectsCursorChunkFieldAndBudgetViolations()
    {
        var limits = new TransferV3Limits(maxFieldBytes: 20, maxBatchRows: 1, maxBatchBytes: 10);
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "Items", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.WriteRowAsync(Cursor(2), new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteRowAsync(Cursor(2), new byte[] { 2 }).AsTask());

        await using var cursorStream = new MemoryStream();
        await using var cursorWriter = new TransferV3JsonlWriter(
            cursorStream,
            "Items",
            new TransferV3Limits(maxFieldBytes: 20, maxBatchRows: 2, maxBatchBytes: 10));
        await cursorWriter.WriteTableHeaderAsync();
        await cursorWriter.StartBatchAsync(0, null);
        await cursorWriter.WriteRowAsync(Cursor(2), new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cursorWriter.WriteRowAsync(Cursor(1), new byte[] { 2 }).AsTask());

        await using var chunkStream = new MemoryStream();
        var chunkWriter = new TransferV3JsonlWriter(chunkStream, "Items", limits);
        await chunkWriter.WriteTableHeaderAsync();
        await chunkWriter.StartBatchAsync(0, null);
        await chunkWriter.StartChunkedRowAsync(Cursor(1), 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chunkWriter.WriteFieldChunkAsync(1, new byte[] { 1 }).AsTask());

        await using var shortChunkStream = new MemoryStream();
        await using var shortChunkWriter = new TransferV3JsonlWriter(
            shortChunkStream,
            "Items",
            limits);
        await shortChunkWriter.WriteTableHeaderAsync();
        await shortChunkWriter.StartBatchAsync(0, null);
        await shortChunkWriter.StartChunkedRowAsync(Cursor(1), 2);
        await shortChunkWriter.WriteFieldChunkAsync(0, new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            shortChunkWriter.WriteFieldChunkAsync(0, new byte[] { 2 }).AsTask());
    }

    [Fact]
    public async Task Parser_RejectsPayloadAndBudgetsBeforeUnboundedBuffering()
    {
        var exportLimits = new TransferV3Limits(maxFieldBytes: 2 * 1024 * 1024);
        var valid = await WriteSingleChunkedRowAsync(
            exportLimits,
            new byte[TransferV3Limits.MaxDecodedChunkBytes]);

        await AssertRejectsAsync(
            valid,
            new TransferV3Limits(maxFieldBytes: TransferV3Limits.MaxDecodedChunkBytes - 1));

        var oversizedEncoded = Encoding.UTF8.GetString(valid)
            .Replace(
                TransferV3CursorCodec.EncodeBase64Url(new byte[TransferV3Limits.MaxDecodedChunkBytes]),
                TransferV3CursorCodec.EncodeBase64Url(new byte[TransferV3Limits.MaxDecodedChunkBytes + 1]),
                StringComparison.Ordinal);
        await AssertRejectsAsync(Encoding.UTF8.GetBytes(oversizedEncoded), exportLimits);

        var normal = await WriteRowsAsync(exportLimits, 2);
        await AssertRejectsAsync(
            normal,
            new TransferV3Limits(maxFieldBytes: 2 * 1024 * 1024, maxBatchRows: 1));
    }

    [Theory]
    [MemberData(nameof(MalformedCanonicalMutations))]
    public async Task Parser_RejectsMalformedNoncanonicalAndTrailingFrames(
        Func<string, string> mutate)
    {
        var limits = new TransferV3Limits(maxFieldBytes: 2 * 1024 * 1024);
        var valid = Encoding.UTF8.GetString(await WriteRowsAsync(limits, 2));

        await AssertRejectsAsync(Encoding.UTF8.GetBytes(mutate(valid)), limits);
    }

    [Fact]
    public async Task Parser_RejectsDuplicateAndNonmonotonicCursorsAndBadChunkSequence()
    {
        var limits = new TransferV3Limits(maxFieldBytes: 2 * 1024 * 1024);
        var validRows = Encoding.UTF8.GetString(await WriteRowsAsync(limits, 2));
        var duplicateCursor = ReplaceNth(validRows, Cursor(2), Cursor(1), occurrence: 1);
        await AssertRejectsAsync(Encoding.UTF8.GetBytes(duplicateCursor), limits);

        var chunked = Encoding.UTF8.GetString(await WriteTwoChunkRowAsync(limits));
        var badChunk = chunked.Replace("\"chunk\":1", "\"chunk\":2", StringComparison.Ordinal);
        await AssertRejectsAsync(Encoding.UTF8.GetBytes(badChunk), limits);

        var emptyNonzeroChunk = chunked.Replace(
            "\"chunk\":1,\"data\":\"AQ\"",
            "\"chunk\":1,\"data\":\"\"",
            StringComparison.Ordinal);
        await AssertRejectsAsync(Encoding.UTF8.GetBytes(emptyNonzeroChunk), limits);
    }

    [Theory]
    [InlineData(
        "{\"frame\":\"table\",\"version\":\"sensitive-version\",\"table\":\"DavItems\"}",
        "The version property must be an Int32.")]
    [InlineData(
        "{\"frame\":\"table-end\",\"table\":\"DavItems\",\"batches\":0,\"rows\":{\"sensitive-row\":\"secret\"},\"bytes\":0,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}",
        "The rows property must be a nonnegative Int64.")]
    public void FrameCodec_RejectsWrongNumericJsonKindsAsRedactedFormatErrors(
        string json,
        string expectedMessage)
    {
        var exception = Assert.Throws<FormatException>(() =>
            TransferV3FrameCodec.ParseCanonical(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("sensitive", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_RequireExplicitSaneFieldAndBatchCeilings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferV3Limits(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferV3Limits(1, maxBatchRows: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferV3Limits(1, maxBatchBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TransferV3Limits(TransferV3Limits.MaxAllowedFieldBytes + 1));
    }

    public static IEnumerable<object[]> MalformedCanonicalMutations()
    {
        yield return [new Func<string, string>(text => text.Replace(
            "\"version\":3,",
            "\"version\":3,\"version\":3,",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"version\":3,",
            "\"unknown\":0,\"version\":3,",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"version\":3,\"table\":\"DavItems\"",
            "\"table\":\"DavItems\",\"version\":3",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"version\":3,",
            string.Empty,
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"table\":\"DavItems\",\"cursor\"",
            "\"table\":\"WrongTable\",\"cursor\"",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"data\":\"AQ\"",
            "\"data\":\"AQ==\"",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"data\":\"AQ\"",
            "\"data\":\"+Q\"",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.Replace(
            "\"rows\":2",
            "\"rows\":3",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => CorruptFirstDigest(text))];
        yield return [new Func<string, string>(text => text + "{}\n")];
        yield return [new Func<string, string>(text => text.Replace(
            "{\"frame\":\"table\"",
            "{ \"frame\":\"table\"",
            StringComparison.Ordinal))];
        yield return [new Func<string, string>(text => text.TrimEnd('\n'))];
    }

    private static async Task<byte[]> WriteRowsAsync(TransferV3Limits limits, int rowCount)
    {
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "DavItems", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        for (var row = 1; row <= rowCount; row++)
        {
            await writer.WriteRowAsync(Cursor(row), new byte[] { (byte)row });
        }

        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static async Task<byte[]> WriteTwoBatchesAsync(TransferV3Limits limits)
    {
        await using var stream = new MemoryStream();
        await using var writer = new TransferV3JsonlWriter(stream, "DavItems", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.WriteRowAsync(Cursor(1), new byte[] { 1 });
        await writer.EndBatchAsync();
        await writer.StartBatchAsync(1, Cursor(1));
        await writer.WriteRowAsync(Cursor(2), new byte[] { 2 });
        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static async Task<byte[]> WriteSingleChunkedRowAsync(
        TransferV3Limits limits,
        byte[] data)
    {
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "QueueNzbContents", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.StartChunkedRowAsync(Cursor(1), 1);
        await writer.WriteFieldChunkAsync(0, data);
        await writer.EndChunkedRowAsync();
        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static async Task<byte[]> WriteTwoChunkRowAsync(TransferV3Limits limits)
    {
        await using var stream = new MemoryStream();
        var writer = new TransferV3JsonlWriter(stream, "QueueNzbContents", limits);
        await writer.WriteTableHeaderAsync();
        await writer.StartBatchAsync(0, null);
        await writer.StartChunkedRowAsync(Cursor(1), 1);
        await writer.WriteFieldChunkAsync(0, new byte[TransferV3Limits.MaxDecodedChunkBytes]);
        await writer.WriteFieldChunkAsync(0, new byte[] { 1 });
        await writer.EndChunkedRowAsync();
        await writer.EndBatchAsync();
        await writer.EndTableAsync();
        return stream.ToArray();
    }

    private static async Task AssertRejectsAsync(byte[] bytes, TransferV3Limits limits)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<FormatException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, new CountingObserver()));
    }

    private static string Cursor(long value) => TransferV3CursorCodec.Encode(
        TransferV3CursorComponent.FromInt64(value));

    private static string FrameKind(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("frame").GetString()!;
    }

    private static string Digest(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("sha256").GetString()!;
    }

    private static string HashCanonicalLines(params string[] lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ReplaceNth(string text, string oldValue, string newValue, int occurrence)
    {
        var start = 0;
        for (var index = 0; index <= occurrence; index++)
        {
            start = text.IndexOf(oldValue, start, StringComparison.Ordinal);
            Assert.True(start >= 0);
            if (index < occurrence)
            {
                start += oldValue.Length;
            }
        }

        return text[..start] + newValue + text[(start + oldValue.Length)..];
    }

    private static string CorruptFirstDigest(string text)
    {
        const string marker = "\"sha256\":\"";
        var position = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.True(position >= marker.Length);
        return text[..position] + "g" + text[(position + 1)..];
    }

    private sealed class RecordingObserver : ITransferV3FrameObserver
    {
        internal List<TransferV3Frame> Frames { get; } = [];

        public void Observe(TransferV3Frame frame) => Frames.Add(RetainPayload(frame));

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd) => Frames.Add(tableEnd);

        public void Abort(Exception failure)
        {
        }

        private static TransferV3Frame RetainPayload(TransferV3Frame frame) => frame switch
        {
            TransferV3RowFrame row => row with { Data = row.Data.ToArray() },
            TransferV3FieldChunkFrame chunk => chunk with { Data = chunk.Data.ToArray() },
            _ => frame,
        };
    }

    private sealed class CountingObserver : ITransferV3FrameObserver
    {
        internal int Rows { get; private set; }
        internal long PayloadBytes { get; private set; }
        internal int MaxPayloadBytes { get; private set; }

        public void Observe(TransferV3Frame frame)
        {
            switch (frame)
            {
                case TransferV3RowFrame row:
                    Rows++;
                    Add(row.Data.Length);
                    break;
                case TransferV3ChunkedRowStartFrame:
                    Rows++;
                    break;
                case TransferV3FieldChunkFrame chunk:
                    Add(chunk.Data.Length);
                    break;
            }
        }

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
        }

        public void Abort(Exception failure)
        {
        }

        private void Add(int bytes)
        {
            PayloadBytes += bytes;
            MaxPayloadBytes = Math.Max(MaxPayloadBytes, bytes);
        }
    }
}

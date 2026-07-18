using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3JsonlParserTransactionTests
{
    [Fact]
    public async Task ValidTable_CommitsEachVerifiedBatchThenCompletesTableExactlyOnce()
    {
        var limits = new TransferV3Limits(1024);
        await using var stream = new MemoryStream(
            await ValidTwoBatchTableAsync(limits),
            writable: false);
        var observer = new TransactionObserver();

        await TransferV3JsonlParser.ParseAsync(stream, limits, observer);

        Assert.Equal(2, observer.BatchCommits);
        Assert.Equal(2, observer.CommittedBatches.Count);
        Assert.All(
            observer.CommittedBatches,
            batch =>
            {
                Assert.IsType<TransferV3BatchStartFrame>(batch[0]);
                Assert.IsType<TransferV3BatchEndFrame>(batch[^1]);
            });
        Assert.Equal(1, observer.TableCompletions);
        Assert.NotNull(observer.CompletedTable);
        Assert.Equal(0, observer.Aborts);
    }

    [Fact]
    public async Task CorruptLaterBatch_LeavesPriorBatchCommittedAndAbortsOnlyCurrentBatch()
    {
        var limits = new TransferV3Limits(1024);
        var valid = Encoding.UTF8.GetString(await ValidTwoBatchTableAsync(limits));
        var corrupt = Encoding.UTF8.GetBytes(CorruptDigest(valid, occurrence: 1));
        await using var stream = new MemoryStream(corrupt, writable: false);
        var observer = new TransactionObserver();

        await Assert.ThrowsAsync<FormatException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer));

        Assert.Equal(1, observer.BatchCommits);
        Assert.Single(observer.CommittedBatches);
        Assert.Contains(
            observer.CommittedBatches[0],
            frame => frame is TransferV3RowFrame row && row.Data.Span.SequenceEqual(new byte[] { 1 }));
        Assert.Contains(observer.StagedAtAbort, frame => frame is TransferV3BatchStartFrame { Batch: 1 });
        Assert.Contains(
            observer.StagedAtAbort,
            frame => frame is TransferV3RowFrame row && row.Data.Span.SequenceEqual(new byte[] { 2 }));
        Assert.Empty(observer.CurrentBatch);
        Assert.Equal(0, observer.TableCompletions);
        Assert.Equal(1, observer.Aborts);
        Assert.IsType<FormatException>(observer.AbortFailure);
    }

    [Fact]
    public async Task CancellationAfterVerifiedLaterBatchEnd_DoesNotPublishCurrentBatch()
    {
        var limits = new TransferV3Limits(1024);
        await using var stream = new MemoryStream(
            await ValidTwoBatchTableAsync(limits),
            writable: false);
        using var cancellation = new CancellationTokenSource();
        var observer = new TransactionObserver(frame =>
        {
            if (frame is TransferV3BatchEndFrame { Batch: 1 })
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer, cancellation.Token));

        Assert.Equal(1, observer.BatchCommits);
        Assert.Single(observer.CommittedBatches);
        Assert.Contains(observer.StagedAtAbort, frame => frame is TransferV3BatchEndFrame { Batch: 1 });
        Assert.Empty(observer.CurrentBatch);
        Assert.Equal(0, observer.TableCompletions);
        Assert.Equal(1, observer.Aborts);
    }

    [Fact]
    public async Task TrailingContent_LeavesVerifiedBatchesCommittedButNeverCompletesTable()
    {
        var limits = new TransferV3Limits(1024);
        var bytes = (await ValidTwoBatchTableAsync(limits)).Concat("{}\n"u8.ToArray()).ToArray();
        await using var stream = new MemoryStream(bytes, writable: false);
        var observer = new TransactionObserver();

        await Assert.ThrowsAsync<FormatException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer));

        Assert.Equal(2, observer.BatchCommits);
        Assert.Equal(2, observer.CommittedBatches.Count);
        Assert.Equal(0, observer.TableCompletions);
        Assert.Null(observer.CompletedTable);
        Assert.Equal(1, observer.Aborts);
        Assert.IsType<FormatException>(observer.AbortFailure);
    }

    [Fact]
    public async Task PreCanceledParse_AbortsBeforeReadingOrObservingAFrame()
    {
        var limits = new TransferV3Limits(1024);
        await using var stream = new MemoryStream(
            await ValidTwoBatchTableAsync(limits),
            writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observer = new TransactionObserver();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer, cancellation.Token));

        Assert.Empty(observer.ObservedFrames);
        Assert.Equal(0, observer.BatchCommits);
        Assert.Equal(0, observer.TableCompletions);
        Assert.Equal(1, observer.Aborts);
    }

    [Fact]
    public async Task AbortFailure_IsReportedWithoutDiscardingPrimaryParseFailure()
    {
        var limits = new TransferV3Limits(1024);
        await using var stream = new MemoryStream("not-json\n"u8.ToArray(), writable: false);
        var observer = new TransactionObserver(abortThrows: true);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            TransferV3JsonlParser.ParseAsync(stream, limits, observer));

        Assert.Contains(failure.InnerExceptions, exception => exception is FormatException);
        Assert.Contains(
            failure.InnerExceptions,
            exception => exception is InvalidOperationException { Message: "Injected abort failure." });
    }

    private static async Task<byte[]> ValidTwoBatchTableAsync(TransferV3Limits limits)
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

    private static string CorruptDigest(string text, int occurrence)
    {
        const string marker = "\"sha256\":\"";
        var position = -1;
        for (var index = 0; index <= occurrence; index++)
        {
            position = text.IndexOf(marker, position + 1, StringComparison.Ordinal);
            Assert.True(position >= 0);
        }

        position += marker.Length;
        var replacement = text[position] == 'a' ? 'b' : 'a';
        return text[..position] + replacement + text[(position + 1)..];
    }

    private static string Cursor(long value) => TransferV3CursorCodec.Encode(
        TransferV3CursorComponent.FromInt64(value));

    private sealed class TransactionObserver : ITransferV3FrameObserver
    {
        private readonly Action<TransferV3Frame>? _onObserve;
        private readonly bool _abortThrows;

        internal TransactionObserver(
            Action<TransferV3Frame>? onObserve = null,
            bool abortThrows = false)
        {
            _onObserve = onObserve;
            _abortThrows = abortThrows;
        }

        internal List<TransferV3Frame> ObservedFrames { get; } = [];
        internal List<TransferV3Frame> CurrentBatch { get; } = [];
        internal List<IReadOnlyList<TransferV3Frame>> CommittedBatches { get; } = [];
        internal IReadOnlyList<TransferV3Frame> StagedAtAbort { get; private set; } = [];
        internal int BatchCommits { get; private set; }
        internal int TableCompletions { get; private set; }
        internal int Aborts { get; private set; }
        internal TransferV3TableEndFrame? CompletedTable { get; private set; }
        internal Exception? AbortFailure { get; private set; }

        public void Observe(TransferV3Frame frame)
        {
            var retained = RetainPayload(frame);
            ObservedFrames.Add(retained);
            if (frame is not TransferV3TableHeaderFrame)
            {
                CurrentBatch.Add(retained);
            }

            _onObserve?.Invoke(frame);
        }

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
            Assert.Same(batchEnd, CurrentBatch[^1]);
            BatchCommits++;
            CommittedBatches.Add(CurrentBatch.ToArray());
            CurrentBatch.Clear();
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
            TableCompletions++;
            CompletedTable = tableEnd;
        }

        public void Abort(Exception failure)
        {
            Aborts++;
            AbortFailure = failure;
            StagedAtAbort = CurrentBatch.ToArray();
            CurrentBatch.Clear();
            if (_abortThrows)
            {
                throw new InvalidOperationException("Injected abort failure.");
            }
        }

        private static TransferV3Frame RetainPayload(TransferV3Frame frame) => frame switch
        {
            TransferV3RowFrame row => row with { Data = row.Data.ToArray() },
            TransferV3FieldChunkFrame chunk => chunk with { Data = chunk.Data.ToArray() },
            _ => frame,
        };
    }
}

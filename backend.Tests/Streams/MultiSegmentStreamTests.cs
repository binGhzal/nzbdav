using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace backend.Tests.Streams;

public sealed class MultiSegmentStreamTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReadAsyncThrowsWhenCallerTokenIsAlreadyCanceled(int articleBufferSize)
    {
        using var client = new BlockingBodyNntpClient();
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment-1" }.AsMemory(),
            client,
            articleBufferSize,
            CancellationToken.None);
        using var readCts = new CancellationTokenSource();
        await readCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], readCts.Token).AsTask());
    }

    [Fact]
    public async Task ReadAsyncObservesCallerCancellationWhileWaitingForBufferedSegment()
    {
        using var client = new BlockingBodyNntpClient();
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment-1" }.AsMemory(),
            client,
            articleBufferSize: 1,
            CancellationToken.None);
        using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                stream.ReadAsync(new byte[1], readCts.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            client.Release();
        }
    }

    [Fact]
    public async Task ReadAsyncCanRetryBufferedSegmentAfterCallerCancellation()
    {
        using var client = new BlockingBodyNntpClient();
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment-1" }.AsMemory(),
            client,
            articleBufferSize: 1,
            CancellationToken.None);
        using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], readCts.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));

        client.Release();
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(1, read);
        Assert.Equal([1], buffer);
    }

    [Fact]
    public async Task ReadAsyncCanRetryUnbufferedSegmentAfterCallerCancellation()
    {
        using var client = new BlockingBodyNntpClient();
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment-1" }.AsMemory(),
            client,
            articleBufferSize: 0,
            CancellationToken.None);
        using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], readCts.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250)));

        client.Release();
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(1, read);
        Assert.Equal([1], buffer);
    }

    private sealed class BlockingBodyNntpClient : NntpClient
    {
        private readonly TaskCompletionSource _bodyGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            _bodyGate.TrySetResult();
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task<UsenetResponse> AuthenticateAsync(
            string user,
            string pass,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new UsenetResponse
            {
                ResponseCode = 281,
                ResponseMessage = "Authentication accepted"
            });
        }

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetStatResponse>(new NotSupportedException());
        }

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetHeadResponse>(new NotSupportedException());
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
        }

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            await _bodyGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CreateBodyResponse(segmentId);
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDecodedArticleResponse>(new NotSupportedException());
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDecodedArticleResponse>(new NotSupportedException());
        }

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDateResponse>(new NotSupportedException());
        }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new UsenetExclusiveConnection(onConnectionReadyAgain: null));
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken)
        {
            return DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken)
        {
            return Task.FromException<UsenetDecodedArticleResponse>(new NotSupportedException());
        }

        public override void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private static UsenetDecodedBodyResponse CreateBodyResponse(string segmentId)
        {
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 - Body follows",
                Stream = new CachedYencStream(
                    new UsenetYencHeader
                    {
                        FileName = "segment.bin",
                        FileSize = 1,
                        LineLength = 128,
                        PartNumber = 1,
                        TotalParts = 1,
                        PartOffset = 0,
                        PartSize = 1
                    },
                    new MemoryStream([1]))
            };
        }
    }
}

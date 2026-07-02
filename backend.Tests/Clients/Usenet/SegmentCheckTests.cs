using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace backend.Tests.Clients.Usenet;

public sealed class SegmentCheckTests
{
    [Fact]
    public async Task CheckSegmentsAsync_ReturnsExistsForPresentSegments()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3])
            .AddSegment("segment-2", [4, 5, 6]);

        var batch = await client.CheckSegmentsAsync(["segment-1", "segment-2"], 2, null, CancellationToken.None);

        Assert.Equal(2, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(0, batch.ProviderErrors);
        Assert.All(batch.Results, result => Assert.Equal(SegmentCheckState.Exists, result.State));
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsMissingForDefinitiveMissingSegment()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [1, 2, 3]);

        var batch = await client.CheckSegmentsAsync(["segment-1", "missing-segment"], 2, null, CancellationToken.None);

        Assert.Equal(2, batch.Checked);
        Assert.Equal(1, batch.Missing);
        Assert.Contains(batch.Results, result =>
            result.SegmentId == "missing-segment" && result.State == SegmentCheckState.Missing);
    }

    [Fact]
    public async Task CheckSegmentsAsync_ReturnsProviderErrorWithoutReportingMissing()
    {
        using var client = new ProviderErrorNntpClient();

        var batch = await client.CheckSegmentsAsync(["segment-1"], 1, null, CancellationToken.None);

        Assert.Equal(1, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(1, batch.ProviderErrors);
        Assert.Equal(SegmentCheckState.ProviderError, batch.Results.Single().State);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_ThrowsArticleNotFoundOnlyForMissingSegment()
    {
        using var client = new FakeNntpClient();

        var ex = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsAsync(["missing-segment"], 1, null, CancellationToken.None));

        Assert.Equal("missing-segment", ex.SegmentId);
    }

    [Fact]
    public async Task CheckAllSegmentsAsync_ThrowsRetryableErrorForProviderFailure()
    {
        using var client = new ProviderErrorNntpClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CheckAllSegmentsAsync(["segment-1"], 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderPipelinedStatThrowsWhenMissingProviderIsFollowedByProviderError()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("backup", new IOException("backup unavailable"))
        ]);

        await Assert.ThrowsAsync<IOException>(() =>
            client.StatPipelinedAsync(["segment-1"], CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderPipelinedStatReturnsMissingWhenEveryProviderDefinitivelyMisses()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId)
        ]);

        var result = await client.StatPipelinedAsync(["segment-1"], CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(UsenetResponseType.NoArticleWithThatMessageId, result.Single().ResponseType);
    }

    [Fact]
    public async Task MultiProviderStatThrowsWhenProviderErrorIsFollowedByMissing()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId)
        ]);

        await Assert.ThrowsAsync<IOException>(() =>
            client.StatAsync("segment-1", CancellationToken.None));
    }

    [Fact]
    public async Task MultiProviderStatCanReturnSuccessFromBackupAfterProviderError()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatAsync("segment-1", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, result.ResponseType);
    }

    [Fact]
    public async Task MultiProviderStatContinuesAfterProviderErrorThenMissingWhenLaterProviderExists()
    {
        using var client = new MultiProviderNntpClient([
            new FakePipelinedProvider("primary", new IOException("primary unavailable")),
            new FakePipelinedProvider("backup", UsenetResponseType.NoArticleWithThatMessageId),
            new FakePipelinedProvider("tertiary", UsenetResponseType.ArticleExists)
        ]);

        var result = await client.StatAsync("segment-1", CancellationToken.None);

        Assert.Equal(UsenetResponseType.ArticleExists, result.ResponseType);
    }

    private sealed class ProviderErrorNntpClient : FakeNntpClient
    {
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            throw new IOException("provider unavailable");
        }
    }

    private sealed class FakePipelinedProvider : MultiConnectionNntpClient
    {
        private readonly UsenetResponseType? _responseType;
        private readonly Exception? _exception;

        public FakePipelinedProvider(string name, UsenetResponseType responseType)
            : base(
                new ConnectionPool<INntpClient>(
                    1,
                    _ => ValueTask.FromResult<INntpClient>(new FakeNntpClient()),
                    TimeSpan.FromMinutes(1)),
                ProviderType.Pooled,
                new ProviderCircuitBreaker(name),
                name,
                providerPriority: 0,
                statPipeliningEnabled: true)
        {
            _responseType = responseType;
        }

        public FakePipelinedProvider(string name, Exception exception)
            : this(name, UsenetResponseType.ArticleExists)
        {
            _responseType = null;
            _exception = exception;
        }

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            if (_exception is not null) throw _exception;
            return Task.FromResult(CreateStatResponse(_responseType ?? UsenetResponseType.ArticleExists));
        }

        public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
        (
            IReadOnlyList<string> segmentIds,
            CancellationToken cancellationToken
        )
        {
            if (_exception is not null) throw _exception;

            var responseType = _responseType ?? UsenetResponseType.ArticleExists;
            IReadOnlyList<UsenetStatResponse> results = segmentIds
                .Select(_ => CreateStatResponse(responseType))
                .ToArray();
            return Task.FromResult(results);
        }

        private static UsenetStatResponse CreateStatResponse(UsenetResponseType responseType)
        {
            return new UsenetStatResponse
            {
                ArticleExists = responseType == UsenetResponseType.ArticleExists,
                ResponseCode = (int)responseType,
                ResponseMessage = responseType.ToString()
            };
        }
    }
}

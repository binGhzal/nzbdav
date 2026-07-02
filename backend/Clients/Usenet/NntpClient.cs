using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Abstract base class for NNTP clients with default implementations of utility methods.
/// </summary>
public abstract class NntpClient : INntpClient
{
    public abstract Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    public abstract Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    public abstract Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    public abstract void Dispose();

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support acquiring exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodyAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedArticleAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        var decodedBodyResponse = await this.DecodedBodyWithFallbackAsync(segmentId, ct).ConfigureAwait(false);
        await using var stream = decodedBodyResponse.Stream;
        var headers = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
        return headers!;
    }

    public virtual async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
    {
        var segmentIds = file.GetSegmentIds();
        if (segmentIds.Length == 0) return 0;
        var headers = await GetYencHeadersAsync(segmentIds[^1], ct).ConfigureAwait(false);
        return headers!.PartOffset + headers!.PartSize;
    }

    public virtual async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int articleBufferSize, CancellationToken ct)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await GetFileSizeAsync(nzbFile, ct).ConfigureAwait(false);
        return new NzbFileStream(segmentIds, fileSize, this, articleBufferSize);
    }

    public virtual NzbFileStream GetFileStream(NzbFile nzbFile, long fileSize, int articleBufferSize)
    {
        return new NzbFileStream(nzbFile.GetSegmentIds(), fileSize, this, articleBufferSize);
    }

    public virtual NzbFileStream GetFileStream(
        string[] segmentIds, long fileSize, int articleBufferSize, long? requestedEndByte = null)
    {
        return new NzbFileStream(segmentIds, fileSize, this, articleBufferSize, requestedEndByte);
    }

    /// <summary>
    /// Default linear implementation: STAT each segment one at a time on this client.
    /// Layers that can do better (a raw connection that pipelines, or a pool/provider that
    /// borrows a single connection for the batch) override this.
    /// </summary>
    public virtual async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        var results = new UsenetStatResponse[segmentIds.Count];
        for (var i = 0; i < segmentIds.Count; i++)
            results[i] = await StatAsync(segmentIds[i], cancellationToken).ConfigureAwait(false);
        return results;
    }

    public virtual async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var batch = await CheckSegmentsAsync(segmentIds, concurrency, progress, cancellationToken)
            .ConfigureAwait(false);
        var missing = batch.Results.FirstOrDefault(x => x.State == SegmentCheckState.Missing);
        if (missing is not null)
            throw new UsenetArticleNotFoundException(missing.CandidateSegmentId ?? missing.SegmentId);

        if (!batch.IsClean)
            throw new InvalidOperationException(
                $"Segment check did not complete cleanly. provider_errors={batch.ProviderErrors}, unknown={batch.Unknown}");
    }

    public virtual async Task<SegmentCheckBatch> CheckSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var segmentList = segmentIds.ToList();
        var results = new SegmentCheckResult[segmentList.Count];
        var tasks = segmentList
            .Select((segmentId, index) => CheckSegmentAsync(index, segmentId, cancellationToken))
            .WithConcurrencyAsync(Math.Max(1, concurrency), cancellationToken);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            progress?.Report(++processed);
            results[task.Index] = task.Result;
        }

        return SegmentCheckBatch.FromResults(results);
    }

    protected virtual async Task<(int Index, SegmentCheckResult Result)> CheckSegmentAsync
    (
        int index,
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await NntpClientSegmentFallbackExtensions.WithFallbackAsync(
                    segmentId,
                    (candidateSegmentId, ct) => StatAsync(candidateSegmentId, ct),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (index, CreateSegmentCheckResult(segmentId, response));
        }
        catch (UsenetArticleNotFoundException e)
        {
            return (
                index,
                new SegmentCheckResult(
                    segmentId,
                    SegmentCheckState.Missing,
                    Provider: null,
                    Error: e.Message,
                    CandidateSegmentId: e.SegmentId)
            );
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            return (index, new SegmentCheckResult(segmentId, SegmentCheckState.ProviderError, Provider: null, Error: e.Message));
        }
    }

    protected static SegmentCheckResult CreateSegmentCheckResult(string segmentId, UsenetStatResponse response)
    {
        return response.ResponseType switch
        {
            UsenetResponseType.ArticleExists =>
                new SegmentCheckResult(segmentId, SegmentCheckState.Exists, Provider: null, Error: null),
            UsenetResponseType.NoArticleWithThatMessageId =>
                new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: null, Error: response.ResponseMessage),
            _ => new SegmentCheckResult(segmentId, SegmentCheckState.Unknown, Provider: null, Error: response.ResponseMessage)
        };
    }
}

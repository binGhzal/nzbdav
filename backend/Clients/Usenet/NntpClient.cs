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
    protected virtual bool SupportsPipelinedSegmentChecks => false;

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
        {
            try
            {
                results[i] = await StatAsync(segmentIds[i], cancellationToken).ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException e)
            {
                results[i] = CreateMissingStatResponse(e.SegmentId);
            }
        }

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
        if (!SupportsPipelinedSegmentChecks)
            return await CheckSegmentsConcurrentlyAsync(segmentIds, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);

        return await CheckSegmentsPipelinedAsync(
                segmentIds,
                batchSize: concurrency,
                maxConcurrentBatches: concurrency,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    protected async Task<SegmentCheckBatch> CheckSegmentsPipelinedAsync
    (
        IEnumerable<string> segmentIds,
        int batchSize,
        int maxConcurrentBatches,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var segmentList = segmentIds.ToList();
        var results = new SegmentCheckResult?[segmentList.Count];
        var unresolvedResults = new SegmentCheckResult?[segmentList.Count];
        var pending = new List<PendingSegmentCheck>(segmentList.Count);

        var processed = 0;
        var normalizedBatchSize = Math.Max(1, batchSize);
        var normalizedConcurrentBatches = Math.Max(1, maxConcurrentBatches);
        for (var i = 0; i < segmentList.Count; i++)
        {
            if (!TryGetCandidateSegmentId(segmentList[i], candidateIndex: 0, out var candidateSegmentId))
            {
                Complete(i, new SegmentCheckResult(
                    segmentList[i],
                    SegmentCheckState.Missing,
                    Provider: null,
                    Error: "Segment id is empty."));
                continue;
            }

            pending.Add(new PendingSegmentCheck(i, CandidateIndex: 0, candidateSegmentId));
        }

        while (pending.Count > 0)
        {
            var nextPending = new List<PendingSegmentCheck>();
            var chunkChecks = pending
                .Chunk(normalizedBatchSize)
                .Select(chunk => CheckPipelinedChunkAsync(chunk.ToArray()))
                .WithConcurrencyAsync(normalizedConcurrentBatches, cancellationToken);

            await foreach (var chunkResult in chunkChecks.ConfigureAwait(false))
            {
                foreach (var (index, result) in chunkResult.Completed)
                    Complete(index, result);
                nextPending.AddRange(chunkResult.NextPending);
            }

            pending = nextPending;
        }

        return CreateSegmentCheckBatch(segmentList, results);

        async Task<PipelinedChunkResult> CheckPipelinedChunkAsync(PendingSegmentCheck[] chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = new List<(int Index, SegmentCheckResult Result)>(chunk.Length);
            var chunkNextPending = new List<PendingSegmentCheck>();

            IReadOnlyList<UsenetStatResponse> responses;
            try
            {
                responses = await StatPipelinedAsync(
                        chunk.Select(x => x.CandidateSegmentId).ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException e)
            {
                responses = chunk
                    .Select(x => string.Equals(x.CandidateSegmentId, e.SegmentId, StringComparison.Ordinal)
                        ? CreateMissingStatResponse(e.SegmentId)
                        : CreateUnknownStatResponse($"Pipelined STAT batch aborted after missing segment {e.SegmentId}."))
                    .ToArray();
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                foreach (var item in chunk)
                {
                    completed.Add((item.Index, new SegmentCheckResult(
                        segmentList[item.Index],
                        SegmentCheckState.ProviderError,
                        Provider: null,
                        Error: e.Message,
                        CandidateSegmentId: item.CandidateSegmentId)));
                }

                return new PipelinedChunkResult(completed, chunkNextPending);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                foreach (var item in chunk)
                {
                    completed.Add((item.Index, new SegmentCheckResult(
                        segmentList[item.Index],
                        SegmentCheckState.ProviderError,
                        Provider: null,
                        Error: e.Message,
                        CandidateSegmentId: item.CandidateSegmentId)));
                }

                return new PipelinedChunkResult(completed, chunkNextPending);
            }

            if (responses.Count != chunk.Length)
            {
                foreach (var item in chunk)
                {
                    completed.Add((item.Index, new SegmentCheckResult(
                        segmentList[item.Index],
                        SegmentCheckState.ProviderError,
                        Provider: null,
                        Error: $"Provider returned {responses.Count} STAT responses for {chunk.Length} requested segments.",
                        CandidateSegmentId: item.CandidateSegmentId)));
                }

                return new PipelinedChunkResult(completed, chunkNextPending);
            }

            for (var i = 0; i < chunk.Length; i++)
            {
                var item = chunk[i];
                if (results[item.Index] is not null) continue;

                var response = responses[i];
                if (response.ResponseType == UsenetResponseType.ArticleExists)
                {
                    completed.Add((item.Index, CreateSegmentCheckResult(
                        segmentList[item.Index],
                        response,
                        item.CandidateSegmentId)));
                    continue;
                }

                var result = CreateSegmentCheckResult(
                    segmentList[item.Index],
                    response,
                    item.CandidateSegmentId);
                if (response.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    unresolvedResults[item.Index] = result;

                var nextCandidateIndex = item.CandidateIndex + 1;
                if (TryGetCandidateSegmentId(
                        segmentList[item.Index],
                        nextCandidateIndex,
                        out var nextCandidateSegmentId))
                {
                    chunkNextPending.Add(new PendingSegmentCheck(
                        item.Index,
                        nextCandidateIndex,
                        nextCandidateSegmentId));
                    continue;
                }

                completed.Add((item.Index, unresolvedResults[item.Index] ?? result));
            }

            return new PipelinedChunkResult(completed, chunkNextPending);
        }

        void Complete(int index, SegmentCheckResult result)
        {
            if (results[index] is not null) return;
            progress?.Report(Interlocked.Increment(ref processed));
            results[index] = result;
        }
    }

    protected async Task<SegmentCheckBatch> CheckSegmentsConcurrentlyAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var segmentList = segmentIds.ToList();
        var results = new SegmentCheckResult?[segmentList.Count];
        var processed = 0;
        var tasks = segmentList
            .Select((segmentId, index) => CheckSegmentAsync(index, segmentId, cancellationToken))
            .WithConcurrencyAsync(Math.Max(1, concurrency), cancellationToken);

        await foreach (var item in tasks.ConfigureAwait(false))
        {
            results[item.Index] = item.Result;
            progress?.Report(++processed);
        }

        return CreateSegmentCheckBatch(segmentList, results);
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
            return (index, await CheckSegmentWithFallbackAsync(segmentId, cancellationToken).ConfigureAwait(false));
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

    private async Task<SegmentCheckResult> CheckSegmentWithFallbackAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        var candidateSegmentIds = NzbSegmentIdSet.Decode(segmentId);
        if (candidateSegmentIds.Length == 0)
        {
            return new SegmentCheckResult(
                segmentId,
                SegmentCheckState.Missing,
                Provider: null,
                Error: "Segment id is empty.");
        }

        SegmentCheckResult? unresolvedResult = null;
        foreach (var candidateSegmentId in candidateSegmentIds)
        {
            try
            {
                var response = await StatAsync(candidateSegmentId, cancellationToken).ConfigureAwait(false);
                var result = CreateSegmentCheckResult(segmentId, response, candidateSegmentId);
                if (result.State == SegmentCheckState.Exists)
                    return result;

                if (unresolvedResult is null
                    || unresolvedResult.State == SegmentCheckState.Missing
                    || result.State != SegmentCheckState.Missing)
                    unresolvedResult = result;
            }
            catch (UsenetArticleNotFoundException e)
            {
                if (unresolvedResult is null || unresolvedResult.State == SegmentCheckState.Missing)
                {
                    unresolvedResult = new SegmentCheckResult(
                        segmentId,
                        SegmentCheckState.Missing,
                        Provider: null,
                        Error: e.Message,
                        CandidateSegmentId: e.SegmentId);
                }
            }
        }

        return unresolvedResult ?? new SegmentCheckResult(
            segmentId,
            SegmentCheckState.Missing,
            Provider: null,
            Error: "No article candidates were available.");
    }

    protected static SegmentCheckResult CreateSegmentCheckResult(
        string segmentId,
        UsenetStatResponse response,
        string? candidateSegmentId = null)
    {
        return response.ResponseType switch
        {
            UsenetResponseType.ArticleExists =>
                new SegmentCheckResult(segmentId, SegmentCheckState.Exists, Provider: null, Error: null, candidateSegmentId),
            UsenetResponseType.NoArticleWithThatMessageId =>
                new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: null, Error: response.ResponseMessage, candidateSegmentId),
            _ => new SegmentCheckResult(segmentId, SegmentCheckState.Unknown, Provider: null, Error: response.ResponseMessage, candidateSegmentId)
        };
    }

    protected static SegmentCheckBatch CreateSegmentCheckBatch(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<SegmentCheckResult?> results)
    {
        var checkedCount = results.Count;
        var missing = 0;
        var providerErrors = 0;
        var unknown = 0;
        var canUseLazyAllExists = checkedCount == segmentIds.Count;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result is null)
            {
                providerErrors++;
                canUseLazyAllExists = false;
                continue;
            }

            switch (result.State)
            {
                case SegmentCheckState.Exists:
                    if (!string.IsNullOrWhiteSpace(result.CandidateSegmentId)
                        && !string.Equals(result.CandidateSegmentId, result.SegmentId, StringComparison.Ordinal))
                        canUseLazyAllExists = false;
                    break;
                case SegmentCheckState.Missing:
                    missing++;
                    canUseLazyAllExists = false;
                    break;
                case SegmentCheckState.ProviderError:
                    providerErrors++;
                    canUseLazyAllExists = false;
                    break;
                case SegmentCheckState.Unknown:
                    unknown++;
                    canUseLazyAllExists = false;
                    break;
                default:
                    canUseLazyAllExists = false;
                    break;
            }
        }

        if (canUseLazyAllExists)
            return SegmentCheckBatch.AllExists(segmentIds);

        return new SegmentCheckBatch(
            results
                .Select((result, index) => result ?? new SegmentCheckResult(
                    segmentIds[index],
                    SegmentCheckState.ProviderError,
                    Provider: null,
                    Error: "Provider did not return a segment check result."))
                .ToArray(),
            checkedCount,
            missing,
            providerErrors,
            unknown);
    }

    private static bool TryGetCandidateSegmentId(
        string encodedSegmentId,
        int candidateIndex,
        out string candidateSegmentId)
    {
        candidateSegmentId = "";
        if (string.IsNullOrWhiteSpace(encodedSegmentId)) return false;

        if (encodedSegmentId[0] != '[')
        {
            if (candidateIndex != 0) return false;
            candidateSegmentId = encodedSegmentId;
            return true;
        }

        var candidates = NzbSegmentIdSet.Decode(encodedSegmentId);
        if ((uint)candidateIndex >= (uint)candidates.Length) return false;

        candidateSegmentId = candidates[candidateIndex];
        return true;
    }

    private static UsenetStatResponse CreateMissingStatResponse(string segmentId)
    {
        return new UsenetStatResponse
        {
            ArticleExists = false,
            ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
            ResponseMessage = $"430 - No article with message-id <{segmentId}>"
        };
    }

    private static UsenetStatResponse CreateUnknownStatResponse(string message)
    {
        return new UsenetStatResponse
        {
            ArticleExists = false,
            ResponseCode = (int)UsenetResponseType.Unknown,
            ResponseMessage = message
        };
    }

    private readonly record struct PendingSegmentCheck(
        int Index,
        int CandidateIndex,
        string CandidateSegmentId);

    private readonly record struct PipelinedChunkResult(
        IReadOnlyList<(int Index, SegmentCheckResult Result)> Completed,
        IReadOnlyList<PendingSegmentCheck> NextPending);
}

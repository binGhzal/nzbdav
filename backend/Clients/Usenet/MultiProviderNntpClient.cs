using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : NntpClient
{
    private long _providerFanoutCursor;

    protected override bool SupportsPipelinedSegmentChecks =>
        providers.Any(provider => provider.ProviderType != ProviderType.Disabled && provider.StatPipeliningEnabled);

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override async Task<SegmentCheckBatch> CheckSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var segmentList = segmentIds.ToList();
        if (segmentList.Count == 0) return SegmentCheckBatch.AllExists(segmentList);

        var results = new SegmentCheckResult?[segmentList.Count];
        var unresolvedResults = new SegmentCheckResult?[segmentList.Count];
        var triedProviderMasks = new ulong[segmentList.Count];
        var triedProviderCounts = new int[segmentList.Count];
        var pending = Enumerable.Range(0, segmentList.Count).ToList();
        var processed = 0;

        var orderedProviders = GetOrderedProviders();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assignments = CreateProviderStatAssignments(pending, triedProviderMasks, orderedProviders);
            if (assignments.Count == 0) break;

            foreach (var assignment in assignments)
            foreach (var index in assignment.Indexes)
                MarkProviderTried(triedProviderMasks, triedProviderCounts, index, assignment.ProviderIndex);

            var assignmentResults = await Task.WhenAll(assignments
                    .Select(assignment => CheckProviderSegmentAssignmentAsync(
                        assignment,
                        segmentList,
                        GetAssignmentConcurrency(concurrency, assignment.Indexes.Count, pending.Count),
                        cancellationToken)))
                .ConfigureAwait(false);

            var nextPending = new List<int>();
            foreach (var assignmentResult in assignmentResults)
            {
                if (assignmentResult.Exception is not null)
                {
                    var error = assignmentResult.Exception.SourceException.Message;
                    foreach (var index in assignmentResult.Indexes)
                    {
                        PreserveUnresolvedResult(index, new SegmentCheckResult(
                            segmentList[index],
                            SegmentCheckState.ProviderError,
                            Provider: null,
                            Error: error));
                        if (triedProviderCounts[index] < orderedProviders.Count)
                            nextPending.Add(index);
                    }

                    continue;
                }

                var providerResults = assignmentResult.Results!;
                for (var i = 0; i < assignmentResult.Indexes.Count; i++)
                {
                    var index = assignmentResult.Indexes[i];
                    if (results[index] is not null) continue;

                    var result = providerResults[i];
                    if (result.State == SegmentCheckState.Exists)
                    {
                        Complete(index, result);
                        continue;
                    }

                    PreserveUnresolvedResult(index, result);
                    if (triedProviderCounts[index] < orderedProviders.Count)
                        nextPending.Add(index);
                }
            }

            pending = nextPending
                .Where(index => results[index] is null)
                .Distinct()
                .ToList();
        }

        for (var index = 0; index < results.Length; index++)
        {
            if (results[index] is not null) continue;
            Complete(index, unresolvedResults[index] ?? new SegmentCheckResult(
                segmentList[index],
                SegmentCheckState.ProviderError,
                Provider: null,
                Error: "There are no usenet providers configured."));
        }

        return CreateSegmentCheckBatch(segmentList, results);

        void PreserveUnresolvedResult(int index, SegmentCheckResult result)
        {
            var existing = unresolvedResults[index];
            if (existing is null || GetUnresolvedStateWeight(result.State) > GetUnresolvedStateWeight(existing.State))
                unresolvedResults[index] = result;
        }

        static int GetUnresolvedStateWeight(SegmentCheckState state)
        {
            return state switch
            {
                SegmentCheckState.ProviderError => 3,
                SegmentCheckState.Unknown => 2,
                SegmentCheckState.Missing => 1,
                _ => 0
            };
        }

        void Complete(int index, SegmentCheckResult result)
        {
            if (results[index] is not null) return;
            results[index] = result;
            progress?.Report(Interlocked.Increment(ref processed));
        }
    }

    private static async Task<ProviderSegmentAssignmentResult> CheckProviderSegmentAssignmentAsync
    (
        ProviderStatAssignment assignment,
        IReadOnlyList<string> segmentIds,
        int concurrency,
        CancellationToken cancellationToken
    )
    {
        var subBatch = assignment.Indexes.Select(i => segmentIds[i]).ToArray();
        try
        {
            var batch = await assignment.Provider
                .CheckSegmentsAsync(subBatch, concurrency, null, cancellationToken)
                .ConfigureAwait(false);
            if (batch.Results.Count != subBatch.Length)
            {
                return new ProviderSegmentAssignmentResult(
                    assignment.ProviderIndex,
                    assignment.Indexes,
                    Results: null,
                    ExceptionDispatchInfo.Capture(new IOException(
                        $"Provider returned {batch.Results.Count} segment check results for {subBatch.Length} requested segments.")));
            }

            return new ProviderSegmentAssignmentResult(
                assignment.ProviderIndex,
                assignment.Indexes,
                batch.Results,
                Exception: null);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            return new ProviderSegmentAssignmentResult(
                assignment.ProviderIndex,
                assignment.Indexes,
                Results: null,
                ExceptionDispatchInfo.Capture(e));
        }
    }

    private static int GetAssignmentConcurrency(int totalConcurrency, int assignmentCount, int pendingCount)
    {
        if (pendingCount <= 0) return 1;
        var normalizedTotal = Math.Max(1, totalConcurrency);
        var share = (int)Math.Ceiling(normalizedTotal * (assignmentCount / (double)pendingCount));
        return Math.Clamp(share, 1, Math.Max(1, assignmentCount));
    }

    public override async Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return [];

        var results = new UsenetStatResponse?[segmentIds.Count];
        var unresolvedResults = new UsenetStatResponse?[segmentIds.Count];
        var providerErrors = new ExceptionDispatchInfo?[segmentIds.Count];
        var triedProviderMasks = new ulong[segmentIds.Count];
        var triedProviderCounts = new int[segmentIds.Count];
        var pending = new List<int>(segmentIds.Count);
        for (var i = 0; i < segmentIds.Count; i++) pending.Add(i);

        var orderedProviders = GetOrderedProviders();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assignments = CreateProviderStatAssignments(pending, triedProviderMasks, orderedProviders);
            if (assignments.Count == 0) break;

            foreach (var assignment in assignments)
            foreach (var index in assignment.Indexes)
                MarkProviderTried(triedProviderMasks, triedProviderCounts, index, assignment.ProviderIndex);

            var assignmentResults = await Task.WhenAll(assignments
                    .Select(assignment => CheckProviderStatAssignmentAsync(
                        assignment,
                        segmentIds,
                        cancellationToken)))
                .ConfigureAwait(false);

            var nextPending = new List<int>();
            foreach (var assignmentResult in assignmentResults)
            {
                if (assignmentResult.Exception is not null)
                {
                    foreach (var index in assignmentResult.Indexes)
                    {
                        providerErrors[index] = assignmentResult.Exception;
                        if (triedProviderCounts[index] < orderedProviders.Count)
                            nextPending.Add(index);
                    }

                    continue;
                }

                var responses = assignmentResult.Responses!;
                for (var k = 0; k < assignmentResult.Indexes.Count; k++)
                {
                    var index = assignmentResult.Indexes[k];
                    if (results[index] is not null) continue;

                    var response = responses[k];
                    if (response.ResponseType == UsenetResponseType.ArticleExists)
                    {
                        results[index] = response;
                        continue;
                    }

                    PreserveUnresolvedResult(index, response);
                    if (triedProviderCounts[index] < orderedProviders.Count)
                        nextPending.Add(index);
                }
            }

            pending = nextPending
                .Where(index => results[index] is null)
                .Distinct()
                .ToList();
        }

        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is not null) continue;
            if (providerErrors[i] is not null)
                ThrowProviderException(providerErrors[i]!);
            if (unresolvedResults[i] is not null)
            {
                results[i] = unresolvedResults[i];
                continue;
            }

            throw new Exception("There are no usenet providers configured.");
        }

        return results!;

        void PreserveUnresolvedResult(int index, UsenetStatResponse response)
        {
            if (unresolvedResults[index] is null
                || response.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                unresolvedResults[index] = response;
        }
    }

    private List<ProviderStatAssignment> CreateProviderStatAssignments
    (
        IReadOnlyList<int> pending,
        IReadOnlyList<ulong> triedProviderMasks,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders
    )
    {
        var providerOrder = CreateProviderFanoutOrder(orderedProviders, pending.Count);
        var assignmentIndexes = new Dictionary<int, List<int>>();
        var providerCursor = ReserveProviderFanoutCursor(pending.Count, providerOrder.Count);

        foreach (var index in pending)
        {
            for (var attempt = 0; attempt < providerOrder.Count; attempt++)
            {
                var providerIndex = providerOrder[(int)(providerCursor % providerOrder.Count)];
                providerCursor++;
                if (HasProviderBeenTried(triedProviderMasks[index], providerIndex)) continue;

                if (!assignmentIndexes.TryGetValue(providerIndex, out var indexes))
                {
                    indexes = [];
                    assignmentIndexes[providerIndex] = indexes;
                }

                indexes.Add(index);
                break;
            }
        }

        return assignmentIndexes
            .OrderBy(x => x.Key)
            .Select(x => new ProviderStatAssignment(x.Key, orderedProviders[x.Key], x.Value))
            .ToList();
    }

    private static bool HasProviderBeenTried(ulong mask, int providerIndex)
    {
        return (mask & (1UL << providerIndex)) != 0;
    }

    private static void MarkProviderTried(
        ulong[] triedProviderMasks,
        int[] triedProviderCounts,
        int segmentIndex,
        int providerIndex)
    {
        var bit = 1UL << providerIndex;
        if ((triedProviderMasks[segmentIndex] & bit) != 0) return;

        triedProviderMasks[segmentIndex] |= bit;
        triedProviderCounts[segmentIndex]++;
    }

    private long ReserveProviderFanoutCursor(int pendingCount, int providerOrderCount)
    {
        if (providerOrderCount <= 0) return 0;

        var reservedSlots = Math.Max(1, pendingCount);
        var nextCursor = Interlocked.Add(ref _providerFanoutCursor, reservedSlots);
        return PositiveModulo(nextCursor - reservedSlots, providerOrderCount);
    }

    private static long PositiveModulo(long value, int modulo)
    {
        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static List<int> CreateProviderFanoutOrder(
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        int pendingCount)
    {
        var maxUsefulSlotsPerProvider = Math.Max(1, pendingCount);
        var providerOrder = new List<int>();
        for (var providerIndex = 0; providerIndex < orderedProviders.Count; providerIndex++)
        {
            var provider = orderedProviders[providerIndex];
            var slots = Math.Clamp(provider.AvailableConnections, 1, maxUsefulSlotsPerProvider);
            for (var i = 0; i < slots; i++)
                providerOrder.Add(providerIndex);
        }

        return providerOrder;
    }

    private static async Task<ProviderStatAssignmentResult> CheckProviderStatAssignmentAsync
    (
        ProviderStatAssignment assignment,
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        var subBatch = assignment.Indexes.Select(i => segmentIds[i]).ToList();
        try
        {
            var responses = assignment.Provider.StatPipeliningEnabled
                ? await assignment.Provider
                    .StatPipelinedAsync(subBatch, cancellationToken)
                    .ConfigureAwait(false)
                : await CheckNonPipelinedProviderStatsAsync(
                        assignment.Provider,
                        subBatch,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (responses.Count != subBatch.Count)
            {
                return new ProviderStatAssignmentResult(
                    assignment.ProviderIndex,
                    assignment.Indexes,
                    Responses: null,
                    ExceptionDispatchInfo.Capture(new IOException(
                        $"Provider returned {responses.Count} pipelined STAT responses for {subBatch.Count} requested segments.")));
            }

            return new ProviderStatAssignmentResult(
                assignment.ProviderIndex,
                assignment.Indexes,
                responses,
                Exception: null);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            return new ProviderStatAssignmentResult(
                assignment.ProviderIndex,
                assignment.Indexes,
                Responses: null,
                ExceptionDispatchInfo.Capture(e));
        }
    }

    private static async Task<IReadOnlyList<UsenetStatResponse>> CheckNonPipelinedProviderStatsAsync
    (
        MultiConnectionNntpClient provider,
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        var responses = new UsenetStatResponse?[segmentIds.Count];
        var concurrency = Math.Clamp(provider.AvailableConnections, 1, Math.Max(1, segmentIds.Count));
        var tasks = segmentIds
            .Select((segmentId, index) => CheckAsync(index, segmentId))
            .WithConcurrencyAsync(concurrency, cancellationToken);

        await foreach (var item in tasks.ConfigureAwait(false))
            responses[item.Index] = item.Response;

        return responses.Select(x => x!).ToArray();

        async Task<(int Index, UsenetStatResponse Response)> CheckAsync(int index, string segmentId)
        {
            var response = await provider.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            return (index, response);
        }
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        ExceptionDispatchInfo? lastException = null;
        T? unresolvedResult = null;
        var orderedProviders = GetOrderedProviders();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);

                if (result.ResponseType is not UsenetResponseType.NoArticleWithThatMessageId
                    and not UsenetResponseType.Unknown)
                    return result;

                // Missing and Unknown both remain unresolved until every provider has had a
                // chance. Preserve Unknown over Missing so repair never treats an inconclusive
                // provider response as definitive article loss.
                if (unresolvedResult is null || result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    unresolvedResult = result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (lastException is not null) ThrowProviderException(lastException);
        if (unresolvedResult is not null) return unresolvedResult;
        throw new Exception("There are no usenet providers configured.");
    }

    private static void ThrowProviderException(ExceptionDispatchInfo exception)
    {
        var source = exception.SourceException;
        if (source is RetryableDownloadException retryable)
            throw retryable;

        if (IsProviderUnavailable(source))
            throw new RetryableDownloadException("All usenet providers are temporarily unavailable.", source);

        exception.Throw();
    }

    private static bool IsProviderUnavailable(Exception exception)
    {
        return exception is IOException or TimeoutException or RetryableDownloadException
               || exception.TryGetCausingException<CouldNotLoginToUsenetException>(out _)
               || exception.TryGetCausingException<IOException>(out _)
               || exception.TryGetCausingException<TimeoutException>(out _);
    }

    private sealed record ProviderStatAssignment(
        int ProviderIndex,
        MultiConnectionNntpClient Provider,
        IReadOnlyList<int> Indexes);

    private sealed record ProviderStatAssignmentResult(
        int ProviderIndex,
        IReadOnlyList<int> Indexes,
        IReadOnlyList<UsenetStatResponse>? Responses,
        ExceptionDispatchInfo? Exception);

    private sealed record ProviderSegmentAssignmentResult(
        int ProviderIndex,
        IReadOnlyList<int> Indexes,
        IReadOnlyList<SegmentCheckResult>? Results,
        ExceptionDispatchInfo? Exception);

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .OrderBy(x => x.ProviderType)
            .ThenBy(x => x.ProviderPriority)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        return healthy.Count > 0 ? healthy : enabled;
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for caching Article/Body commands to disk.
/// It is intended to be short-lived. It will delete all cached articles on disposal.
/// </summary>
/// <param name="usenetClient">The underlying client to cache.</param>
/// <param name="leaveOpen">Indicates whether disposing this client also disposes the underlying client.</param>
public class ArticleCachingNntpClient(
    INntpClient usenetClient,
    long maxCacheBytes = long.MaxValue,
    long? sharedMaxCacheBytes = null,
    bool leaveOpen = true,
    ArticleCacheBudget? sharedBudget = null,
    int maxFetchedSegmentIds = 100_000
) : WrappingNntpClient(usenetClient), IAsyncDisposable
{
    private readonly string _cacheDir = Directory.CreateTempSubdirectory().FullName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();
    private readonly ConcurrentDictionary<string, byte> _fetchedSegmentIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _fetchedSegmentOrder = new();
    private readonly SemaphoreSlim _cacheSizeSemaphore = new(1, 1);
    private readonly long _maxCacheBytes = maxCacheBytes > 0 ? maxCacheBytes : long.MaxValue;
    private readonly int _maxFetchedSegmentIds = Math.Max(0, maxFetchedSegmentIds);
    private readonly ArticleCacheBudget _sharedBudget = ConfigureSharedBudget(
        sharedBudget ?? ArticleCacheBudget.Shared,
        sharedMaxCacheBytes ?? maxCacheBytes);
    private long _cacheBytes;
    private bool _disposed;

    private static ArticleCacheBudget ConfigureSharedBudget(ArticleCacheBudget budget, long maxCacheBytes)
    {
        budget.Configure(maxCacheBytes);
        return budget;
    }

    private class CacheEntry
    {
        public required UsenetYencHeader YencHeaders { get; init; }
        public bool HasArticleHeaders { get; set; }
        public UsenetArticleHeader? ArticleHeaders { get; set; }
        public long SizeBytes { get; init; }
        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            // Check if already cached
            if (_cachedSegments.TryGetValue(segmentId, out var existingEntry))
            {
                TouchCacheEntry(existingEntry);
                try
                {
                    var cachedResponse = ReadCachedBodyAsync(segmentId, existingEntry.YencHeaders);
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                    return cachedResponse;
                }
                catch (Exception e) when (IsStaleCacheFileException(e))
                {
                    await RemoveCachedEntryAsync(segmentId, cancellationToken).ConfigureAwait(false);
                }
            }

            // Fetch and cache the body
            var response = await base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);
            if (response.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
                return response;

            // Get the decoded stream
            await using var stream = response.Stream;

            // Get yenc headers before caching the decoded stream
            var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (yencHeaders == null)
            {
                throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");
            }

            var sizeBytes = await CacheDecodedStreamAsync(segmentId, stream, cancellationToken).ConfigureAwait(false);
            MarkFetched(segmentId);

            // Mark as cached (body only, no article headers yet)
            _cachedSegments.TryAdd(segmentId, new CacheEntry
            {
                YencHeaders = yencHeaders,
                HasArticleHeaders = false,
                ArticleHeaders = null,
                SizeBytes = sizeBytes
            });
            await UpdateCacheSizeAndEvictAsync(sizeBytes, segmentId, cancellationToken).ConfigureAwait(false);

            // Return a new stream from the cached file
            return ReadCachedBodyAsync(segmentId, yencHeaders);
        }
        finally
        {
            semaphore.Release();
            if (!_cachedSegments.ContainsKey(segmentId))
                _pendingRequests.TryRemove(segmentId, out _);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        try
        {
            // Check if already cached with headers
            if (_cachedSegments.TryGetValue(segmentId, out var cacheEntry))
            {
                TouchCacheEntry(cacheEntry);
                if (cacheEntry.HasArticleHeaders)
                {
                    // Full article is cached, read from cache
                    try
                    {
                        var cachedResponse = ReadCachedArticleAsync(segmentId, cacheEntry.YencHeaders, cacheEntry.ArticleHeaders!);
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                        return cachedResponse;
                    }
                    catch (Exception e) when (IsStaleCacheFileException(e))
                    {
                        await RemoveCachedEntryAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Only body is cached, fetch article headers separately
                    var headResponse = await base.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);

                    // Update cache entry to include article headers
                    cacheEntry.HasArticleHeaders = true;
                    cacheEntry.ArticleHeaders = headResponse.ArticleHeaders;

                    try
                    {
                        var cachedResponse = ReadCachedArticleAsync(segmentId, cacheEntry.YencHeaders, headResponse.ArticleHeaders!);
                        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                        return cachedResponse;
                    }
                    catch (Exception e) when (IsStaleCacheFileException(e))
                    {
                        await RemoveCachedEntryAsync(segmentId, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Fetch and cache the full article
            var response = await base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken)
                .ConfigureAwait(false);
            if (response.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                return response;

            // Get the decoded stream
            await using var stream = response.Stream;

            // Get yenc headers before caching the decoded stream
            var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (yencHeaders == null)
            {
                throw new InvalidOperationException($"Failed to read yenc headers for segment {segmentId}");
            }

            var sizeBytes = await CacheDecodedStreamAsync(segmentId, stream, cancellationToken).ConfigureAwait(false);
            MarkFetched(segmentId);

            // Mark as cached with both yenc and article headers
            _cachedSegments.TryAdd(segmentId, new CacheEntry
            {
                YencHeaders = yencHeaders,
                HasArticleHeaders = true,
                ArticleHeaders = response.ArticleHeaders,
                SizeBytes = sizeBytes
            });
            await UpdateCacheSizeAndEvictAsync(sizeBytes, segmentId, cancellationToken).ConfigureAwait(false);

            // Return a new stream from the cached file
            return ReadCachedArticleAsync(segmentId, yencHeaders, response.ArticleHeaders);
        }
        finally
        {
            semaphore.Release();
            if (!_cachedSegments.ContainsKey(segmentId))
                _pendingRequests.TryRemove(segmentId, out _);
        }
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cachedSegments.ContainsKey(segmentId)
                ? new UsenetExclusiveConnection(onConnectionReadyAgain: null)
                : await base.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);
        }
        finally
        {
            semaphore.Release();
            if (!_cachedSegments.ContainsKey(segmentId))
                _pendingRequests.TryRemove(segmentId, out _);
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        if (!_cachedSegments.TryGetValue(segmentId, out var existingEntry))
            return base.GetYencHeadersAsync(segmentId, ct);

        TouchCacheEntry(existingEntry);
        return Task.FromResult(existingEntry.YencHeaders);
    }

    public IReadOnlyCollection<string> GetCachedSegmentIdsSnapshot()
    {
        return _cachedSegments.Keys.ToArray();
    }

    public IReadOnlyCollection<string> GetFetchedSegmentIdsSnapshot()
    {
        return _fetchedSegmentIds.Keys.ToArray();
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

        var results = new SegmentCheckResult[segmentList.Count];
        var uncachedSegments = new List<(int Index, string SegmentId)>(segmentList.Count);
        var completed = 0;

        for (var i = 0; i < segmentList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentId = segmentList[i];
            if (IsSegmentKnownFetched(segmentId)
                || await IsSegmentCachedAsync(segmentId, cancellationToken).ConfigureAwait(false))
            {
                results[i] = new SegmentCheckResult(segmentId, SegmentCheckState.Exists, Provider: null, Error: null);
                progress?.Report(++completed);
                continue;
            }

            uncachedSegments.Add((i, segmentId));
        }

        if (uncachedSegments.Count == 0)
            return SegmentCheckBatch.AllExists(segmentList);

        var progressOffset = completed;
        var uncachedProgress = progress == null
            ? null
            : new OffsetProgress(progress, progressOffset);
        var uncachedBatch = await base
            .CheckSegmentsAsync(
                uncachedSegments.Select(x => x.SegmentId),
                concurrency,
                uncachedProgress,
                cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < uncachedSegments.Count; i++)
            results[uncachedSegments[i].Index] = uncachedBatch.Results[i];

        return CreateSegmentCheckBatch(segmentList, results);
    }

    private async Task<long> CacheDecodedStreamAsync(string segmentId, YencStream stream,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(segmentId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir);
                await using var fileStream = new FileStream(
                    cachePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return fileStream.Length;
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
                // A temp/cache cleanup can race between CreateDirectory and opening the file.
            }
        }

        throw new DirectoryNotFoundException($"Could not create article cache path `{cachePath}`.");
    }

    private sealed class OffsetProgress(IProgress<int> inner, int offset) : IProgress<int>
    {
        public void Report(int value)
        {
            inner.Report(offset + value);
        }
    }

    private UsenetDecodedBodyResponse ReadCachedBodyAsync(string segmentId, UsenetYencHeader yencHeaders)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedBodyResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 - Article retrieved from file cache",
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private UsenetDecodedArticleResponse ReadCachedArticleAsync(
        string segmentId, UsenetYencHeader yencHeaders, UsenetArticleHeader articleHeaders)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        return new UsenetDecodedArticleResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "220 - Article retrieved from cache",
            ArticleHeaders = articleHeaders,
            Stream = new CachedYencStream(yencHeaders, fileStream)
        };
    }

    private string GetCachePath(string segmentId)
    {
        // Use SHA256 hash of segment ID to create a valid filename
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var filename = Convert.ToHexString(hash);
        return Path.Combine(_cacheDir, filename);
    }

    private static void TouchCacheEntry(CacheEntry cacheEntry)
    {
        cacheEntry.LastAccessUtc = DateTime.UtcNow;
    }

    private void MarkFetched(string segmentId)
    {
        if (_maxFetchedSegmentIds == 0) return;
        if (!_fetchedSegmentIds.TryAdd(segmentId, 0)) return;

        _fetchedSegmentOrder.Enqueue(segmentId);
        while (_fetchedSegmentIds.Count > _maxFetchedSegmentIds
               && _fetchedSegmentOrder.TryDequeue(out var oldestSegmentId))
        {
            _fetchedSegmentIds.TryRemove(oldestSegmentId, out _);
        }
    }

    private async Task UpdateCacheSizeAndEvictAsync
    (
        long addedBytes,
        string currentSegmentId,
        CancellationToken ct
    )
    {
        await _cacheSizeSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cacheBytes += addedBytes;
            _sharedBudget.Add(addedBytes);
            if (_cacheBytes <= _maxCacheBytes && !_sharedBudget.IsOverBudget) return;

            var candidates = _cachedSegments
                .Where(x => x.Key != currentSegmentId)
                .OrderBy(x => x.Value.LastAccessUtc)
                .ToList();

            foreach (var (segmentId, cacheEntry) in candidates)
            {
                if (_cacheBytes <= _maxCacheBytes && !_sharedBudget.IsOverBudget) break;
                if (!_cachedSegments.TryRemove(segmentId, out _)) continue;

                _cacheBytes -= cacheEntry.SizeBytes;
                _sharedBudget.Remove(cacheEntry.SizeBytes);
                _pendingRequests.TryRemove(segmentId, out _);
                TryDeleteCachedFile(segmentId);
            }
        }
        finally
        {
            _cacheSizeSemaphore.Release();
        }
    }

    private void TryDeleteCachedFile(string segmentId)
    {
        try
        {
            File.Delete(GetCachePath(segmentId));
        }
        catch
        {
            // The temp cache directory is deleted on disposal; failed per-file eviction is recoverable.
        }
    }

    private async Task<bool> IsSegmentCachedAsync(string segmentId, CancellationToken ct)
    {
        foreach (var candidateSegmentId in NzbSegmentIdSet.Decode(segmentId))
        {
            if (!_cachedSegments.ContainsKey(candidateSegmentId)) continue;
            if (File.Exists(GetCachePath(candidateSegmentId)))
                return true;

            await RemoveCachedEntryAsync(candidateSegmentId, ct).ConfigureAwait(false);
        }

        return false;
    }

    private bool IsSegmentKnownFetched(string segmentId)
    {
        return NzbSegmentIdSet
            .Decode(segmentId)
            .Any(candidateSegmentId => _fetchedSegmentIds.ContainsKey(candidateSegmentId));
    }

    private async Task RemoveCachedEntryAsync(string segmentId, CancellationToken ct)
    {
        await _cacheSizeSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_cachedSegments.TryRemove(segmentId, out var cacheEntry)) return;

            _cacheBytes = Math.Max(0, _cacheBytes - cacheEntry.SizeBytes);
            _sharedBudget.Remove(cacheEntry.SizeBytes);
            _pendingRequests.TryRemove(segmentId, out _);
        }
        finally
        {
            _cacheSizeSemaphore.Release();
        }

        TryDeleteCachedFile(segmentId);
    }

    private static bool IsStaleCacheFileException(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException or IOException;

    public override void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose the underlying client
        // only when leaveOpen is false.
        if (!leaveOpen)
            base.Dispose();

        // Clean up semaphores
        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();

        _pendingRequests.Clear();
        _cachedSegments.Clear();
        _fetchedSegmentIds.Clear();
        while (_fetchedSegmentOrder.TryDequeue(out _))
        {
        }
        _cacheSizeSemaphore.Dispose();
        _sharedBudget.Remove(_cacheBytes);
        _cacheBytes = 0;

        await DeleteCacheDir(_cacheDir).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static async Task DeleteCacheDir(string cacheDir)
    {
        if (!Directory.Exists(cacheDir)) return;

        var ct = SigtermUtil.GetCancellationToken();
        var delay = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = Math.Min(delay * 2, 10000);
            }
        }
    }
}

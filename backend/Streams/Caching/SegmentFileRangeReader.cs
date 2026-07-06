using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using UsenetSharp.Models;

namespace NzbWebDAV.Streams.Caching;

public sealed class SegmentFileRangeReader(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize
) : IFileRangeReader
{
    private const int RangePrefetchOvershootSegments = 4;
    private readonly string[] _fileSegmentIds = NormalizeSegmentIds(fileSegmentIds);
    private readonly bool _hasRequiredSegmentMetadata = fileSize <= 0 || NormalizeSegmentIds(fileSegmentIds).Length > 0;
    private readonly Dictionary<string, Lazy<Task<UsenetYencHeader>>> _yencHeaderCache = new(StringComparer.Ordinal);
    private readonly object _yencHeaderCacheLock = new();
    private bool _checkedCachedMissingSegments;

    public long Length => fileSize;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        EnsureRequiredSegmentMetadata();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= fileSize || buffer.Length == 0) return 0;
        EnsureNoKnownMissingSegments();

        var bytesToRead = (int)Math.Min(buffer.Length, fileSize - offset);
        await using var stream = await GetFileStream(offset, bytesToRead, ct).ConfigureAwait(false);

        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await stream.ReadAsync(buffer[totalRead..bytesToRead], ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException(
                    $"Segment stream ended before satisfying range read at offset {offset + totalRead}.");
            }

            totalRead += read;
        }

        return totalRead;
    }

    private async Task<Stream> GetFileStream(long rangeStart, int bytesToRead, CancellationToken ct)
    {
        if (rangeStart == 0)
        {
            var initialEndSegmentCount = await ComputeEndSegmentCountAsync(
                    0,
                    _fileSegmentIds.Length,
                    rangeStart,
                    bytesToRead,
                    ct)
                .ConfigureAwait(false);
            return GetMultiSegmentStream(0, initialEndSegmentCount, ct);
        }

        var foundSegment = await SeekSegment(rangeStart, ct).ConfigureAwait(false);
        var endSegmentCount = await ComputeEndSegmentCountAsync(
                foundSegment.FoundIndex,
                _fileSegmentIds.Length - foundSegment.FoundIndex,
                rangeStart,
                bytesToRead,
                ct)
            .ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, endSegmentCount, ct);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, ct)
            .ConfigureAwait(false);
        return stream;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, _fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async guess =>
            {
                var header = await GetCachedYencHeaderAsync(_fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<UsenetYencHeader> GetCachedYencHeaderAsync(string segmentId, CancellationToken ct)
    {
        Lazy<Task<UsenetYencHeader>> lazy;
        lock (_yencHeaderCacheLock)
        {
            if (!_yencHeaderCache.TryGetValue(segmentId, out lazy!))
            {
                lazy = new Lazy<Task<UsenetYencHeader>>(
                    () => usenetClient.GetYencHeadersAsync(segmentId, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _yencHeaderCache[segmentId] = lazy;
            }
        }

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (lazy.IsValueCreated
                && lazy.Value.IsCompleted
                && !lazy.Value.IsCompletedSuccessfully)
            {
                lock (_yencHeaderCacheLock)
                {
                    if (_yencHeaderCache.TryGetValue(segmentId, out var cached) && ReferenceEquals(cached, lazy))
                        _yencHeaderCache.Remove(segmentId);
                }
            }

            throw;
        }
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, int? endSegmentCount, CancellationToken ct)
    {
        var segmentIds = _fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(segmentIds, usenetClient, articleBufferSize, ct, endSegmentCount);
    }

    private void EnsureNoKnownMissingSegments()
    {
        if (_checkedCachedMissingSegments) return;
        HealthCheckService.CheckCachedMissingSegmentIds(_fileSegmentIds);
        _checkedCachedMissingSegments = true;
    }

    private async Task<int?> ComputeEndSegmentCountAsync(
        int firstSegmentIndex,
        int remainingSegmentCount,
        long rangeStart,
        int bytesToRead,
        CancellationToken ct)
    {
        if (remainingSegmentCount <= 0) return 0;
        if (_fileSegmentIds.Length == 0) return null;
        if (bytesToRead <= 0) return 0;

        var endByte = Math.Clamp(rangeStart + bytesToRead - 1, 0, fileSize - 1);
        if (endByte >= fileSize - 1) return null;

        var endSegment = await SeekSegment(endByte, ct).ConfigureAwait(false);
        var withOvershoot = endSegment.FoundIndex + RangePrefetchOvershootSegments;
        var relativeCount = withOvershoot - firstSegmentIndex + 1;
        if (relativeCount <= 0) return 0;
        if (relativeCount >= remainingSegmentCount) return null;
        return relativeCount;
    }

    private void EnsureRequiredSegmentMetadata()
    {
        if (_hasRequiredSegmentMetadata) return;

        throw new InvalidDataException("Cannot stream a non-empty file because segment metadata is missing.");
    }

    private static string[] NormalizeSegmentIds(IEnumerable<string>? segmentIds)
    {
        return segmentIds?
            .Where(segmentId => !string.IsNullOrWhiteSpace(segmentId))
            .ToArray() ?? [];
    }
}

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams.Caching;

public sealed class SegmentFileRangeReader(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize
) : IFileRangeReader
{
    private const int RangePrefetchOvershootSegments = 4;
    private bool _checkedCachedMissingSegments;

    public long Length => fileSize;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= fileSize || buffer.Length == 0) return 0;
        EnsureNoKnownMissingSegments();

        var bytesToRead = (int)Math.Min(buffer.Length, fileSize - offset);
        await using var stream = await GetFileStream(offset, bytesToRead, ct).ConfigureAwait(false);

        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await stream.ReadAsync(buffer[totalRead..bytesToRead], ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    private async Task<Stream> GetFileStream(long rangeStart, int bytesToRead, CancellationToken ct)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, rangeStart, bytesToRead, ct);

        var foundSegment = await SeekSegment(rangeStart, ct).ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, rangeStart, bytesToRead, ct);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, ct)
            .ConfigureAwait(false);
        return stream;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async guess =>
            {
                var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, long rangeStart, int bytesToRead, CancellationToken ct)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        var endSegmentCount = ComputeEndSegmentCount(firstSegmentIndex, segmentIds.Length, rangeStart, bytesToRead);
        return MultiSegmentStream.Create(segmentIds, usenetClient, articleBufferSize, ct, endSegmentCount);
    }

    private void EnsureNoKnownMissingSegments()
    {
        if (_checkedCachedMissingSegments) return;
        HealthCheckService.CheckCachedMissingSegmentIds(fileSegmentIds);
        _checkedCachedMissingSegments = true;
    }

    private int? ComputeEndSegmentCount(int firstSegmentIndex, int remainingSegmentCount, long rangeStart, int bytesToRead)
    {
        if (fileSegmentIds.Length == 0 || remainingSegmentCount <= 0) return null;
        if (bytesToRead <= 0) return 0;

        var endByte = Math.Clamp(rangeStart + bytesToRead - 1, 0, fileSize - 1);
        var avgSegmentSize = (double)fileSize / fileSegmentIds.Length;
        if (avgSegmentSize <= 0) return null;

        var absoluteEndIndex = Math.Clamp(
            (int)(endByte / avgSegmentSize), 0, fileSegmentIds.Length - 1);
        var withOvershoot = absoluteEndIndex + RangePrefetchOvershootSegments;
        var relativeCount = withOvershoot - firstSegmentIndex + 1;
        if (relativeCount <= 0) return 0;
        if (relativeCount >= remainingSegmentCount) return null;
        return relativeCount;
    }

}

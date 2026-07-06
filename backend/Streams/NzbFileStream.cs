using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams.Caching;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize,
    long? requestedEndByte = null,
    SparseSegmentCacheOptions? cacheOptions = null
) : FastReadOnlyStream
{
    // Extra segments fetched past the requested end to cover seek imprecision.
    private const int RangePrefetchOvershootSegments = 4;
    private readonly string[] _fileSegmentIds = NormalizeSegmentIds(fileSegmentIds);
    private readonly bool _hasRequiredSegmentMetadata = fileSize <= 0 || NormalizeSegmentIds(fileSegmentIds).Length > 0;
    private readonly long _readLimitExclusive = Math.Clamp(requestedEndByte.HasValue ? requestedEndByte.Value + 1 : fileSize, 0, fileSize);

    private long _position;
    private bool _disposed;
    private bool _checkedCachedMissingSegments;
    private Stream? _innerStream;
    private readonly IFileRangeReader? _rangeReader = CreateRangeReader(
        NormalizeSegmentIds(fileSegmentIds),
        fileSize,
        usenetClient,
        articleBufferSize,
        requestedEndByte,
        cacheOptions);

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureRequiredSegmentMetadata();
        if (buffer.Length == 0) return 0;
        if (_position >= _readLimitExclusive) return 0;
        EnsureNoKnownMissingSegments();
        if (_rangeReader != null)
        {
            if (_position >= _rangeReader.Length) return 0;
            var bytesToRead = (int)Math.Min(buffer.Length, _rangeReader.Length - _position);
            var readFromCache = await _rangeReader
                .ReadAtAsync(_position, buffer[..bytesToRead], cancellationToken)
                .ConfigureAwait(false);
            _position += readFromCache;
            return readFromCache;
        }

        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var directBytesToRead = (int)Math.Min(buffer.Length, _readLimitExclusive - _position);
        var read = await _innerStream.ReadAsync(buffer[..directBytesToRead], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new IOException($"Nzb file stream ended before declared file length at offset {_position}.");
        }

        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (absoluteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot seek before the beginning of the stream.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        if (_rangeReader == null)
        {
            _innerStream?.Dispose();
            _innerStream = null;
        }

        return _position;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, _fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                var header = await usenetClient.GetYencHeadersAsync(_fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        EnsureRequiredSegmentMetadata();
        if (rangeStart == 0)
        {
            var initialEndSegmentCount = await ComputeEndSegmentCountAsync(
                    0,
                    _fileSegmentIds.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            return GetMultiSegmentStream(0, initialEndSegmentCount, cancellationToken);
        }

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var endSegmentCount = await ComputeEndSegmentCountAsync(
                foundSegment.FoundIndex,
                _fileSegmentIds.Length - foundSegment.FoundIndex,
                cancellationToken)
            .ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, endSegmentCount, cancellationToken);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, cancellationToken)
            .ConfigureAwait(false);
        return stream;
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, int? endSegmentCount, CancellationToken cancellationToken)
    {
        var segmentIds = _fileSegmentIds.AsMemory()[firstSegmentIndex..];
        if (endSegmentCount.HasValue)
        {
            Log.Debug(
                "Range-bounded prefetch: requestedEndByte={EndByte}, firstSegmentIndex={First}, "
                + "endSegmentCount={Count} (of {Remaining} remaining segments)",
                requestedEndByte, firstSegmentIndex, endSegmentCount.Value, segmentIds.Length);
        }
        return MultiSegmentStream.Create(
            segmentIds, usenetClient, articleBufferSize, cancellationToken, endSegmentCount);
    }

    // Returns segment count covering requestedEndByte, or null when no cap is needed.
    private async Task<int?> ComputeEndSegmentCountAsync(
        int firstSegmentIndex,
        int remainingSegmentCount,
        CancellationToken ct)
    {
        if (!requestedEndByte.HasValue) return null;
        if (_fileSegmentIds.Length == 0 || remainingSegmentCount <= 0) return null;

        var endByte = Math.Clamp(requestedEndByte.Value, 0, fileSize - 1);
        if (endByte >= fileSize - 1) return null;

        var endSegment = await SeekSegment(endByte, ct).ConfigureAwait(false);
        var withOvershoot = endSegment.FoundIndex + RangePrefetchOvershootSegments;
        var relativeCount = withOvershoot - firstSegmentIndex + 1;
        if (relativeCount <= 0) return 0;
        if (relativeCount >= remainingSegmentCount) return null;
        return relativeCount;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        if (_rangeReader is IDisposable disposable) disposable.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        if (_rangeReader is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_rangeReader is IDisposable disposable)
            disposable.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static IFileRangeReader? CreateRangeReader
    (
        string[] segmentIds,
        long length,
        INntpClient client,
        int bufferSize,
        long? endByte,
        SparseSegmentCacheOptions? options
    )
    {
        if (options is not { Enabled: true }) return null;
        // The sparse cache already has bounded read-ahead. Keep source reads
        // sequential so every cache chunk does not multiply buffered article
        // streams and retain decoded segment buffers under rclone range storms.
        var inner = new SegmentFileRangeReader(segmentIds, length, client, articleBufferSize: 0);
        var key = SparseSegmentCacheManager.CreateKey(segmentIds, length);
        var readLimitExclusive = endByte.HasValue ? Math.Clamp(endByte.Value + 1, 0, length) : length;
        return SparseSegmentCacheManager.Shared.Open(key, inner, options, readLimitExclusive);
    }

    private void EnsureNoKnownMissingSegments()
    {
        if (_checkedCachedMissingSegments) return;
        HealthCheckService.CheckCachedMissingSegmentIds(_fileSegmentIds);
        _checkedCachedMissingSegments = true;
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

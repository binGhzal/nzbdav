using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Streams.Caching;

public sealed class DavMultipartFileRangeReader : IFileRangeReader, IDisposable, IAsyncDisposable
{
    private readonly IFileRangeReader _reader;
    private bool _disposed;

    public DavMultipartFileRangeReader(
        DavMultipartFile.FilePart[] fileParts,
        INntpClient usenetClient,
        int articleBufferSize,
        long? requestedEndByte = null,
        SparseSegmentCacheOptions? cacheOptions = null)
    {
        fileParts ??= [];
        var inner = new UncachedDavMultipartFileRangeReader(fileParts, usenetClient, articleBufferSize);
        if (cacheOptions is { Enabled: true })
        {
            var readLimitExclusive = requestedEndByte.HasValue
                ? Math.Clamp(requestedEndByte.Value + 1, 0, inner.Length)
                : inner.Length;
            _reader = SparseSegmentCacheManager.Shared.Open(
                CreateKey(fileParts, inner.Length),
                inner,
                cacheOptions,
                readLimitExclusive);
        }
        else
        {
            _reader = inner;
        }
    }

    public long Length => _reader.Length;

    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _reader.ReadAtAsync(offset, buffer, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_reader is IDisposable disposable) disposable.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_reader is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_reader is IDisposable disposable)
            disposable.Dispose();
    }

    private static string CreateKey(DavMultipartFile.FilePart[] fileParts, long length)
    {
        using var sha256 = SHA256.Create();
        AddLong(sha256, length);
        foreach (var part in fileParts)
        {
            AddRange(sha256, part.SegmentIdByteRange);
            AddRange(sha256, part.FilePartByteRange);
            var segmentIds = part.SegmentIds ?? [];
            AddLong(sha256, segmentIds.Length);
            foreach (var segmentId in segmentIds)
                AddString(sha256, segmentId);

            var segmentSlices = part.SegmentSlices ?? [];
            AddLong(sha256, segmentSlices.Length);
            foreach (var slice in segmentSlices)
            {
                AddString(sha256, slice.SegmentId);
                AddRange(sha256, slice.SegmentByteRange);
                AddRange(sha256, slice.FilePartByteRange);
            }
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private static void AddRange(HashAlgorithm hash, LongRange range)
    {
        AddLong(hash, range.StartInclusive);
        AddLong(hash, range.EndExclusive);
    }

    private static void AddLong(HashAlgorithm hash, long value)
    {
        var bytes = BitConverter.GetBytes(value);
        hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static void AddString(HashAlgorithm hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
        hash.TransformBlock([0], 0, 1, null, 0);
    }

    private sealed class UncachedDavMultipartFileRangeReader(
        DavMultipartFile.FilePart[] fileParts,
        INntpClient usenetClient,
        int articleBufferSize
    ) : IFileRangeReader
    {
        private readonly PartWindow[] _partWindows = CreatePartWindows(fileParts);
        private readonly string[] _allSegmentIds = GetAllSegmentIds(fileParts);
        private bool _checkedCachedMissingSegments;

        public long Length { get; } = fileParts.Select(x => x.FilePartByteRange.Count).Sum();

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset >= Length || buffer.Length == 0) return 0;
            EnsureNoKnownMissingSegments();

            var remaining = (int)Math.Min(buffer.Length, Length - offset);
            var totalRead = 0;
            while (totalRead < remaining)
            {
                ct.ThrowIfCancellationRequested();
                var absoluteOffset = offset + totalRead;
                var partWindow = FindPartWindow(absoluteOffset);
                if (partWindow == null) break;

                var localOffset = absoluteOffset - partWindow.StartInclusive;
                var bytesFromPart = (int)Math.Min(
                    remaining - totalRead,
                    partWindow.Part.FilePartByteRange.Count - localOffset);
                var read = await ReadPartAtAsync(
                        partWindow.Part,
                        localOffset,
                        buffer.Slice(totalRead, bytesFromPart),
                        ct)
                    .ConfigureAwait(false);
                if (read <= 0) break;
                totalRead += read;
            }

            return totalRead;
        }

        private async ValueTask<int> ReadPartAtAsync(
            DavMultipartFile.FilePart part,
            long partOffset,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            if (part.SegmentSlices is { Length: > 0 })
                return await ReadSlicedPartAtAsync(part, partOffset, buffer, ct).ConfigureAwait(false);

            return await ReadLegacyPartAtAsync(part, partOffset, buffer, ct).ConfigureAwait(false);
        }

        private async ValueTask<int> ReadSlicedPartAtAsync(
            DavMultipartFile.FilePart part,
            long partOffset,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            var wantedRange = LongRange.FromStartAndSize(partOffset, buffer.Length);
            var totalRead = 0;
            foreach (var slice in part.SegmentSlices.OrderBy(x => x.FilePartByteRange.StartInclusive))
            {
                var overlap = GetOverlap(slice.FilePartByteRange, wantedRange);
                if (overlap == null) continue;

                var destinationOffset = (int)(overlap.StartInclusive - partOffset);
                var read = await ReadSliceAtAsync(
                        slice,
                        overlap,
                        buffer.Slice(destinationOffset, (int)overlap.Count),
                        ct)
                    .ConfigureAwait(false);
                totalRead = Math.Max(totalRead, destinationOffset + read);
                if (totalRead >= buffer.Length) break;
                if (read <= 0) break;
            }

            return totalRead;
        }

        private async ValueTask<int> ReadSliceAtAsync(
            DavMultipartFile.SegmentSlice slice,
            LongRange wantedRange,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            var offsetWithinSlice = wantedRange.StartInclusive - slice.FilePartByteRange.StartInclusive;
            var segmentOffset = slice.SegmentByteRange.StartInclusive + offsetWithinSlice;
            await using var stream = MultiSegmentStream.Create(
                new[] { slice.SegmentId }.AsMemory(),
                usenetClient,
                articleBufferSize,
                ct,
                1);
            await stream.DiscardBytesAsync(segmentOffset, ct).ConfigureAwait(false);
            return await ReadFullyOrUntilEndAsync(stream, buffer, ct).ConfigureAwait(false);
        }

        private async ValueTask<int> ReadLegacyPartAtAsync(
            DavMultipartFile.FilePart part,
            long partOffset,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            var partEndExclusive = Math.Min(partOffset + buffer.Length, part.FilePartByteRange.Count);
            var requestedPartEndByte = partEndExclusive < part.FilePartByteRange.Count
                ? part.FilePartByteRange.StartInclusive + partEndExclusive - 1
                : (long?)null;
            await using var stream = usenetClient.GetFileStream(
                part.SegmentIds ?? [],
                part.SegmentIdByteRange.Count,
                articleBufferSize,
                requestedPartEndByte);
            stream.Seek(part.FilePartByteRange.StartInclusive + partOffset, SeekOrigin.Begin);
            return await ReadFullyOrUntilEndAsync(stream, buffer, ct).ConfigureAwait(false);
        }

        private PartWindow? FindPartWindow(long offset)
        {
            foreach (var partWindow in _partWindows)
            {
                if (offset >= partWindow.StartInclusive && offset < partWindow.EndExclusive)
                    return partWindow;
            }

            return null;
        }

        private static PartWindow[] CreatePartWindows(DavMultipartFile.FilePart[] parts)
        {
            var windows = new PartWindow[parts.Length];
            long offset = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var start = offset;
                offset += part.FilePartByteRange.Count;
                windows[i] = new PartWindow(part, start, offset);
            }

            return windows;
        }

        private static string[] GetAllSegmentIds(DavMultipartFile.FilePart[] parts)
        {
            return parts.SelectMany(part =>
                    part.SegmentSlices is { Length: > 0 }
                        ? part.SegmentSlices.Select(slice => slice.SegmentId)
                        : part.SegmentIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private void EnsureNoKnownMissingSegments()
        {
            if (_checkedCachedMissingSegments) return;
            HealthCheckService.CheckCachedMissingSegmentIds(_allSegmentIds);
            _checkedCachedMissingSegments = true;
        }

        private static LongRange? GetOverlap(LongRange first, LongRange second)
        {
            var start = Math.Max(first.StartInclusive, second.StartInclusive);
            var end = Math.Min(first.EndExclusive, second.EndExclusive);
            return start < end ? new LongRange(start, end) : null;
        }

        private static async ValueTask<int> ReadFullyOrUntilEndAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            return totalRead;
        }

        private sealed record PartWindow(
            DavMultipartFile.FilePart Part,
            long StartInclusive,
            long EndExclusive);
    }
}

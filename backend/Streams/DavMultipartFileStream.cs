using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream(
    DavMultipartFile.FilePart[] fileParts,
    INntpClient usenetClient,
    int articleBufferSize,
    long? requestedEndByte = null
) : Stream
{
    private long _position = 0;
    private CombinedStream? _innerStream;
    private bool _disposed;


    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_innerStream == null) _innerStream = GetFileStream(_position);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; } = fileParts.Select(x => x.FilePartByteRange.Count).Sum();

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    private (int filePartIndex, long filePartOffset) SeekFilePart(long byteOffset)
    {
        long offset = 0;
        for (var i = 0; i < fileParts.Length; i++)
        {
            var filePart = fileParts[i];
            var nextOffset = offset + filePart.FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return (i, offset);
            offset = nextOffset;
        }

        throw new SeekPositionNotFoundException($"Corrupt file. Cannot seek to byte position {byteOffset}.");
    }

    private CombinedStream GetFileStream(long rangeStart)
    {
        if (rangeStart == 0)
        {
            return GetCombinedStream(0, 0, 0);
        }

        var (filePartIndex, filePartOffset) = SeekFilePart(rangeStart);
        return GetCombinedStream(filePartIndex, rangeStart - filePartOffset, filePartOffset);
    }

    private CombinedStream GetCombinedStream(int firstFilePartIndex, long additionalOffset, long firstPartFileStart)
    {
        var parts = fileParts[firstFilePartIndex..];

        // File-absolute start of each part in the slice, for translating requestedEndByte per part.
        var partFileStarts = new long[parts.Length];
        var running = firstPartFileStart;
        for (var i = 0; i < parts.Length; i++)
        {
            partFileStarts[i] = running;
            running += parts[i].FilePartByteRange.Count;
        }

        var streams = parts
            .Select((x, i) =>
            {
                var offset = (i == 0) ? additionalOffset : 0;
                var stream = usenetClient.GetFileStream(
                    x.SegmentIds, x.SegmentIdByteRange.Count, articleBufferSize, GetPartEndByte(x, partFileStarts[i]));
                stream.Seek(x.FilePartByteRange.StartInclusive + offset, SeekOrigin.Begin);
                return Task.FromResult(stream.LimitLength(x.FilePartByteRange.Count - offset));
            });
        return new CombinedStream(streams);
    }

    // Translates the file-absolute requested end byte into this part's segment-data coordinate so the
    // part's prefetch stops at the requested end. Null = no cap (the whole part is within the request).
    private long? GetPartEndByte(DavMultipartFile.FilePart part, long partFileStart)
    {
        if (!requestedEndByte.HasValue)
        {
            return null;
        }

        var lastWantedByteInPart = requestedEndByte.Value - partFileStart;
        if (lastWantedByteInPart < 0 || lastWantedByteInPart >= part.FilePartByteRange.Count - 1)
        {
            return null;
        }

        return part.FilePartByteRange.StartInclusive + lastWantedByteInPart;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams.Caching;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream(
    DavMultipartFile.FilePart[] fileParts,
    INntpClient usenetClient,
    int articleBufferSize,
    long? requestedEndByte = null,
    SparseSegmentCacheOptions? cacheOptions = null
) : Stream
{
    private long _position = 0;
    private readonly IFileRangeReader _rangeReader = new DavMultipartFileRangeReader(
        fileParts,
        usenetClient,
        articleBufferSize,
        requestedEndByte,
        cacheOptions);
    private bool _disposed;


    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _rangeReader.ReadAtAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
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
    public override long Length => _rangeReader.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing && _rangeReader is IDisposable disposable)
            disposable.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_rangeReader is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_rangeReader is IDisposable disposable)
            disposable.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

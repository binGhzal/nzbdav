using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class UnbufferedMultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private Stream? _stream;
    private int _currentIndex;
    private bool _disposed;


    public UnbufferedMultiSegmentStream(Memory<string> segmentIds, INntpClient usenetClient)
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (_currentIndex >= _segmentIds.Length) return 0;
                var segmentId = _segmentIds.Span[_currentIndex];
                try
                {
                    var body = await _usenetClient
                        .DecodedBodyWithFallbackAsync(segmentId, cancellationToken)
                        .ConfigureAwait(false);
                    _currentIndex++;
                    _stream = body.Stream;
                }
                catch (UsenetArticleNotFoundException e)
                {
                    HealthCheckService.RememberMissingSegmentId(e.SegmentId);
                    throw;
                }
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _stream?.Dispose();
        base.Dispose();
    }
}

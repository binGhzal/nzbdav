using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly Channel<Task<Stream>> _streamTasks;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly Task _downloadTask;
    private readonly int? _endSegmentCount;
    private Stream? _stream;
    private bool _disposed;

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken cancellationToken,
        int? endSegmentCount = null
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(segmentIds, usenetClient)
            : new MultiSegmentStream(segmentIds, usenetClient, articleBufferSize, cancellationToken, endSegmentCount);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken cancellationToken,
        int? endSegmentCount = null
    )
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _streamTasks = Channel.CreateBounded<Task<Stream>>(articleBufferSize);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _endSegmentCount = endSegmentCount;
        _downloadTask = DownloadSegments(_cts.Token);
    }

    private async Task DownloadSegments(CancellationToken cancellationToken)
    {
        var effectiveCount = _endSegmentCount.HasValue
            ? Math.Min(_segmentIds.Length, _endSegmentCount.Value)
            : _segmentIds.Length;
        try
        {
            for (var i = 0; i < effectiveCount; i++)
            {
                var segmentId = _segmentIds.Span[i];

                await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
                var streamTask = DownloadSegment(segmentId, cancellationToken);
                if (_streamTasks.Writer.TryWrite(streamTask)) continue;

                // if we never get a chance to write the stream to the writer
                // then make sure the stream gets disposed.
                ObserveAndDisposeStreamTask(streamTask);
                break;
            }
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }

        return;
    }

    private async Task<Stream> DownloadSegment
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var bodyResponse = await _usenetClient
                .DecodedBodyWithFallbackAsync(
                    segmentId,
                    cancellationToken,
                    (candidateSegmentId, ct) => _usenetClient.AcquireExclusiveConnectionAsync(candidateSegmentId, ct)
                )
                .ConfigureAwait(false);
            return bodyResponse.Stream;
        }
        catch (UsenetArticleNotFoundException e)
        {
            HealthCheckService.RememberMissingSegmentId(e.SegmentId);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_streamTasks.Reader.TryRead(out var streamTask)) return 0;
                _stream = await streamTask;
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }

        return 0;
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
        _cts.Cancel();
        _cts.Dispose();
        _stream?.Dispose();
        _streamTasks.Writer.TryComplete();

        // ensure that streams that were never read from the channel get disposed
        while (_streamTasks.Reader.TryRead(out var streamTask))
            ObserveAndDisposeStreamTask(streamTask);

        _ = ObserveProducerTask(_downloadTask);

        base.Dispose();
    }

    private static void ObserveAndDisposeStreamTask(Task<Stream> streamTask)
    {
        _ = DisposeStreamTaskAsync(streamTask);
    }

    private static async Task DisposeStreamTaskAsync(Task<Stream> streamTask)
    {
        try
        {
            var stream = await streamTask.ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The owning stream is already closing; failed cleanup is recoverable.
        }
    }

    private static async Task ObserveProducerTask(Task producerTask)
    {
        try
        {
            await producerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The producer can fault while cancellation is unwinding.
        }
    }
}

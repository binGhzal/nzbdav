namespace NzbWebDAV.Streams.Caching;

public sealed class StreamFileRangeReader(Stream stream) : IFileRangeReader, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public long Length => stream.Length;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= Length || buffer.Length == 0) return 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stream.Seek(offset, SeekOrigin.Begin);
            var remaining = (int)Math.Min(buffer.Length, Length - offset);
            var totalRead = 0;
            while (totalRead < remaining)
            {
                var read = await stream.ReadAsync(buffer.Slice(totalRead, remaining - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException($"Source stream ended before satisfying range read at offset {offset + totalRead}.");
                }
                totalRead += read;
            }

            return totalRead;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        stream.Dispose();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

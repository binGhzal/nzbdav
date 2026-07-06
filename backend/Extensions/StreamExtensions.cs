using System.Buffers;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Extensions;

public static class StreamExtensions
{
    public static Stream LimitLength(this Stream stream, long length)
    {
        return new LimitedLengthStream(stream, length);
    }

    public static async Task DiscardBytesAsync(this Stream stream, long count, CancellationToken ct = default)
    {
        if (count == 0) return;
        var remaining = count;
        long discarded = 0;
        var throwaway = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, throwaway.Length);
                var read = await stream.ReadAsync(throwaway.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException(
                        $"Source stream ended before discarding {count} byte(s). Discarded {discarded} byte(s).");

                remaining -= read;
                discarded += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    public static Stream OnDispose(this Stream stream, Action onDispose)
    {
        return new DisposableCallbackStream(stream, onDispose, async () => onDispose?.Invoke());
    }

    public static Stream OnDisposeAsync(this Stream stream, Func<ValueTask> onDisposeAsync)
    {
        return new DisposableCallbackStream(stream, onDisposeAsync: onDisposeAsync);
    }
}

using NzbWebDAV.Streams.Caching;

namespace backend.Tests.Streams.Caching;

public sealed class StreamFileRangeReaderTests
{
    [Fact]
    public async Task ReadAtAsyncSerializesConcurrentReadsAgainstSinglePositionedStream()
    {
        var stream = new SlowPositionedMemoryStream([0, 1, 2, 3, 4, 5, 6, 7]);
        await using var reader = new StreamFileRangeReader(stream);
        var first = new byte[3];
        var second = new byte[3];

        await Task.WhenAll(
            reader.ReadAtAsync(0, first, CancellationToken.None).AsTask(),
            reader.ReadAtAsync(4, second, CancellationToken.None).AsTask());

        Assert.Equal([0, 1, 2], first);
        Assert.Equal([4, 5, 6], second);
        Assert.Equal(1, stream.MaxConcurrentReads);
    }

    private sealed class SlowPositionedMemoryStream(byte[] bytes) : Stream
    {
        private int _activeReads;

        public int MaxConcurrentReads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeReads);
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, active);
            try
            {
                await Task.Delay(25, cancellationToken);
                if (Position >= bytes.Length) return 0;

                var count = (int)Math.Min(buffer.Length, bytes.Length - Position);
                bytes.AsMemory((int)Position, count).CopyTo(buffer[..count]);
                Position += count;
                return count;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
            };
            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Base;

namespace backend.Tests.WebDav;

public sealed class GetAndHeadHandlerPatchTests
{
    [Fact]
    public async Task HandleRequestServesSuffixRangeFromFileEnd()
    {
        var source = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        var handler = new GetAndHeadHandlerPatch(new FakeStore(new FakeStoreItem("movie.mkv", source)));
        var context = CreateContext("bytes=-16");

        await handler.HandleRequestAsync(context);

        var body = ((MemoryStream)context.Response.Body).ToArray();
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 1008-1023/1024", context.Response.Headers.ContentRange);
        Assert.Equal(source[^16..], body);
    }

    [Fact]
    public async Task HandleHeadUsesStoreItemSizeWithoutOpeningReadableStream()
    {
        var item = new FakeStoreItem("movie.mkv", new byte[1024]);
        var handler = new GetAndHeadHandlerPatch(new FakeStore(item));
        var context = CreateContext(rangeHeader: null);
        context.Request.Method = HttpMethods.Head;

        await handler.HandleRequestAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1024, context.Response.ContentLength);
        Assert.Equal(0, item.ReadableStreamOpenCount);
    }

    [Fact]
    public async Task HandleRequestThrowsWhenBoundedRangeSourceEndsEarly()
    {
        var handler = new GetAndHeadHandlerPatch(new FakeStore(
            new FakeStoreItem("movie.mkv", () => new TruncatedSeekableStream([1, 2, 3, 4], declaredLength: 8))));
        var context = CreateContext("bytes=0-7");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            handler.HandleRequestAsync(context));

        Assert.Contains("ended before satisfying response range", exception.Message);
    }

    private static DefaultHttpContext CreateContext(string? rangeHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/movie.mkv";
        if (rangeHeader is not null)
            context.Request.Headers.Range = rangeHeader;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeStore(IStoreItem item) : IStore
    {
        public Task<IStoreItem?> GetItemAsync(string path, CancellationToken cancellationToken)
        {
            return Task.FromResult<IStoreItem?>(item);
        }

        public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult<IStoreItem?>(item);
        }

        public Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult<IStoreCollection?>(null);
        }
    }

    private sealed class FakeStoreItem : BaseStoreReadonlyItem
    {
        private readonly Func<Stream> _openStream;

        public FakeStoreItem(string name, byte[] bytes)
            : this(name, () => new MemoryStream(bytes, writable: false), bytes.Length)
        {
        }

        public FakeStoreItem(string name, Func<Stream> openStream)
            : this(name, openStream, fileSize: 0)
        {
        }

        private FakeStoreItem(string name, Func<Stream> openStream, long fileSize)
        {
            Name = name;
            UniqueKey = name;
            FileSize = fileSize;
            _openStream = openStream;
        }

        public override string Name { get; }
        public override string UniqueKey { get; }
        public override long FileSize { get; }
        public override DateTime CreatedAt { get; } = DateTime.UtcNow;
        public int ReadableStreamOpenCount { get; private set; }

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
        {
            ReadableStreamOpenCount++;
            return Task.FromResult(_openStream());
        }
    }

    private sealed class TruncatedSeekableStream(byte[] bytes, long declaredLength) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => declaredLength;
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= bytes.Length) return ValueTask.FromResult(0);

            var count = (int)Math.Min(buffer.Length, bytes.Length - Position);
            bytes.AsMemory((int)Position, count).CopyTo(buffer[..count]);
            Position += count;
            return ValueTask.FromResult(count);
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

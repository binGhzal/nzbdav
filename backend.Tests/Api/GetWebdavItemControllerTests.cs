using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;

namespace backend.Tests.Api;

public sealed class GetWebdavItemControllerTests
{
    [Fact]
    public async Task HandleRequestCopiesResponseBodyWithStreamingSizedReads()
    {
        var source = new TrackingReadStream(length: 256 * 1024);
        var store = new FakeStore(new FakeStoreItem("movie.mkv", source));
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" }
        ]);
        var controller = new GetWebdavItemController(store, configManager);
        var context = CreateContext(".ids/movie.mkv");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleRequest();

        Assert.Equal(
            GetWebdavItemController.ResponseCopyBufferSize,
            source.MaxRequestedReadSize);
        Assert.Equal(source.Length, context.Response.Body.Length);
    }

    [Fact]
    public async Task HandleRequestReturnsRangeNotSatisfiableWhenRangeStartsPastEnd()
    {
        var source = new TrackingReadStream(length: 1024);
        var controller = CreateController(source);
        var context = CreateContext(".ids/movie.mkv", rangeHeader: "bytes=2048-");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleRequest();

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
        Assert.Equal("bytes */1024", context.Response.Headers.ContentRange);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Equal(0, source.MaxRequestedReadSize);
    }

    [Fact]
    public async Task HandleRequestClampsRangeEndToFileLength()
    {
        var source = new TrackingReadStream(length: 1024);
        var controller = CreateController(source);
        var context = CreateContext(".ids/movie.mkv", rangeHeader: "bytes=512-4096");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleRequest();

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 512-1023/1024", context.Response.Headers.ContentRange);
        Assert.Equal(512, context.Response.Body.Length);
    }

    [Fact]
    public async Task HandleRequestReturnsSuffixRangeFromFileEnd()
    {
        var source = new TrackingReadStream(length: 1024);
        var controller = CreateController(source);
        var context = CreateContext(".ids/movie.mkv", rangeHeader: "bytes=-500");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleRequest();

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 524-1023/1024", context.Response.Headers.ContentRange);
        Assert.Equal(500, context.Response.Body.Length);
        Assert.Equal(1024, source.Position);
    }

    [Fact]
    public async Task HandleRequestClampsSuffixRangeToFileLength()
    {
        var source = new TrackingReadStream(length: 1024);
        var controller = CreateController(source);
        var context = CreateContext(".ids/movie.mkv", rangeHeader: "bytes=-4096");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleRequest();

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 0-1023/1024", context.Response.Headers.ContentRange);
        Assert.Equal(1024, context.Response.Body.Length);
    }

    [Fact]
    public async Task HandleHeadRequestUsesStoreItemSizeWithoutOpeningReadableStream()
    {
        var item = new FakeStoreItem("movie.mkv", length: 1024);
        var store = new FakeStore(item);
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" }
        ]);
        var controller = new GetWebdavItemController(store, configManager);
        var context = CreateContext(".ids/movie.mkv");
        context.Request.Method = HttpMethods.Head;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleHeadRequest();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1024L, context.Response.ContentLength);
        Assert.Equal(0, item.ReadableStreamOpenCount);
    }

    [Fact]
    public async Task HandleHeadRequestSkipsPar2PreviewStreamParsing()
    {
        var item = new FakeStoreItem("repair.par2", length: 1024);
        var store = new FakeStore(item);
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" },
            new ConfigItem { ConfigName = "webdav.preview-par2-files", ConfigValue = "true" }
        ]);
        var controller = new GetWebdavItemController(store, configManager);
        var context = CreateContext(".ids/repair.par2");
        context.Request.Method = HttpMethods.Head;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        await controller.HandleHeadRequest();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1024L, context.Response.ContentLength);
        Assert.Equal(0, item.ReadableStreamOpenCount);
    }

    private static GetWebdavItemController CreateController(Stream source)
    {
        var store = new FakeStore(new FakeStoreItem("movie.mkv", source));
        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "test-strm-key" }
        ]);
        return new GetWebdavItemController(store, configManager);
    }

    private static DefaultHttpContext CreateContext(string path, string? rangeHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = $"/view/{path}";
        context.Request.QueryString = QueryString.Create(
            "downloadKey",
            GetWebdavItemRequest.GenerateDownloadKey("test-strm-key", path));
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
        private readonly Stream? _stream;
        public override string Name { get; }
        public override string UniqueKey { get; }
        public override long FileSize { get; }
        public override DateTime CreatedAt { get; } = DateTime.UtcNow;
        public int ReadableStreamOpenCount { get; private set; }

        public FakeStoreItem(string name, Stream stream)
        {
            Name = name;
            UniqueKey = name;
            _stream = stream;
            FileSize = stream.Length;
        }

        public FakeStoreItem(string name, long length)
        {
            Name = name;
            UniqueKey = name;
            FileSize = length;
        }

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
        {
            ReadableStreamOpenCount++;
            if (_stream is null)
                throw new InvalidOperationException("HEAD requests should not open the readable stream.");
            return Task.FromResult(_stream);
        }
    }

    private sealed class TrackingReadStream(long length) : Stream
    {
        private long _position;

        public int MaxRequestedReadSize { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            MaxRequestedReadSize = Math.Max(MaxRequestedReadSize, buffer.Length);
            if (_position >= Length) return 0;

            var read = (int)Math.Min(buffer.Length, Length - _position);
            buffer[..read].Clear();
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => _position
            };
            return _position;
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

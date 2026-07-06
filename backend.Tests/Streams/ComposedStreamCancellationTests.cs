using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Streams;

public sealed class ComposedStreamCancellationTests
{
    [Fact]
    public async Task CombinedStreamThrowsWhenCallerTokenIsAlreadyCanceled()
    {
        await using var stream = new CombinedStream(
            [Task.FromResult<Stream>(new MemoryStream([1]))]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task MultipartFileStreamThrowsWhenCallerTokenIsAlreadyCanceled()
    {
        var multipartFile = new MultipartFile
        {
            FileParts =
            [
                new MultipartFile.FilePart
                {
                    NzbFile = new NzbFile { Subject = "test.bin" },
                    ByteRange = new LongRange(0, 1)
                }
            ]
        };
        await using var stream = new MultipartFileStream(multipartFile, new FakeNntpClient());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], cts.Token).AsTask());
    }

    [Fact]
    public async Task MultipartFileStreamRejectsNegativeSeekWithoutChangingPosition()
    {
        var multipartFile = new MultipartFile
        {
            FileParts =
            [
                new MultipartFile.FilePart
                {
                    NzbFile = new NzbFile { Subject = "test.bin" },
                    ByteRange = new LongRange(0, 1)
                }
            ]
        };
        await using var stream = new MultipartFileStream(multipartFile, new FakeNntpClient());

        stream.Seek(1, SeekOrigin.Begin);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-2, SeekOrigin.Current));
        Assert.Equal(1, stream.Position);
    }
}

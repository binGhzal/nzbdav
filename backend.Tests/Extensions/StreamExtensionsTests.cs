using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class StreamExtensionsTests
{
    [Fact]
    public async Task DiscardBytesAsyncAdvancesByRequestedByteCount()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        await stream.DiscardBytesAsync(2, CancellationToken.None);

        Assert.Equal(3, stream.ReadByte());
    }

    [Fact]
    public async Task DiscardBytesAsyncThrowsWhenSourceEndsBeforeRequestedByteCount()
    {
        using var stream = new MemoryStream([1, 2]);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            stream.DiscardBytesAsync(4, CancellationToken.None));

        Assert.Contains("ended before discarding", exception.Message);
    }
}

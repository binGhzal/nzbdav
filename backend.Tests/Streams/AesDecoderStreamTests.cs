using NzbWebDAV.Models;

namespace backend.Tests.Streams;

public sealed class AesDecoderStreamTests
{
    [Fact]
    public void FlushIsNoOpForReadOnlyDecoderStream()
    {
        using var stream = CreateDecoderStream();

        stream.Flush();
    }

    [Fact]
    public void WriteOperationsThrowNotSupportedForReadOnlyDecoderStream()
    {
        using var stream = CreateDecoderStream();

        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    private static Stream CreateDecoderStream()
    {
        var type = Type.GetType("NzbWebDAV.Streams.AesDecoderStream, NzbWebDAV", throwOnError: true)!;
        var input = new MemoryStream(new byte[16], writable: false);
        var aesParams = new AesParams
        {
            Key = new byte[32],
            Iv = new byte[16],
            DecodedSize = 16
        };

        return Assert.IsAssignableFrom<Stream>(Activator.CreateInstance(type, input, aesParams));
    }
}

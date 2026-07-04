using System.Text;
using UsenetSharp.Streams;

namespace backend.Tests.Clients.Usenet;

public sealed class YencStreamRegressionTests
{
    [Fact]
    public async Task YencStream_ReadsSegmentsLargerThanOneMiB()
    {
        if (!OperatingSystem.IsLinux()) return;

        var expected = Enumerable.Repeat((byte)1, 1_048_576 + 257).ToArray();
        await using var encoded = CreateYencSegment(expected);
        await using var stream = new YencStream(encoded);
        var headers = await stream.GetYencHeadersAsync();
        var buffer = new byte[expected.Length];

        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead));
            if (read == 0) break;
            totalRead += read;
        }

        Assert.NotNull(headers);
        Assert.Equal(expected.Length, headers.PartSize);
        Assert.Equal(expected.Length, totalRead);
        Assert.Equal(expected, buffer);
    }

    private static MemoryStream CreateYencSegment(byte[] decoded)
    {
        var encoded = new MemoryStream();
        WriteAsciiLine(encoded, $"=ybegin line=128 size={decoded.Length} name=segment.bin");
        WriteAsciiLine(encoded, $"=ypart begin=1 end={decoded.Length}");

        var currentLineLength = 0;
        foreach (var value in decoded)
        {
            encoded.WriteByte((byte)(value + 42));
            currentLineLength++;
            if (currentLineLength < 128) continue;

            WriteAsciiLine(encoded, "");
            currentLineLength = 0;
        }

        if (currentLineLength > 0) WriteAsciiLine(encoded, "");
        WriteAsciiLine(encoded, $"=yend size={decoded.Length} part=1");
        encoded.Position = 0;
        return encoded;
    }

    private static void WriteAsciiLine(Stream stream, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        stream.Write(bytes);
    }
}

using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Streams;

public sealed class DavMultipartFileStreamTests
{
    [Fact]
    public async Task ReadAsyncUsesSegmentSlicesAcrossSegmentBoundaries()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2, 3], partOffset: 0)
            .AddSegment("segment-2", [4, 5, 6, 7], partOffset: 4);
        await using var stream = new DavMultipartFileStream(
            [
                new DavMultipartFile.FilePart
                {
                    SegmentIds = ["segment-1", "segment-2"],
                    SegmentIdByteRange = new LongRange(0, 8),
                    FilePartByteRange = new LongRange(2, 6),
                    SegmentSlices =
                    [
                        new DavMultipartFile.SegmentSlice
                        {
                            SegmentId = "segment-1",
                            SegmentByteRange = new LongRange(2, 4),
                            FilePartByteRange = new LongRange(0, 2)
                        },
                        new DavMultipartFile.SegmentSlice
                        {
                            SegmentId = "segment-2",
                            SegmentByteRange = new LongRange(0, 2),
                            FilePartByteRange = new LongRange(2, 4)
                        }
                    ]
                }
            ],
            client,
            articleBufferSize: 1);
        var buffer = new byte[4];

        var read = await ReadFullyAsync(stream, buffer);

        Assert.Equal(4, read);
        Assert.Equal([2, 3, 4, 5], buffer);
    }

    [Fact]
    public async Task ReadAsyncFallsBackToLegacySegmentRangesWhenSlicesAreMissing()
    {
        using var client = new FakeNntpClient()
            .AddSegment("segment-1", [0, 1, 2, 3], partOffset: 0)
            .AddSegment("segment-2", [4, 5, 6, 7], partOffset: 4);
        await using var stream = new DavMultipartFileStream(
            [
                new DavMultipartFile.FilePart
                {
                    SegmentIds = ["segment-1", "segment-2"],
                    SegmentIdByteRange = new LongRange(0, 8),
                    FilePartByteRange = new LongRange(2, 6),
                    SegmentSlices = []
                }
            ],
            client,
            articleBufferSize: 1);
        var buffer = new byte[4];

        var read = await ReadFullyAsync(stream, buffer);

        Assert.Equal(4, read);
        Assert.Equal([2, 3, 4, 5], buffer);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, CancellationToken.None);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }
}

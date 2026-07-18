using NzbWebDAV.Config;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Queue;

public sealed class FetchFirstSegmentsStepTests
{
    [Fact]
    public async Task UsesBodyInsteadOfArticleWhenNzbProvidesTheStandardPostDate()
    {
        var postedAt = DateTimeOffset.FromUnixTimeSeconds(1_710_000_000);
        var file = CreateFile(postedAt);
        using var client = new FakeNntpClient().AddSegment("segment-a", new byte[20 * 1024]);

        var result = Assert.Single(await FetchFirstSegmentsStep.FetchFirstSegments(
            [file], client, new ConfigManager(), CancellationToken.None));

        Assert.Equal(1, client.DecodedBodyCallCount);
        Assert.Equal(0, client.DecodedArticleCallCount);
        Assert.Equal(postedAt, result.ReleaseDate);
        Assert.Equal(16 * 1024, result.First16KB!.Length);
    }

    [Fact]
    public async Task FallsBackToArticleHeadersWhenNzbPostDateIsMissing()
    {
        var file = CreateFile(postedAt: null);
        using var client = new FakeNntpClient().AddSegment("segment-a", new byte[128]);

        _ = Assert.Single(await FetchFirstSegmentsStep.FetchFirstSegments(
            [file], client, new ConfigManager(), CancellationToken.None));

        Assert.Equal(0, client.DecodedBodyCallCount);
        Assert.Equal(1, client.DecodedArticleCallCount);
    }

    private static NzbFile CreateFile(DateTimeOffset? postedAt)
    {
        var file = new NzbFile
        {
            Subject = "example.bin",
            PostedAt = postedAt
        };
        file.Segments.Add(new NzbSegment
        {
            Number = 1,
            Bytes = 20 * 1024,
            MessageId = "segment-a"
        });
        return file;
    }
}

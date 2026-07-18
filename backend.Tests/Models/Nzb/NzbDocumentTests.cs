using System.Text;
using NzbWebDAV.Models.Nzb;

namespace NzbWebDAV.Tests.Models.Nzb;

public class NzbDocumentTests
{
    [Fact]
    public async Task DuplicateSegmentNumbersCollapseIntoOneLogicalSegmentWithAlternates()
    {
        const string xml = """
                           <?xml version="1.0" encoding="utf-8"?>
                           <nzb>
                             <file subject="example.mkv">
                               <segments>
                                 <segment bytes="10" number="1">segment-a</segment>
                                 <segment bytes="11" number="1">segment-b</segment>
                                 <segment bytes="20" number="2">segment-c</segment>
                               </segments>
                             </file>
                           </nzb>
                           """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = await NzbDocument.LoadAsync(stream);
        var file = Assert.Single(document.Files);

        Assert.Equal(2, file.GetLogicalSegmentCount());
        Assert.Equal(30, file.GetTotalYencodedSize());

        var segmentIds = file.GetSegmentIds();
        Assert.Equal(["segment-a", "segment-b"], NzbSegmentIdSet.Decode(segmentIds[0]));
        Assert.Equal(["segment-c"], NzbSegmentIdSet.Decode(segmentIds[1]));
    }

    [Fact]
    public async Task OutOfOrderNumberedSegmentsAreReturnedInSegmentNumberOrder()
    {
        const string xml = """
                           <?xml version="1.0" encoding="utf-8"?>
                           <nzb>
                             <file subject="example.mkv">
                               <segments>
                                 <segment bytes="20" number="2">segment-b</segment>
                                 <segment bytes="10" number="1">segment-a</segment>
                               </segments>
                             </file>
                           </nzb>
                           """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var document = await NzbDocument.LoadAsync(stream);
        var file = Assert.Single(document.Files);

        Assert.Equal(["segment-a", "segment-b"], file.GetSegmentIds());
        Assert.Equal(30, file.GetTotalYencodedSize());
    }

    [Theory]
    [InlineData("1710000000", 1710000000L)]
    [InlineData("not-a-date", null)]
    [InlineData("999999999999999999", null)]
    public async Task FileDateParsesAsOptionalUnixTimestamp(string rawDate, long? expectedUnixSeconds)
    {
        var xml = $"""
                  <?xml version="1.0" encoding="utf-8"?>
                  <nzb>
                    <file subject="example.mkv" date="{rawDate}">
                      <segments>
                        <segment bytes="10" number="1">segment-a</segment>
                      </segments>
                    </file>
                  </nzb>
                  """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var file = Assert.Single((await NzbDocument.LoadAsync(stream)).Files);

        Assert.Equal(expectedUnixSeconds, file.PostedAt?.ToUnixTimeSeconds());
    }
}

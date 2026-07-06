using System.Collections;
using NzbWebDAV.Clients.Usenet.Models;

namespace backend.Tests.Clients.Usenet;

public sealed class SegmentCheckBatchTests
{
    [Fact]
    public void AllExistsCreatesCleanBatchWithoutEnumeratingSource()
    {
        var segments = new IndexOnlyList<string>(["segment-1", "segment-2"]);

        var batch = SegmentCheckBatch.AllExists(segments);

        Assert.True(batch.IsClean);
        Assert.Equal(2, batch.Checked);
        Assert.Equal(0, batch.Missing);
        Assert.Equal(0, batch.ProviderErrors);
        Assert.Equal(0, batch.Unknown);
        Assert.Equal("segment-1", batch.Results[0].SegmentId);
        Assert.Equal("segment-2", batch.Results[1].SegmentId);
    }

    private sealed class IndexOnlyList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int Count => values.Count;

        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException("Source should not be enumerated.");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

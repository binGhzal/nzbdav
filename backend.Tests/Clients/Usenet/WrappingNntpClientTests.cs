using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Clients.Usenet;

public sealed class WrappingNntpClientTests
{
    [Fact]
    public async Task ReplaceUnderlyingClient_DisposesOldClientAfterDrainDelay()
    {
        var disposed = false;
        var oldClient = new FakeNntpClient(onDispose: () => disposed = true);
        var newClient = new FakeNntpClient();
        using var wrapper = new TestWrappingNntpClient(oldClient);

        wrapper.Replace(newClient, TimeSpan.FromMilliseconds(50));

        Assert.False(disposed);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.True(disposed);
    }

    [Fact]
    public void ReplaceUnderlyingClient_DoesNotThrowWhenOldClientDisposeFails()
    {
        var newClientDisposed = false;
        var oldClient = new FakeNntpClient(onDispose: () => throw new InvalidOperationException("dispose failed"));
        var newClient = new FakeNntpClient(onDispose: () => newClientDisposed = true);
        using var wrapper = new TestWrappingNntpClient(oldClient);

        var exception = Record.Exception(() => wrapper.Replace(newClient, TimeSpan.Zero));

        Assert.Null(exception);
        wrapper.Dispose();
        Assert.True(newClientDisposed);
    }

    [Fact]
    public async Task CheckAllSegmentsAsyncUsesCurrentInstanceCheckSegmentsAsync()
    {
        using var wrapper = new RecordingWrappingNntpClient(new FakeNntpClient());

        await wrapper.CheckAllSegmentsAsync(["segment-1"], 1, null, CancellationToken.None);

        Assert.Equal(1, wrapper.CheckSegmentsCallCount);
    }

    private sealed class TestWrappingNntpClient(INntpClient client) : WrappingNntpClient(client)
    {
        public void Replace(INntpClient client, TimeSpan drainDelay)
        {
            ReplaceUnderlyingClient(client, drainDelay);
        }
    }

    private sealed class RecordingWrappingNntpClient(INntpClient client) : WrappingNntpClient(client)
    {
        public int CheckSegmentsCallCount { get; private set; }

        public override Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            CheckSegmentsCallCount++;
            var results = segmentIds
                .Select(segmentId => new SegmentCheckResult(
                    segmentId,
                    SegmentCheckState.Exists,
                    Provider: null,
                    Error: null))
                .ToArray();
            return Task.FromResult(SegmentCheckBatch.FromResults(results));
        }
    }
}

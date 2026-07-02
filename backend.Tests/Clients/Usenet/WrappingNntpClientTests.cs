using NzbWebDAV.Clients.Usenet;
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

    private sealed class TestWrappingNntpClient(INntpClient client) : WrappingNntpClient(client)
    {
        public void Replace(INntpClient client, TimeSpan drainDelay)
        {
            ReplaceUnderlyingClient(client, drainDelay);
        }
    }
}

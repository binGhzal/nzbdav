using NzbWebDAV.Queue;

namespace backend.Tests.Queue;

public sealed class QueueWorkLaneCoordinatorTests
{
    [Fact]
    public async Task EnterVerifyAsyncLimitsConcurrentVerification()
    {
        using var coordinator = new QueueWorkLaneCoordinator();
        using var first = await coordinator.EnterVerifyAsync(2, CancellationToken.None);
        using var second = await coordinator.EnterVerifyAsync(2, CancellationToken.None);

        var thirdTask = coordinator.EnterVerifyAsync(2, CancellationToken.None).AsTask();
        await Task.Delay(50);

        Assert.Equal(2, coordinator.VerifyActive);
        Assert.False(thirdTask.IsCompleted);

        first.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, coordinator.VerifyActive);
    }
}

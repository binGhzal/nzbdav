using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrQueueResolutionLimiterTests
{
    [Fact]
    public void TryAcquireBlocksActionsDuringStartupGrace()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ArrQueueResolutionLimiter(
            () => now,
            startupGrace: TimeSpan.FromMinutes(2),
            actionCooldown: TimeSpan.FromHours(1));

        Assert.False(limiter.TryAcquire(
            "sonarr",
            "queue-1",
            ArrConfig.QueueAction.RemoveAndBlocklistAndSearch));
    }

    [Fact]
    public void TryAcquireDedupesSameItemActionAfterStartupGrace()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new ArrQueueResolutionLimiter(
            () => now,
            startupGrace: TimeSpan.Zero,
            actionCooldown: TimeSpan.FromHours(1));

        Assert.True(limiter.TryAcquire("sonarr", "queue-1", ArrConfig.QueueAction.Remove));
        Assert.False(limiter.TryAcquire("sonarr", "queue-1", ArrConfig.QueueAction.Remove));

        now += TimeSpan.FromHours(2);
        Assert.True(limiter.TryAcquire("sonarr", "queue-1", ArrConfig.QueueAction.Remove));
    }
}

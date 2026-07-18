using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Database.Models;
using static backend.Tests.Database.LegacyTimestampContractTests;

namespace backend.Tests.Api;

public sealed class AddFileLocalTimestampTests
{
    [Fact]
    public void QueueItemFactoryUsesInjectedDeploymentLocalWallTime()
    {
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero),
            FixedLocalZone());
        var request = new AddFileRequest
        {
            FileName = "fixed-local-wall.nzb",
            Category = "movies",
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        };

        var queueItem = AddFileController.CreateQueueItem(
            Guid.NewGuid(),
            "fixed-local-wall",
            100,
            200,
            request,
            timeProvider);

        Assert.Equal(new DateTime(2026, 7, 12, 5, 2, 3, DateTimeKind.Unspecified), queueItem.CreatedAt);
        Assert.Equal(DateTimeKind.Unspecified, queueItem.CreatedAt.Kind);
    }
}

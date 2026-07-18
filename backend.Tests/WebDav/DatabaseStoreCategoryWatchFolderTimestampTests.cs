using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Requests;
using static backend.Tests.Database.LegacyTimestampContractTests;

namespace backend.Tests.WebDav;

public sealed class DatabaseStoreCategoryWatchFolderTimestampTests
{
    [Fact]
    public void AddFileRequestAndQueueItemPreserveInjectedLocalWallPause()
    {
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 1, 2, 3, TimeSpan.Zero),
            FixedLocalZone());
        var request = new CreateItemRequest
        {
            Name = "fixed-local-wall.nzb",
            Stream = Stream.Null,
            Overwrite = false,
            CancellationToken = CancellationToken.None
        };

        var addFileRequest = DatabaseStoreCategoryWatchFolder.CreateAddFileRequest(
            "movies",
            request,
            timeProvider);
        var queueItem = AddFileController.CreateQueueItem(
            Guid.NewGuid(),
            "fixed-local-wall",
            100,
            200,
            addFileRequest,
            timeProvider);

        var expectedPauseUntil = new DateTime(2026, 7, 12, 5, 2, 6, DateTimeKind.Unspecified);
        Assert.Equal(expectedPauseUntil, addFileRequest.PauseUntil);
        Assert.Equal(DateTimeKind.Unspecified, addFileRequest.PauseUntil!.Value.Kind);
        Assert.Equal(expectedPauseUntil, queueItem.PauseUntil);
        Assert.Equal(DateTimeKind.Unspecified, queueItem.PauseUntil!.Value.Kind);
        Assert.Equal(QueueItem.PriorityOption.Normal, queueItem.Priority);
        Assert.Equal(QueueItem.PostProcessingOption.RepairUnpackDelete, queueItem.PostProcessing);
    }
}

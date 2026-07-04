using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrPriorityServiceTests
{
    [Fact]
    public void Score_PrioritizesRecentlyAiredSeriesCompletion()
    {
        var queueItem = CreateQueueItem();
        var correlation = new ArrDownloadCorrelation
        {
            ArrApp = "sonarr",
            InstanceKey = "sonarr:test",
            EpisodeId = 10
        };
        var metadata = new ArrPriorityMetadata();
        metadata.SonarrMissingEpisodes[("sonarr:test", 10)] = new SonarrMissingEpisode
        {
            Id = 10,
            SeriesId = 20,
            SeasonNumber = 1,
            AirDateUtc = DateTimeOffset.UtcNow.AddDays(-1),
            Monitored = true
        };
        metadata.SonarrMissingBySeries[("sonarr:test", 20)] = 1;
        metadata.SonarrMissingBySeason[("sonarr:test", 20, 1)] = 1;

        var decision = ArrPriorityService.Score(queueItem, correlation, metadata);

        Assert.Equal(QueueItem.PriorityOption.High, decision.Priority);
        Assert.Contains("series-completion", decision.Reasons);
        Assert.Contains("recently-aired", decision.Reasons);
    }

    [Fact]
    public void Score_PrioritizesRadarrCollectionCompletion()
    {
        var queueItem = CreateQueueItem();
        var correlation = new ArrDownloadCorrelation
        {
            ArrApp = "radarr",
            InstanceKey = "radarr:test",
            MovieId = 30
        };
        var metadata = new ArrPriorityMetadata();
        metadata.RadarrMissingMovies[("radarr:test", 30)] = new RadarrMissingMovie
        {
            Id = 30,
            Monitored = true,
            Collection = new RadarrMovieCollection { Id = 40, Title = "Example Collection" }
        };
        metadata.RadarrMissingByCollection[("radarr:test", 40)] = 1;

        var decision = ArrPriorityService.Score(queueItem, correlation, metadata);

        Assert.Equal(QueueItem.PriorityOption.High, decision.Priority);
        Assert.Contains("collection-completion", decision.Reasons);
    }

    [Fact]
    public void Score_ReportsUncorrelatedItemsWithoutApplyingPriority()
    {
        var decision = ArrPriorityService.Score(CreateQueueItem(), null, new ArrPriorityMetadata());

        Assert.Equal(0, decision.Score);
        Assert.Equal(QueueItem.PriorityOption.Normal, decision.Priority);
        Assert.NotNull(decision.StaleReason);
    }

    private static QueueItem CreateQueueItem() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = "Example.nzb",
        JobName = "Example",
        Category = "tv",
        NzbFileSize = 100,
        TotalSegmentBytes = 1024,
        Priority = QueueItem.PriorityOption.Normal,
        PostProcessing = QueueItem.PostProcessingOption.None
    };
}

using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrIntegrationTests
{
    [Fact]
    public void GetMediaKey_UsesFormerDefaultSeasonWhenSeasonIsMissing()
    {
        var record = new SonarrQueueRecord
        {
            SeriesId = 41
        };

        var mediaKey = ArrIntegration.GetMediaKey("sonarr", record);

        Assert.Equal("sonarr:series:41:season:0", mediaKey);
    }
}

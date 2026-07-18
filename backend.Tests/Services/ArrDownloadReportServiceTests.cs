using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrDownloadReportServiceTests
{
    [Fact]
    public async Task CompletionRefreshBypassesRecentBestEffortDebounce()
    {
        var client = new RecordingSonarrClient("tv");
        var service = new ArrDownloadReportService(new ConfigManager(), () => [client]);

        await service.RefreshMonitoredDownloadsDebouncedAsync("tv");
        await service.RefreshMonitoredDownloadsDebouncedAsync("tv");
        await service.RefreshMonitoredDownloadsOnCompletionAsync("tv");

        Assert.Equal(2, client.RefreshCount);
    }

    private sealed class RecordingSonarrClient(string category)
        : SonarrClient("http://sonarr.invalid", "test-key")
    {
        public int RefreshCount { get; private set; }

        public override Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default)
        {
            var client = JsonSerializer.Deserialize<ArrDownloadClient>(
                $$"""
                {
                  "enable": true,
                  "protocol": "usenet",
                  "fields": [{ "name": "tvCategory", "value": "{{category}}" }]
                }
                """)!;
            return Task.FromResult(new List<ArrDownloadClient> { client });
        }

        public override Task<int> GetQueueCountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public override Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default)
        {
            RefreshCount++;
            return Task.FromResult(new ArrCommand { Id = RefreshCount, Name = "RefreshMonitoredDownloads" });
        }
    }
}

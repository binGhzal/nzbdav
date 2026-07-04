using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ArrOperationsServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ArrOperationsServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HasRejectableDuplicateAsync_DetectsActiveQueueItem()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.QueueItems.Add(CreateQueueItem("Example.Show.S01E01.nzb", "tv"));
        await dbContext.SaveChangesAsync();

        var service = new ArrOperationsService(_fixture.CreateConfigManager());
        var duplicate = await service.HasRejectableDuplicateAsync(
            dbContext,
            "Example Show S01E01.nzb",
            "Example Show S01E01",
            "tv");

        Assert.True(duplicate);
    }

    [Fact]
    public async Task IngestCustomScriptEvent_CreatesCorrelationAndLifecycleEvent()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());
        var queueItem = CreateQueueItem("Example.Show.S01E01.nzb", "tv");
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var response = await service.IngestCustomScriptEventAsync(
            dbContext,
            "sonarr",
            new Dictionary<string, string>
            {
                ["instance_host"] = "http://sonarr:8989",
                ["event_type"] = "Grab",
                ["nzo_id"] = queueItem.Id.ToString(),
                ["episode_id"] = "123",
                ["series_id"] = "456",
                ["season_number"] = "1",
                ["release_title"] = "Example.Show.S01E01",
                ["category"] = "tv"
            });

        Assert.Equal("sonarr:episode:123", response.Correlation.MediaKey);
        Assert.Equal(queueItem.Id.ToString(), response.Correlation.QueueItemId);
        Assert.Equal("Grabbed", (await dbContext.ArrDownloadLifecycleEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task UpsertManualCorrelation_CreatesOperatorCorrelation()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());
        var queueItem = CreateQueueItem("Movie.nzb", "movies");
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var correlation = await service.UpsertManualCorrelationAsync(
            dbContext,
            new NzbWebDAV.Api.Controllers.Arr.ArrManualCorrelationRequest
            {
                NzoId = queueItem.Id.ToString(),
                ArrApp = "radarr",
                InstanceHost = "http://radarr:7878",
                MovieId = 99,
                ReleaseTitle = "Movie 2026"
            });

        Assert.Equal("radarr:movie:99", correlation.MediaKey);
        Assert.Equal(queueItem.Id.ToString(), correlation.QueueItemId);
    }

    [Fact]
    public async Task BuildValidationAsync_ReportsCorrelationCoverageAndIssues()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem("Movie.nzb", "movies");
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var service = new ArrOperationsService(_fixture.CreateConfigManager());
        var validation = await service.BuildValidationAsync(new DavDatabaseClient(dbContext));

        Assert.Equal(0, validation.CorrelationCoveragePercent);
        Assert.Contains(validation.Issues, x => x.Code == "queue_uncorrelated");
    }

    [Fact]
    public async Task ArrSearchNudgeService_ReportMode_PlansSonarrEpisodeSearchWithoutPostingCommand()
    {
        await using var server = await FakeArrServer.StartAsync();
        server.MissingResponse = MissingEpisodeResponse(123, 456);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var config = CreateArrConfigManager(server.Url, mode: "report");
        var service = new ArrSearchNudgeService(config);

        await service.RunOnceAsync();

        var command = await dbContext.ArrSearchNudgeCommands.SingleAsync();
        Assert.Equal("planned", command.Status);
        Assert.Equal("EpisodeSearch", command.CommandName);
        Assert.Empty(server.PostedCommands);
    }

    [Fact]
    public async Task ArrSearchNudgeService_ApplyMode_PostsSonarrEpisodeSearch()
    {
        await using var server = await FakeArrServer.StartAsync();
        server.MissingResponse = MissingEpisodeResponse(321, 654);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var config = CreateArrConfigManager(server.Url, mode: "apply");
        var service = new ArrSearchNudgeService(config);

        await service.RunOnceAsync();

        var command = await dbContext.ArrSearchNudgeCommands.SingleAsync();
        Assert.Equal("executed", command.Status);
        Assert.Single(server.PostedCommands);
        Assert.Contains("EpisodeSearch", server.PostedCommands[0]);
        Assert.Contains("321", server.PostedCommands[0]);
    }

    private static ConfigManager CreateArrConfigManager(string host, string mode)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = JsonSerializer.Serialize(new ArrConfig
                {
                    SonarrInstances = [new ArrConfig.ConnectionDetails { Host = host, ApiKey = "test" }],
                    SearchNudge = new ArrConfig.SearchNudgeOptions
                    {
                        Enabled = true,
                        Mode = mode,
                        IntervalSeconds = 300,
                        CooldownSeconds = 300,
                        MaxCommandsPerHour = 20,
                        SonarrBatchSize = 10,
                        ConcurrentCommandsPerInstance = 1
                    }
                })
            }
        ]);
        return config;
    }

    private static QueueItem CreateQueueItem(string fileName, string category) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = fileName,
        JobName = Path.GetFileNameWithoutExtension(fileName).Replace('.', ' '),
        Category = category,
        NzbFileSize = 100,
        TotalSegmentBytes = 100,
        Priority = QueueItem.PriorityOption.Normal,
        PostProcessing = QueueItem.PostProcessingOption.None
    };

    private static string MissingEpisodeResponse(int episodeId, int seriesId)
    {
        var airDateUtc = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        return $$"""
        {"page":1,"pageSize":500,"totalRecords":1,"records":[{"id":{{episodeId}},"seriesId":{{seriesId}},"seasonNumber":1,"episodeNumber":1,"airDateUtc":"{{airDateUtc}}","hasFile":false,"monitored":true}]}
        """;
    }

    private sealed class FakeArrServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeArrServer(HttpListener listener, string url)
        {
            _listener = listener;
            Url = url.TrimEnd('/');
            _loop = Task.Run(HandleLoopAsync);
        }

        public string Url { get; }
        public string MissingResponse { get; set; } = """{"page":1,"pageSize":500,"totalRecords":0,"records":[]}""";
        public List<string> PostedCommands { get; } = [];

        public static Task<FakeArrServer> StartAsync()
        {
            var port = GetFreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return Task.FromResult(new FakeArrServer(listener, prefix));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Close();
            try
            {
                await _loop;
            }
            catch
            {
                // listener shutdown races are expected in tests
            }
        }

        private async Task HandleLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context), _cts.Token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/api/v3/wanted/missing")
            {
                await WriteJsonAsync(context, MissingResponse);
                return;
            }

            if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/api/v3/command")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                PostedCommands.Add(await reader.ReadToEndAsync());
                await WriteJsonAsync(context, """{"id":42,"name":"EpisodeSearch","commandName":"EpisodeSearch","result":"started","status":"started","priority":"normal"}""");
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}

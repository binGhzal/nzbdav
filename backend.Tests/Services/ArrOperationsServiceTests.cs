using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.AddFile;
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
    public async Task AddFileAsync_RejectsDuplicateBeforeWritingBlobOrQueueRow()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.QueueItems.Add(CreateQueueItem("Example.Show.S01E01.nzb", "tv"));
        await dbContext.SaveChangesAsync();

        var configManager = _fixture.CreateConfigManager();
        Assert.Equal("increment", configManager.GetDuplicateNzbBehavior());
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "api.duplicate-nzb-behavior",
                ConfigValue = "reject"
            }
        ]);
        var controller = new AddFileController(
            new DefaultHttpContext(),
            new DavDatabaseClient(dbContext),
            queueManager: null!,
            configManager,
            websocketManager: null!,
            arrDownloadReportService: null!,
            new ArrOperationsService(configManager));

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => controller.AddFileAsync(new AddFileRequest
        {
            FileName = "Example.Show.S01E01.nzb",
            Category = "tv",
            NzbFileStream = new MemoryStream(Encoding.UTF8.GetBytes("<nzb></nzb>")),
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None
        }));

        Assert.Equal("Duplicate NZB rejected because an equivalent item is already active or recently completed.", ex.Message);
        Assert.Equal(1, await dbContext.QueueItems.CountAsync());
        var blobsPath = Path.Join(DavDatabaseContext.ConfigPath, "blobs");
        Assert.False(Directory.Exists(blobsPath) && Directory.EnumerateFiles(blobsPath, "*", SearchOption.AllDirectories).Any());
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

        Assert.Equal("sonarr:episode:123", response.Correlation!.MediaKey);
        Assert.Equal(queueItem.Id.ToString(), response.Correlation.QueueItemId);
        Assert.Equal("Grabbed", (await dbContext.ArrDownloadLifecycleEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task IngestCustomScriptEvent_TreatsTestEventAsNoOpSuccess()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());

        var response = await service.IngestCustomScriptEventAsync(
            dbContext,
            "radarr",
            new Dictionary<string, string>
            {
                ["radarr_eventtype"] = "Test"
            });

        Assert.True(response.Status);
        Assert.Equal("Test", response.EventType);
        Assert.Null(response.Correlation);
        Assert.Empty(await dbContext.ArrDownloadCorrelations.ToListAsync());
        Assert.Empty(await dbContext.ArrDownloadLifecycleEvents.ToListAsync());
    }

    [Fact]
    public async Task IngestCustomScriptEvent_NormalizesOfficialArrVariables()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());

        var response = await service.IngestCustomScriptEventAsync(
            dbContext,
            "lidarr",
            new Dictionary<string, string>
            {
                ["lidarr_eventtype"] = "Grab",
                ["lidarr_download_id"] = "lidarr-download-1",
                ["lidarr_artist_id"] = "77",
                ["lidarr_album_id"] = "88",
                ["lidarr_release_title"] = "Artist - Album",
                ["lidarr_quality"] = "FLAC"
            });

        Assert.Equal("lidarr:album:88", response.Correlation!.MediaKey);
        Assert.Equal("custom-script", response.Correlation.Source);
        Assert.False(response.Correlation.ManualLock);
        Assert.Equal("lidarr-download-1", response.Correlation.DownloadId);
        Assert.Equal("FLAC", response.Correlation.Quality);
    }

    [Fact]
    public async Task IngestCustomScriptEvent_DoesNotOverwriteManualLockedIdentity()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem("Example.Show.S01E01.nzb", "tv");
        dbContext.QueueItems.Add(queueItem);
        dbContext.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
        {
            Id = Guid.NewGuid(),
            QueueItemId = queueItem.Id,
            ArrApp = "sonarr",
            InstanceKey = "sonarr:http://sonarr:8989",
            InstanceHost = "http://sonarr:8989",
            DownloadId = "operator-download",
            MediaKey = "sonarr:episode:111",
            EpisodeId = 111,
            SeriesId = 222,
            SeasonNumber = 1,
            Source = "manual",
            ManualLock = true,
            Status = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());

        var response = await service.IngestCustomScriptEventAsync(
            dbContext,
            "sonarr",
            new Dictionary<string, string>
            {
                ["instance_host"] = "http://sonarr:8989",
                ["sonarr_eventtype"] = "Download",
                ["nzo_id"] = queueItem.Id.ToString(),
                ["sonarr_download_id"] = "arr-download",
                ["sonarr_episode_id"] = "333",
                ["sonarr_series_id"] = "444",
                ["sonarr_release_title"] = "Updated Title"
            });

        Assert.Equal("operator-download", response.Correlation!.DownloadId);
        Assert.Equal("sonarr:episode:111", response.Correlation.MediaKey);
        Assert.Equal(111, response.Correlation.EpisodeId);
        Assert.Equal("manual", response.Correlation.Source);
        Assert.True(response.Correlation.ManualLock);
        Assert.Equal("Updated Title", response.Correlation.ReleaseTitle);
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
        Assert.Equal("manual", correlation.Source);
        Assert.True(correlation.ManualLock);
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
    public async Task GetSearchNudgeCommandsAsync_FiltersByAppStatusModeCommandAndSearch()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var matching = new ArrSearchNudgeCommand
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = "sonarr:http://sonarr:8989",
            InstanceHost = "http://sonarr:8989",
            CommandName = "EpisodeSearch",
            TargetsJson = "[123]",
            Mode = "apply",
            Status = "failed",
            CooldownKey = "sonarr:123",
            ReasonsJson = """["recently-aired"]""",
            Error = "network failed",
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            NextAllowedAt = DateTimeOffset.UtcNow
        };
        dbContext.ArrSearchNudgeCommands.AddRange(
            matching,
            new ArrSearchNudgeCommand
            {
                Id = Guid.NewGuid(),
                ArrApp = "radarr",
                InstanceKey = "radarr:http://radarr:7878",
                InstanceHost = "http://radarr:7878",
                CommandName = "MoviesSearch",
                TargetsJson = "[456]",
                Mode = "report",
                Status = "planned",
                CooldownKey = "radarr:456",
                ReasonsJson = """["collection-completion"]""",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                NextAllowedAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();
        var service = new ArrOperationsService(_fixture.CreateConfigManager());

        var commands = await service.GetSearchNudgeCommandsAsync(
            dbContext,
            limit: 50,
            status: "failed",
            arrApp: "sonarr",
            mode: "apply",
            commandName: "EpisodeSearch",
            search: "network");

        Assert.Single(commands);
        Assert.Equal(matching.Id.ToString(), commands[0].Id);
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

    [Fact]
    public async Task RetrySearchNudgeCommand_ExecutesPendingApplyCommand()
    {
        await using var server = await FakeArrServer.StartAsync();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var commandId = Guid.NewGuid();
        dbContext.ArrSearchNudgeCommands.Add(new ArrSearchNudgeCommand
        {
            Id = commandId,
            ArrApp = "sonarr",
            InstanceKey = GetInstanceKey("sonarr", server.Url),
            InstanceHost = server.Url,
            CommandName = "EpisodeSearch",
            TargetsJson = "[777]",
            Mode = "apply",
            Status = "failed",
            CooldownKey = "sonarr:777",
            ReasonsJson = """["retry"]""",
            Error = "previous failure",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAllowedAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await dbContext.SaveChangesAsync();
        var config = CreateArrConfigManager(server.Url, mode: "apply");
        var operations = new ArrOperationsService(config);

        await operations.RetrySearchNudgeCommandAsync(dbContext, commandId);
        await new ArrSearchNudgeService(config).RunOnceAsync();

        dbContext.ChangeTracker.Clear();
        var command = await dbContext.ArrSearchNudgeCommands.SingleAsync(x => x.Id == commandId);
        Assert.Equal("executed", command.Status);
        Assert.Null(command.Error);
        Assert.Single(server.PostedCommands);
        Assert.Contains("777", server.PostedCommands[0]);
    }

    [Fact]
    public async Task ArrSearchNudgeService_LeavesPendingApplyCommandWhenInstanceConcurrencyIsSaturated()
    {
        await using var server = await FakeArrServer.StartAsync();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var instanceKey = GetInstanceKey("sonarr", server.Url);
        var pendingId = Guid.NewGuid();
        dbContext.ArrSearchNudgeCommands.AddRange(
            new ArrSearchNudgeCommand
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = instanceKey,
                InstanceHost = server.Url,
                CommandName = "EpisodeSearch",
                TargetsJson = "[100]",
                Mode = "apply",
                Status = "executing",
                CooldownKey = "sonarr:100",
                ReasonsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAllowedAt = DateTimeOffset.UtcNow
            },
            new ArrSearchNudgeCommand
            {
                Id = pendingId,
                ArrApp = "sonarr",
                InstanceKey = instanceKey,
                InstanceHost = server.Url,
                CommandName = "EpisodeSearch",
                TargetsJson = "[101]",
                Mode = "apply",
                Status = "pending_apply",
                CooldownKey = "sonarr:101",
                ReasonsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAllowedAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();
        var config = CreateArrConfigManager(server.Url, mode: "apply");

        await new ArrSearchNudgeService(config).RunOnceAsync();

        var pending = await dbContext.ArrSearchNudgeCommands.SingleAsync(x => x.Id == pendingId);
        Assert.Equal("pending_apply", pending.Status);
        Assert.Empty(server.PostedCommands);
    }


    [Fact]
    public async Task ArrSearchNudgeService_ApplyMode_PersistsCommandFailure()
    {
        await using var server = await FakeArrServer.StartAsync();
        server.CommandStatusCode = 500;
        server.CommandResponse = """{"error":"boom"}""";
        server.MissingResponse = MissingEpisodeResponse(987, 654);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var config = CreateArrConfigManager(server.Url, mode: "apply");

        await new ArrSearchNudgeService(config).RunOnceAsync();

        var command = await dbContext.ArrSearchNudgeCommands.SingleAsync();
        Assert.Equal("failed", command.Status);
        Assert.NotNull(command.Error);
        Assert.Single(server.PostedCommands);
    }

    [Fact]
    public async Task ArrSearchNudgeService_SkipsActiveMediaAlreadyInQueue()
    {
        await using var server = await FakeArrServer.StartAsync();
        server.MissingResponse = MissingEpisodeResponse(555, 654);
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem("Example.Show.S01E01.nzb", "tv");
        dbContext.QueueItems.Add(queueItem);
        dbContext.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
        {
            Id = Guid.NewGuid(),
            QueueItemId = queueItem.Id,
            ArrApp = "sonarr",
            InstanceKey = GetInstanceKey("sonarr", server.Url),
            InstanceHost = server.Url,
            MediaKey = "sonarr:episode:555",
            Source = "auto",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var config = CreateArrConfigManager(server.Url, mode: "report");

        await new ArrSearchNudgeService(config).RunOnceAsync();

        Assert.Empty(await dbContext.ArrSearchNudgeCommands.ToListAsync());
        Assert.Empty(server.PostedCommands);
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

    private static string GetInstanceKey(string app, string host)
    {
        host = host.Trim().TrimEnd('/').ToLowerInvariant();
        var raw = $"{app}:{host}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        return $"{app}:{hash}";
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
        public int CommandStatusCode { get; set; } = 200;
        public string CommandResponse { get; set; } = """{"id":42,"name":"EpisodeSearch","commandName":"EpisodeSearch","result":"started","status":"started","priority":"normal"}""";
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
                context.Response.StatusCode = CommandStatusCode;
                await WriteJsonAsync(context, CommandResponse);
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

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task HasRejectableDuplicateAsync_UsesDeploymentLocalWallTimeForHistoryCutoff()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var localZone = TimeZoneInfo.CreateCustomTimeZone(
            "legacy-local-plus-four",
            TimeSpan.FromHours(4),
            "legacy-local-plus-four",
            "legacy-local-plus-four");
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            localZone);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 7, 11, 2, 0, 0, DateTimeKind.Unspecified),
            FileName = "Example.Movie.nzb",
            JobName = "Example Movie",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 1
        });
        await dbContext.SaveChangesAsync();

        var duplicate = await new ArrOperationsService(_fixture.CreateConfigManager(), timeProvider)
            .HasRejectableDuplicateAsync(
                dbContext,
                "Example.Movie.nzb",
                "Example Movie",
                "movies");

        Assert.False(duplicate);
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
            new ArrOperationsService(configManager),
            new NzbBlobIngestCoordinator());

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
    public async Task QueueLifecycleCanBeStagedIntoTheCallersAtomicAcceptanceCommit()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var configManager = _fixture.CreateConfigManager();
        var service = new ArrDownloadReportService(configManager, () => []);
        var queueItem = CreateQueueItem("Example.Show.S01E02.nzb", "tv");
        dbContext.QueueItems.Add(queueItem);

        await service.RecordQueueLifecycleAsync(
            new DavDatabaseClient(dbContext),
            queueItem,
            "Queued",
            "NZB accepted.",
            saveChanges: false);

        Assert.Equal(0, await dbContext.ArrDownloadLifecycleEvents.CountAsync());
        Assert.Equal(EntityState.Added, dbContext.Entry(queueItem).State);
        Assert.Single(dbContext.ChangeTracker.Entries<ArrDownloadLifecycleEvent>());

        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await dbContext.QueueItems.CountAsync());
        Assert.Equal(1, await dbContext.ArrDownloadLifecycleEvents.CountAsync());
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

    [Theory]
    [InlineData("sonarr", "sonarr_applicationurl", "http://sonarr:8989")]
    [InlineData("radarr", "radarr_applicationurl", "http://radarr:7878")]
    public async Task IngestCustomScriptEvent_UsesOfficialApplicationUrlForExactInstanceRouting(
        string app,
        string applicationUrlKey,
        string applicationUrl)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem("Official.Application.Url.nzb", app == "sonarr" ? "tv" : "movies");
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();

        var response = await new ArrOperationsService(_fixture.CreateConfigManager())
            .IngestCustomScriptEventAsync(
                dbContext,
                app,
                new Dictionary<string, string>
                {
                    [$"{app}_eventtype"] = "Grab",
                    [$"{app}_download_id"] = queueItem.Id.ToString(),
                    [applicationUrlKey] = applicationUrl,
                });

        Assert.NotNull(response.Correlation);
        Assert.Equal(applicationUrl, response.Correlation.InstanceHost);
        Assert.Equal(GetInstanceKey(app, applicationUrl), response.Correlation.InstanceKey);
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Download")]
    public async Task IngestCustomScriptEvent_MarksCorrelatedReceiptImported(string eventType)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1
        });
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = davItemId,
            HistoryItemId = historyId,
            State = ImportReceiptState.UnlinkClaimed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await dbContext.SaveChangesAsync();

        await new ArrOperationsService(_fixture.CreateConfigManager()).IngestCustomScriptEventAsync(
            dbContext,
            "radarr",
            new Dictionary<string, string>
            {
                ["event_type"] = eventType,
                ["history_item_id"] = historyId.ToString(),
                ["instance_host"] = "http://radarr:7878"
            });

        var receipt = await dbContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(ImportReceiptState.Imported, receipt.State);
        Assert.NotNull(receipt.ImportedAt);
        Assert.Equal("Imported", (await dbContext.ArrDownloadLifecycleEvents.SingleAsync(x => x.HistoryItemId == historyId)).State);
    }

    [Fact]
    public async Task IngestImportedEventCannotOverwriteVerificationQuarantine()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        var quarantineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string quarantineReason = "confirmed missing articles; operator review required";
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Quarantined.nzb",
            JobName = "Quarantined",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1
        });
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = davItemId,
            HistoryItemId = historyId,
            State = ImportReceiptState.Imported,
            CreatedAt = quarantineAt.AddMinutes(-1),
            UpdatedAt = quarantineAt.AddMinutes(-1),
            ImportedAt = quarantineAt.AddMinutes(-1)
        });
        await dbContext.SaveChangesAsync();
        await new ImportReceiptService(dbContext).MarkVerificationQuarantineAsync(
            historyId,
            quarantineAt,
            quarantineReason,
            CancellationToken.None);

        await new ArrOperationsService(_fixture.CreateConfigManager()).IngestCustomScriptEventAsync(
            dbContext,
            "radarr",
            new Dictionary<string, string>
            {
                ["event_type"] = "Import",
                ["history_item_id"] = historyId.ToString(),
                ["instance_host"] = "http://radarr:7878"
            });

        dbContext.ChangeTracker.Clear();
        var receipt = await dbContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId);
        Assert.Equal(ImportReceiptState.VerificationQuarantined, receipt.State);
        Assert.Equal(quarantineReason, receipt.Detail);
        Assert.Equal(quarantineAt, receipt.UpdatedAt);
        Assert.Equal(
            "Imported",
            (await dbContext.ArrDownloadLifecycleEvents.SingleAsync(x => x.HistoryItemId == historyId)).State);
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Download")]
    public async Task IngestOfficialDownloadIdOnlyEventPreservesEffectiveCorrelationIds(string eventType)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        const string downloadId = "official-download-id";
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "movies",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1
        });
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = davItemId,
            HistoryItemId = historyId,
            State = ImportReceiptState.UnlinkClaimed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        dbContext.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyId,
            ArrApp = "radarr",
            InstanceKey = GetInstanceKey("radarr", "http://radarr:7878"),
            InstanceHost = "http://radarr:7878",
            DownloadId = downloadId,
            MediaKey = "radarr:movie:42",
            MovieId = 42,
            Source = "auto",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await dbContext.SaveChangesAsync();

        await new ArrOperationsService(_fixture.CreateConfigManager()).IngestCustomScriptEventAsync(
            dbContext,
            "radarr",
            new Dictionary<string, string>
            {
                ["radarr_eventtype"] = eventType,
                ["radarr_download_id"] = downloadId,
                ["radarr_host"] = "http://radarr:7878"
            });

        dbContext.ChangeTracker.Clear();
        var correlation = await dbContext.ArrDownloadCorrelations.SingleAsync(x => x.DownloadId == downloadId);
        Assert.Equal(historyId, correlation.HistoryItemId);
        Assert.Equal("radarr:movie:42", correlation.MediaKey);
        Assert.Equal(42, correlation.MovieId);
        var lifecycle = await dbContext.ArrDownloadLifecycleEvents.SingleAsync(x => x.DownloadId == downloadId);
        Assert.Equal(historyId, lifecycle.HistoryItemId);
        Assert.Equal("radarr:movie:42", lifecycle.MediaKey);
        Assert.Equal(
            ImportReceiptState.Imported,
            (await dbContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
    }

    [Fact]
    public async Task IngestCustomScriptEvent_SaveFailureRollsBackReceiptAndLifecycle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        var historyId = Guid.NewGuid();
        var davItemId = Guid.NewGuid();
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.HistoryItems.Add(new HistoryItem
            {
                Id = historyId,
                CreatedAt = DateTime.UtcNow,
                FileName = "Rollback.nzb",
                JobName = "Rollback",
                Category = "movies",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1024,
                DownloadTimeSeconds = 1
            });
            setup.ImportReceipts.Add(new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.UnlinkClaimed,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await setup.SaveChangesAsync();
        }
        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new ArrOperationsService(_fixture.CreateConfigManager()).IngestCustomScriptEventAsync(
                failingContext,
                "radarr",
                new Dictionary<string, string>
                {
                    ["event_type"] = "Import",
                    ["history_item_id"] = historyId.ToString(),
                    ["instance_host"] = "http://radarr:7878"
                }));

        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Equal(
            ImportReceiptState.UnlinkClaimed,
            (await assertionContext.ImportReceipts.SingleAsync(x => x.DavItemId == davItemId)).State);
        Assert.Empty(await assertionContext.ArrDownloadLifecycleEvents.ToListAsync());
        Assert.Empty(await assertionContext.ArrDownloadCorrelations.ToListAsync());
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
    public async Task BuildValidationAsync_ReportsDerivedNonSecretPolicy()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var config = CreateArrConfigManager("http://sonarr:8989", mode: "apply");
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = "api.duplicate-nzb-behavior",
                ConfigValue = "reject"
            }
        ]);
        var service = new ArrOperationsService(config);

        var validation = await service.BuildValidationAsync(new DavDatabaseClient(dbContext));

        Assert.Equal(["sonarr"], validation.ConfiguredApps);
        Assert.Equal("apply", validation.SearchNudgeMode);
        Assert.Equal("reject", validation.DuplicateNzbBehavior);
    }

    [Fact]
    public async Task BuildValidationAsync_IgnoresUnsupportedQueueCategoriesForCorrelationCoverage()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.QueueItems.Add(CreateQueueItem("Unsupported.nzb", "whisparr"));
        await dbContext.SaveChangesAsync();

        var service = new ArrOperationsService(CreateArrConfigManager("http://sonarr:8989", mode: "report"));
        var validation = await service.BuildValidationAsync(new DavDatabaseClient(dbContext));

        Assert.Equal(0, validation.QueueItems);
        Assert.Equal(1, validation.IgnoredQueueItems);
        Assert.Equal(100, validation.CorrelationCoveragePercent);
        Assert.DoesNotContain(validation.Issues, x => x.Code == "queue_uncorrelated");
        Assert.DoesNotContain(validation.Issues, x => x.Code == "queue_partial_correlation");
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

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("forced ARR transaction failure"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }

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

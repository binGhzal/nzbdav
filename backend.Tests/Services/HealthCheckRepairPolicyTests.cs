using System.Data.Common;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Reflection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;
using NzbWebDAV.Exceptions;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public class HealthCheckRepairPolicyTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public HealthCheckRepairPolicyTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/media/movies/Movie/Movie.mkv", "/media/movies")]
    [InlineData("/media/movies", "/media/movies")]
    [InlineData(@"C:\Media\Movies\Movie\Movie.mkv", @"C:\Media\Movies")]
    public void IsPathInsideRootReturnsTrueForMatchingRoot(string path, string rootPath)
    {
        Assert.True(HealthCheckService.IsPathInsideRoot(path, rootPath));
    }

    [Theory]
    [InlineData("/media/movies2/Movie/Movie.mkv", "/media/movies")]
    [InlineData("/media/movie", "/media/movies")]
    [InlineData("/media/tv/Series/Episode.mkv", "/media/movies")]
    [InlineData("/media/movies/Movie/Movie.mkv", null)]
    [InlineData("", "/media/movies")]
    public void IsPathInsideRootReturnsFalseForNonMatchingRoot(string path, string? rootPath)
    {
        Assert.False(HealthCheckService.IsPathInsideRoot(path, rootPath));
    }

    [Fact]
    public void CheckCachedMissingSegmentIdsIgnoresBlankSegmentMetadata()
    {
        HealthCheckService.CheckCachedMissingSegmentIds(["", " "]);
    }

    [Fact]
    public async Task ApplyMissingSegmentPolicyMarksActionNeededForDefinitiveMissingSegments()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(davItem);

        var now = DateTimeOffset.UtcNow;
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult("segment-1", SegmentCheckState.Missing, Provider: null, Error: "missing")
        ]);

        HealthCheckService.ApplyMissingSegmentPolicy(davItem, dbClient, batch, now);
        await dbContext.SaveChangesAsync();

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(now, davItem.LastHealthCheck);
        Assert.Equal(now.AddHours(6), davItem.NextHealthCheck);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Contains("missing articles", result.Message);
    }

    [Theory]
    [InlineData(SegmentCheckState.ProviderError, "provider_errors=1")]
    [InlineData(SegmentCheckState.Unknown, "unknown=1")]
    public async Task ApplyUnknownVerificationPolicyRetriesIndeterminateResultsWithoutRepairAction(
        SegmentCheckState state,
        string expectedMessagePart)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(davItem);

        var now = DateTimeOffset.UtcNow;
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult("segment-1", state, Provider: "primary", Error: "timeout")
        ]);

        HealthCheckService.ApplyUnknownVerificationPolicy(davItem, dbClient, batch, now);
        await dbContext.SaveChangesAsync();

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(now, davItem.LastHealthCheck);
        Assert.Equal(now.AddMinutes(15), davItem.NextHealthCheck);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
        Assert.Contains(expectedMessagePart, result.Message);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
    }

    [Theory]
    [InlineData(SegmentCheckState.ProviderError)]
    [InlineData(SegmentCheckState.Unknown)]
    public async Task PerformHealthCheckRetriesIndeterminateVerificationWithoutRepairJob(SegmentCheckState state)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult("segment-1", state, Provider: "primary", Error: "timeout")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager(), batch);
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
    }

    [Fact]
    public async Task PerformHealthCheckDoesNotRepairWhenReleaseDateProbeMissesButBatchIsIndeterminate()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult("segment-1", SegmentCheckState.ProviderError, Provider: "primary", Error: "timeout")
        ]);
        using var usenetClient = new ProbeMissingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager(), batch);
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Null(davItem.ReleaseDate);
        Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
    }

    [Fact]
    public async Task PerformHealthCheckEnqueuesRepairJobForDefinitiveMissingSegments()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult("segment-1", SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager(), batch);
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        var repairJob = await dbContext.WorkerJobs.SingleAsync();
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Equal(WorkerJob.JobKind.Repair, repairJob.Kind);
        Assert.Equal(WorkerJob.JobStatus.Pending, repairJob.Status);
        Assert.Equal(davItem.Id, repairJob.TargetId);
    }

    [Fact]
    public async Task PerformHealthCheckEnqueuesRepairJobWhenFileMetadataIsMissing()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 4, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        var repairJob = await dbContext.WorkerJobs.SingleAsync();
        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Contains("metadata", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkerJob.JobKind.Repair, repairJob.Kind);
        Assert.Equal(davItem.Id, repairJob.TargetId);
    }

    [Fact]
    public async Task PerformHealthCheckChecksEachSegmentOnlyOnce()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1", "segment-1", "segment-2"]
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        Assert.Equal(["segment-1", "segment-2"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformHealthCheckUsesArchiveSegmentSlicesWhenAvailable()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv", DavItem.ItemSubType.MultipartFile);
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = davItem.Id,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["segment-1", "segment-2", "segment-3"],
                        SegmentSlices =
                        [
                            new DavMultipartFile.SegmentSlice
                            {
                                SegmentId = "segment-2",
                                SegmentByteRange = LongRange.FromStartAndSize(0, 10),
                                FilePartByteRange = LongRange.FromStartAndSize(0, 10)
                            }
                        ]
                    }
                ]
            }
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        Assert.Equal(["segment-2"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformHealthCheckFallsBackToLegacyMultipartSegmentsWhenSliceMetadataIsCorrupt()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv", DavItem.ItemSubType.MultipartFile);
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = davItem.Id,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["legacy-segment"],
                        SegmentSlices = [null!]
                    }
                ]
            }
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        Assert.Equal(["legacy-segment"], usenetClient.CheckedSegments);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, (await dbContext.HealthCheckResults.SingleAsync()).Result);
    }

    [Fact]
    public async Task PerformHealthCheckFallsBackToLegacyMultipartSegmentsWhenAnySliceMetadataIsCorrupt()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv", DavItem.ItemSubType.MultipartFile);
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = davItem.Id,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["legacy-segment-1", "legacy-segment-2"],
                        SegmentSlices =
                        [
                            new DavMultipartFile.SegmentSlice
                            {
                                SegmentId = "slice-segment-1",
                                SegmentByteRange = LongRange.FromStartAndSize(0, 1),
                                FilePartByteRange = LongRange.FromStartAndSize(0, 1)
                            },
                            null!
                        ]
                    }
                ]
            }
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        Assert.Equal(["legacy-segment-1", "legacy-segment-2"], usenetClient.CheckedSegments);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, (await dbContext.HealthCheckResults.SingleAsync()).Result);
    }

    [Fact]
    public async Task PerformHealthCheckSchedulesFutureCheckWhenReleaseDateLookupFailsButFileIsHealthy()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new HeadFailingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        Assert.Null(davItem.ReleaseDate);
        Assert.NotNull(davItem.NextHealthCheck);
        Assert.True(davItem.NextHealthCheck > davItem.LastHealthCheck);
    }

    [Fact]
    public async Task PerformHealthCheckCanSkipReleaseDateProbeForPostDownloadVerification()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = ["segment-1"]
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new HeadRecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true);

        Assert.Equal(0, usenetClient.HeadCalls);
        Assert.Equal(["segment-1"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformHealthCheckUsesRecentlyVerifiedSegmentsForPrioritizedPostDownloadChecks()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var first = CreateDavItem("/content/First.mkv");
        var second = CreateDavItem("/content/Second.mkv");
        first.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        second.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.AddRange(first, second);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile
            {
                Id = first.Id,
                SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2"]
            },
            new DavNzbFile
            {
                Id = second.Id,
                SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2", $"{prefix}-segment-3"]
            });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            first,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);
        await service.PerformHealthCheckAsync(
            second,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(2, usenetClient.CheckCalls.Count);
        Assert.Equal([$"{prefix}-segment-1", $"{prefix}-segment-2"], usenetClient.CheckCalls[0]);
        Assert.Equal([$"{prefix}-segment-3"], usenetClient.CheckCalls[1]);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckBatchesDatabaseBackedNzbMetadataLookup()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("FROM \"DavNzbFiles\"", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory("/content/Batch", DavItem.ContentFolder.Id, historyId);
        var first = CreateDavItem("/content/Batch/First.mkv", parentId: directory.Id, historyId: historyId);
        var second = CreateDavItem("/content/Batch/Second.mkv", parentId: directory.Id, historyId: historyId);
        var third = CreateDavItem("/content/Batch/Third.mkv", parentId: directory.Id, historyId: historyId);
        dbContext.Items.AddRange(directory, first, second, third);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile { Id = first.Id, SegmentIds = ["segment-1"] },
            new DavNzbFile { Id = second.Id, SegmentIds = ["segment-2"] },
            new DavNzbFile { Id = third.Id, SegmentIds = ["segment-3"] });
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(1, interceptor.Count);
        Assert.Equal(["segment-1", "segment-2", "segment-3"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckUsesHistoryIdInsteadOfPathPrefixForPostDownloadDirectory()
    {
        var interceptor = new CountingCommandInterceptor(commandText =>
            commandText.Contains("FROM \"DavItems\"", StringComparison.OrdinalIgnoreCase)
            && (commandText.Contains("\"Path\" LIKE", StringComparison.OrdinalIgnoreCase)
                || commandText.Contains("instr(\"d\".\"Path\"", StringComparison.OrdinalIgnoreCase)));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory("/content/PostDownload", DavItem.ContentFolder.Id, historyId);
        var file = CreateDavItem("/content/PostDownload/Movie.mkv", parentId: directory.Id, historyId: historyId);
        dbContext.Items.AddRange(directory, file);
        dbContext.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [$"{prefix}-segment-1"] });
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(0, interceptor.Count);
        Assert.Equal([$"{prefix}-segment-1"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckFiltersVideoDavItemsInDatabase()
    {
        var interceptor = new CommandTextCollector(commandText =>
            commandText.Contains("FROM \"DavItems\"", StringComparison.OrdinalIgnoreCase)
            && commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory("/content/PostDownload", DavItem.ContentFolder.Id, historyId);
        var video = CreateDavItem("/content/PostDownload/Movie.mkv", parentId: directory.Id, historyId: historyId);
        var poster = CreateDavItem("/content/PostDownload/Poster.jpg", parentId: directory.Id, historyId: historyId);
        var nfo = CreateDavItem("/content/PostDownload/Movie.nfo", parentId: directory.Id, historyId: historyId);
        dbContext.Items.AddRange(directory, video, poster, nfo);
        dbContext.NzbFiles.Add(new DavNzbFile { Id = video.Id, SegmentIds = [$"{prefix}-segment-1"] });
        await dbContext.SaveChangesAsync();
        interceptor.Reset();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        var davItemQuery = Assert.Single(interceptor.CommandTexts);
        Assert.Contains(".mkv", davItemQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([$"{prefix}-segment-1"], usenetClient.CheckedSegments);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckDoesNotMarkMissingMetadataItemHealthy()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory("/content/Mixed", DavItem.ContentFolder.Id, historyId);
        var healthy = CreateDavItem("/content/Mixed/Healthy.mkv", parentId: directory.Id, historyId: historyId);
        var missingMetadata = CreateDavItem("/content/Mixed/Missing.mkv", parentId: directory.Id, historyId: historyId);
        dbContext.Items.AddRange(directory, healthy, missingMetadata);
        dbContext.NzbFiles.Add(new DavNzbFile { Id = healthy.Id, SegmentIds = ["segment-1"] });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        var results = await dbContext.HealthCheckResults
            .OrderBy(x => x.Path)
            .ToListAsync();
        Assert.Equal(2, results.Count);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, results.Single(x => x.DavItemId == healthy.Id).Result);
        var missingResult = results.Single(x => x.DavItemId == missingMetadata.Id);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, missingResult.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, missingResult.RepairStatus);
        Assert.DoesNotContain(results, x => x.DavItemId == directory.Id);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckTreatsNullBlobArchiveMetadataAsMissingMetadata()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var historyId = Guid.NewGuid();
        var directory = CreateDirectory("/content/Archive", DavItem.ContentFolder.Id, historyId);
        var archive = CreateDavItem(
            "/content/Archive/Movie.mkv",
            DavItem.ItemSubType.RarFile,
            parentId: directory.Id,
            historyId: historyId);
        archive.FileBlobId = Guid.NewGuid();
        dbContext.Items.AddRange(directory, archive);
        await dbContext.SaveChangesAsync();
        await BlobStore.WriteBlob(archive.FileBlobId.Value, new DavRarFile
        {
            Id = archive.Id,
            RarParts = null!
        });

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Empty(usenetClient.CheckCalls);
        var result = await dbContext.HealthCheckResults.SingleAsync(x => x.DavItemId == archive.Id);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckRetriesWhenBlobMetadataIsTemporarilyUnreadable()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var historyId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var directory = CreateDirectory("/content/Archive", DavItem.ContentFolder.Id, historyId);
        var video = CreateDavItem(
            "/content/Archive/Movie.mkv",
            parentId: directory.Id,
            historyId: historyId);
        video.FileBlobId = blobId;
        dbContext.Items.AddRange(directory, video);
        await dbContext.SaveChangesAsync();
        await BlobStore.WriteBlob(blobId, new DavNzbFile
        {
            Id = video.Id,
            SegmentIds = ["segment-1"]
        });

        await using var lockedBlob = new FileStream(
            GetBlobPath(blobId),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Empty(usenetClient.CheckCalls);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
        var result = await dbContext.HealthCheckResults.SingleAsync(x => x.DavItemId == video.Id);
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
        Assert.Contains("metadata", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerformHealthCheckSkipsSegmentsRememberedFromSuccessfulDownload()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var davItem = CreateDavItem($"/content/{prefix}-Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2"]
        });
        await dbContext.SaveChangesAsync();
        HealthCheckService.RememberRecentlyVerifiedSegmentIds(
            [$"{prefix}-segment-1", $"{prefix}-segment-2"]);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, (await dbContext.HealthCheckResults.SingleAsync()).Result);
    }

    [Fact]
    public async Task PerformHealthCheckKeepsRecentlyVerifiedSegmentsLongEnoughForLargeDownloads()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var segmentId = $"{prefix}-segment-1";
        var davItem = CreateDavItem($"/content/{prefix}-Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [segmentId]
        });
        await dbContext.SaveChangesAsync();
        HealthCheckService.RememberRecentlyVerifiedSegmentIds([segmentId]);
        BackdateRecentlyVerifiedSegment(segmentId, DateTimeOffset.UtcNow.AddHours(-2));

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, (await dbContext.HealthCheckResults.SingleAsync()).Result);
    }

    [Fact]
    public async Task PerformHealthCheckBatchesDirectoryPostDownloadVerificationAcrossFiles()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var historyId = Guid.NewGuid();
        var mountFolder = CreateDirectory(
            $"/content/{prefix}",
            parentId: DavItem.ContentFolder.Id,
            historyId);
        var first = CreateDavItem(
            $"/content/{prefix}/First.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        first.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        var second = CreateDavItem(
            $"/content/{prefix}/Second.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        second.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        var subtitle = CreateDavItem(
            $"/content/{prefix}/Subtitle.srt",
            parentId: mountFolder.Id,
            historyId: historyId);
        subtitle.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.AddRange(mountFolder, first, second, subtitle);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile
            {
                Id = first.Id,
                SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2"]
            },
            new DavNzbFile
            {
                Id = second.Id,
                SegmentIds = [$"{prefix}-segment-2", $"{prefix}-segment-3"]
            },
            new DavNzbFile
            {
                Id = subtitle.Id,
                SegmentIds = [$"{prefix}-subtitle-segment"]
            });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            mountFolder,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        var checkCall = Assert.Single(usenetClient.CheckCalls);
        Assert.Equal(
            [$"{prefix}-segment-1", $"{prefix}-segment-2", $"{prefix}-segment-3"],
            checkCall);
        var results = await dbContext.HealthCheckResults
            .OrderBy(x => x.Path)
            .ToListAsync();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(HealthCheckResult.HealthResult.Healthy, result.Result));
        Assert.DoesNotContain(results, result => result.DavItemId == mountFolder.Id);
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckDoesNotEnumerateCleanSegmentResults()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var historyId = Guid.NewGuid();
        var mountFolder = CreateDirectory(
            $"/content/{prefix}",
            parentId: DavItem.ContentFolder.Id,
            historyId);
        var first = CreateDavItem(
            $"/content/{prefix}/First.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        var second = CreateDavItem(
            $"/content/{prefix}/Second.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        dbContext.Items.AddRange(mountFolder, first, second);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile
            {
                Id = first.Id,
                SegmentIds = [$"{prefix}-segment-1"]
            },
            new DavNzbFile
            {
                Id = second.Id,
                SegmentIds = [$"{prefix}-segment-2"]
            });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new CleanBatchWithoutEnumerableResultsStreamingClient(
            new ConfigManager(),
            new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            mountFolder,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: false);

        Assert.Equal(2, usenetClient.CheckedSegments.Count);
        var results = await dbContext.HealthCheckResults
            .OrderBy(x => x.Path)
            .ToListAsync();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(HealthCheckResult.HealthResult.Healthy, result.Result));
    }

    [Fact]
    public async Task DeduplicatedVerificationReportsProgressForDuplicateLogicalSegments()
    {
        using var usenetClient = new RecordingSegmentCheckStreamingClient(
            new ConfigManager(),
            new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        var batch = await InvokeRunDeduplicatedVerificationAsync(
            service,
            ["duplicate-segment", "duplicate-segment", "duplicate-segment"],
            progress);

        Assert.Equal(["duplicate-segment"], usenetClient.CheckedSegments);
        Assert.Equal(3, batch.Results.Count);
        Assert.Equal([3], progress.Values);
    }

    [Fact]
    public async Task PerformHealthCheckTreatsRememberedFallbackCandidateAsVerified()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var candidate = $"{prefix}-candidate";
        var encodedSegmentId = NzbSegmentIdSet.Encode([$"{prefix}-missing", candidate]);
        var davItem = CreateDavItem($"/content/{prefix}-Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [encodedSegmentId]
        });
        await dbContext.SaveChangesAsync();
        HealthCheckService.RememberRecentlyVerifiedSegmentIds([candidate]);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, (await dbContext.HealthCheckResults.SingleAsync()).Result);
    }

    [Fact]
    public async Task PerformHealthCheckRemembersEncodedFallbackCandidatesAfterSuccessfulVerification()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var encodedSegmentId = NzbSegmentIdSet.Encode([$"{prefix}-primary", $"{prefix}-backup"]);
        var davItem = CreateDavItem($"/content/{prefix}-Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [encodedSegmentId]
        });
        await dbContext.SaveChangesAsync();

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);
        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        var checkCall = Assert.Single(usenetClient.CheckCalls);
        Assert.Equal([encodedSegmentId], checkCall);
    }

    [Fact]
    public async Task RunVerificationDoesNotCacheMissingFallbackCandidatesAsVerified()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var missingCandidate = $"{prefix}-missing";
        var presentCandidate = $"{prefix}-present";
        var encodedSegmentId = NzbSegmentIdSet.Encode([missingCandidate, presentCandidate]);

        using var usenetClient = new CandidateFallbackRecordingSegmentCheckStreamingClient(
            new ConfigManager(),
            new WebsocketManager(),
            encodedSegmentId,
            presentCandidate);
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        var firstBatch = await InvokeRunVerificationAsync(
            service,
            [encodedSegmentId],
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);
        var secondBatch = await InvokeRunVerificationAsync(
            service,
            [missingCandidate],
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(2, usenetClient.CheckCalls.Count);
        Assert.Equal(SegmentCheckState.Exists, Assert.Single(firstBatch.Results).State);
        Assert.Equal(SegmentCheckState.Missing, Assert.Single(secondBatch.Results).State);
    }

    [Fact]
    public async Task PerformHealthCheckDeduplicatesConcurrentRecentlyVerifiedSegmentChecks()
    {
        await using (var seedContext = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var prefix = Guid.NewGuid().ToString("N");
            var first = CreateDavItem($"/content/{prefix}-First.mkv");
            var second = CreateDavItem($"/content/{prefix}-Second.mkv");
            first.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
            second.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
            seedContext.Items.AddRange(first, second);
            seedContext.NzbFiles.AddRange(
                new DavNzbFile
                {
                    Id = first.Id,
                    SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2"]
                },
                new DavNzbFile
                {
                    Id = second.Id,
                    SegmentIds = [$"{prefix}-segment-1", $"{prefix}-segment-2"]
                });
            await seedContext.SaveChangesAsync();
        }

        await using var firstContext = await _fixture.CreateMigratedContextAsync();
        await using var secondContext = await _fixture.CreateMigratedContextAsync();
        var firstItem = await firstContext.Items.SingleAsync(x => x.Name.EndsWith("First.mkv"));
        var secondItem = await secondContext.Items.SingleAsync(x => x.Name.EndsWith("Second.mkv"));

        using var usenetClient = new BlockingRecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        var firstTask = service.PerformHealthCheckAsync(
            firstItem,
            new DavDatabaseClient(firstContext),
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);
        await usenetClient.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = service.PerformHealthCheckAsync(
            secondItem,
            new DavDatabaseClient(secondContext),
            concurrency: 4,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            useRecentlyVerifiedSegmentCache: true);

        await Task.Delay(100);
        usenetClient.ReleaseChecks();
        await Task.WhenAll(firstTask, secondTask);

        var checkCall = Assert.Single(usenetClient.CheckCalls);
        Assert.Equal(2, checkCall.Length);
    }

    [Fact]
    public async Task RunVerificationAsyncReportsCachedProgressAsCompletedCount()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var segments = new List<string>
        {
            $"{prefix}-segment-1",
            $"{prefix}-segment-2",
            $"{prefix}-segment-3"
        };
        HealthCheckService.RememberRecentlyVerifiedSegmentIds(segments);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        var batch = await InvokeRunVerificationAsync(
            service,
            segments,
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(3, batch.Checked);
        Assert.Equal(segments, batch.Results.Select(x => x.SegmentId));
        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal([3], progress.Values);
    }

    [Fact]
    public async Task RunVerificationAsyncReportsCachedProgressForDuplicateLogicalSegments()
    {
        var segment = $"{Guid.NewGuid():N}-duplicate-segment";
        var segments = new List<string>
        {
            segment,
            segment,
            segment
        };
        HealthCheckService.RememberRecentlyVerifiedSegmentIds([segment]);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        var batch = await InvokeRunVerificationAsync(
            service,
            segments,
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(3, batch.Checked);
        Assert.Equal(segments, batch.Results.Select(x => x.SegmentId));
        Assert.Empty(usenetClient.CheckCalls);
        Assert.Equal([3], progress.Values);
    }

    [Fact]
    public async Task RunVerificationAsyncReportsCachedSegmentsAsOneProgressStep()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var segments = new List<string>
        {
            $"{prefix}-segment-1",
            $"{prefix}-segment-2",
            $"{prefix}-segment-3"
        };
        HealthCheckService.RememberRecentlyVerifiedSegmentIds(segments);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        await InvokeRunVerificationAsync(
            service,
            segments,
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal([3], progress.Values);
    }

    [Fact]
    public async Task RunVerificationAsyncReturnsLazyCleanBatchWhenCachedAndCheckedSegmentsAreHealthy()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var cachedSegment = $"{prefix}-cached";
        var uncachedSegment = $"{prefix}-uncached";
        var segments = new List<string> { cachedSegment, uncachedSegment };
        HealthCheckService.RememberRecentlyVerifiedSegmentIds([cachedSegment]);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var progress = new RecordingProgress();

        var batch = await InvokeRunVerificationAsync(
            service,
            segments,
            concurrency: 4,
            progress,
            useRecentlyVerifiedSegmentCache: true);

        Assert.Equal(2, batch.Checked);
        AssertLazyAllExists(batch);
        Assert.Equal([uncachedSegment], Assert.Single(usenetClient.CheckCalls));
        Assert.Equal([1, 2], progress.Values);
    }


    [Fact]
    public void GetWorkerSchedulingPolicyProcessesExplicitVerifyJobsWhenRepairAutomationIsDisabled()
    {
        var policy = HealthCheckService.GetWorkerSchedulingPolicy(repairJobEnabled: false);

        Assert.True(policy.ProcessExplicitVerifyJobs);
        Assert.False(policy.AutoEnqueueDueVerifyItems);
        Assert.False(policy.ProcessRepairJobs);
    }

    [Theory]
    [InlineData(40, 4, 0, 40)]
    [InlineData(40, 4, 1, 20)]
    [InlineData(40, 4, 3, 10)]
    [InlineData(2, 4, 3, 1)]
    public void GetVerificationSegmentConcurrencyUsesAvailableBudget(
        int segmentConcurrency,
        int maxVerifyJobs,
        int activeVerifyJobs,
        int expected)
    {
        var concurrency = HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency,
            maxVerifyJobs,
            activeVerifyJobs);

        Assert.Equal(expected, concurrency);
    }

    [Fact]
    public void GetVerificationSegmentConcurrencyUsesPostDownloadBudgetForPrioritizedJobs()
    {
        Assert.Equal(2, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 0,
            workerJobPriority: 0));
        Assert.Equal(8, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 0,
            workerJobPriority: 10));
        Assert.Equal(8, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 3,
            workerJobPriority: 10));
    }

    [Fact]
    public void GetVerificationSegmentConcurrencyDoesNotThrottlePostDownloadJobsBehindBackgroundVerifyWorkers()
    {
        var concurrency = HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 3,
            workerJobPriority: 50);

        Assert.Equal(8, concurrency);
    }

    [Fact]
    public async Task CancelledVerificationWorkerReleasesDurableJobLease()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var targetId = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Verify, targetId, priority: 0, now: DateTimeOffset.UtcNow);
        var workerJob = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Verify,
            owner: "verify-worker",
            leaseDuration: TimeSpan.FromMinutes(30));
        Assert.NotNull(workerJob);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InvokeRunVerificationWorkerAsync(service, workerJob, cts.Token);

        dbContext.ChangeTracker.Clear();
        var savedJob = await dbContext.WorkerJobs.SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Pending, savedJob.Status);
        Assert.Null(savedJob.LeaseOwner);
        Assert.Null(savedJob.LeaseExpiresAt);
    }

    [Fact]
    public async Task PostDownloadVerificationPayloadMarkerUsesRecentlyFetchedSegmentCache()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var segmentId = $"{Guid.NewGuid():N}-segment-1";
        var davItem = CreateDavItem("/content/PostDownload/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [segmentId]
        });
        await dbContext.SaveChangesAsync();
        HealthCheckService.RememberRecentlyVerifiedSegmentIds([segmentId]);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var payloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson();

        await service.PerformHealthCheckAsync(
            davItem,
            dbClient,
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: DavDatabaseClient.IsPostDownloadVerifyPayload(payloadJson),
            useRecentlyVerifiedSegmentCache: DavDatabaseClient.IsPostDownloadVerifyPayload(payloadJson));

        Assert.Empty(usenetClient.CheckCalls);
        dbContext.ChangeTracker.Clear();
        var result = await dbContext.HealthCheckResults.SingleAsync(x => x.DavItemId == davItem.Id);
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, result.Result);
    }

    [Fact]
    public async Task CancelledRepairWorkerReleasesDurableJobLease()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var targetId = Guid.NewGuid();
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, targetId, priority: 0, now: DateTimeOffset.UtcNow);
        var workerJob = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair,
            owner: "repair-worker",
            leaseDuration: TimeSpan.FromMinutes(30));
        Assert.NotNull(workerJob);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InvokeRunRepairWorkerAsync(service, workerJob, cts.Token);

        dbContext.ChangeTracker.Clear();
        var savedJob = await dbContext.WorkerJobs.SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Pending, savedJob.Status);
        Assert.Null(savedJob.LeaseOwner);
        Assert.Null(savedJob.LeaseExpiresAt);
    }

    [Fact]
    public async Task RepairWorkerLeaseSkipsActiveHealthCheckTargets()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var activeTargetId = Guid.NewGuid();
        var nextTargetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, activeTargetId, priority: 100, now: now);
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, nextTargetId, priority: 10, now: now);

        var leasedJob = await InvokeLeaseNextRepairJobAsync([activeTargetId], CancellationToken.None);

        Assert.NotNull(leasedJob);
        Assert.Equal(nextTargetId, leasedJob.TargetId);

        dbContext.ChangeTracker.Clear();
        var activeJob = await dbContext.WorkerJobs.SingleAsync(x => x.TargetId == activeTargetId);
        Assert.Equal(WorkerJob.JobStatus.Pending, activeJob.Status);
        Assert.Null(activeJob.LeaseOwner);
        Assert.Null(activeJob.LeaseExpiresAt);
        Assert.Equal(0, activeJob.Attempts);
    }

    [Fact]
    public async Task TryStartRepairWorkerAsyncReleasesLeaseWhenTargetBecomesActiveAfterLease()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var targetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await dbClient.EnqueueWorkerJobAsync(WorkerJob.JobKind.Repair, targetId, priority: 0, now: now);
        var workerJob = await dbClient.LeaseNextWorkerJobAsync(
            WorkerJob.JobKind.Repair,
            owner: "repair-worker",
            leaseDuration: TimeSpan.FromMinutes(30),
            now: now);
        Assert.NotNull(workerJob);

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        MarkHealthCheckActive(service, targetId);

        var started = await InvokeTryStartRepairWorkerAsync(service, workerJob, CancellationToken.None);

        Assert.False(started);
        dbContext.ChangeTracker.Clear();
        var savedJob = await dbContext.WorkerJobs.SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Pending, savedJob.Status);
        Assert.Null(savedJob.LeaseOwner);
        Assert.Null(savedJob.LeaseExpiresAt);
    }

    private static DavItem CreateDavItem(
        string path,
        DavItem.ItemSubType subType = DavItem.ItemSubType.NzbFile,
        Guid? parentId = null,
        Guid? historyId = null)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId ?? DavItem.ContentFolder.Id,
            Name = Path.GetFileName(path),
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = subType,
            Path = path,
            HistoryItemId = historyId
        };
    }

    private static DavItem CreateDirectory(string path, Guid parentId, Guid? historyId = null)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = Path.GetFileName(path),
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = path,
            HistoryItemId = historyId
        };
    }

    private static string GetBlobPath(Guid id)
    {
        var method = typeof(BlobStore).GetMethod(
            "GetBlobPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, [id])!;
    }

    private static async Task<SegmentCheckBatch> InvokeRunVerificationAsync(
        HealthCheckService service,
        List<string> segments,
        int concurrency,
        IProgress<int> progress,
        bool useRecentlyVerifiedSegmentCache)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "RunVerificationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<SegmentCheckBatch>)method.Invoke(
            service,
            [segments, concurrency, progress, CancellationToken.None, useRecentlyVerifiedSegmentCache])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<SegmentCheckBatch> InvokeRunDeduplicatedVerificationAsync(
        HealthCheckService service,
        IReadOnlyList<string> segments,
        IProgress<int> progress)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "RunDeduplicatedVerificationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<SegmentCheckBatch>)method.Invoke(
            service,
            [segments, 4, progress, CancellationToken.None])!;
        return await task.ConfigureAwait(false);
    }

    private static void BackdateRecentlyVerifiedSegment(string segmentId, DateTimeOffset timestamp)
    {
        var field = typeof(HealthCheckService).GetField(
            "_recentlyVerifiedSegmentIds",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var cache = Assert.IsType<Dictionary<string, DateTimeOffset>>(field.GetValue(null));
        lock (cache)
        {
            cache[segmentId] = timestamp;
        }
    }

    private static void AssertLazyAllExists(SegmentCheckBatch batch)
    {
        Assert.True(batch.IsClean);
        Assert.Contains("AllExistsResults", batch.Results.GetType().Name);
    }

    private static async Task InvokeRunVerificationWorkerAsync(
        HealthCheckService service,
        WorkerJob workerJob,
        CancellationToken ct)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "RunVerificationWorkerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(
            service,
            [workerJob, 1, new NoopDisposable(), ct])!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeRunRepairWorkerAsync(
        HealthCheckService service,
        WorkerJob workerJob,
        CancellationToken ct)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "RunRepairWorkerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(service, [workerJob, ct])!;
        await task.ConfigureAwait(false);
    }

    private static async Task<WorkerJob?> InvokeLeaseNextRepairJobAsync(
        IReadOnlyCollection<Guid> activeHealthCheckIds,
        CancellationToken ct)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "LeaseNextRepairJobAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<WorkerJob?>)method.Invoke(null, [activeHealthCheckIds, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<bool> InvokeTryStartRepairWorkerAsync(
        HealthCheckService service,
        WorkerJob workerJob,
        CancellationToken ct)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "TryStartRepairWorkerAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<bool>)method.Invoke(service, [workerJob, ct])!;
        return await task.ConfigureAwait(false);
    }

    private static void MarkHealthCheckActive(HealthCheckService service, Guid targetId)
    {
        var lockField = typeof(HealthCheckService).GetField(
            "_activeHealthChecksLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var activeField = typeof(HealthCheckService).GetField(
            "_activeHealthChecks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        Assert.NotNull(activeField);
        var sync = Assert.IsType<object>(lockField.GetValue(service));
        var activeIds = Assert.IsType<HashSet<Guid>>(activeField.GetValue(service));
        lock (sync)
        {
            activeIds.Add(targetId);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value)
        {
            Values.Add(value);
        }
    }

    private sealed class CountingCommandInterceptor(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset()
        {
            Volatile.Write(ref _count, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CountIfMatched(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CountIfMatched(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CountIfMatched(DbCommand command)
        {
            if (predicate(command.CommandText))
                Interlocked.Increment(ref _count);
        }
    }

    private sealed class CommandTextCollector(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<string> CommandTexts
        {
            get
            {
                lock (_lock)
                {
                    return _commandTexts.ToList();
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _commandTexts.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            CollectIfMatched(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync
        (
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CollectIfMatched(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CollectIfMatched(DbCommand command)
        {
            if (!predicate(command.CommandText)) return;
            lock (_lock)
            {
                _commandTexts.Add(command.CommandText);
            }
        }
    }

    private class FixedSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        SegmentCheckBatch batch)
        : UsenetStreamingClient(configManager, websocketManager)
    {
        public override Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            foreach (var _ in segmentIds)
                progress?.Report(1);

            return Task.FromResult(batch);
        }
    }

    private sealed class ProbeMissingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        SegmentCheckBatch batch)
        : FixedSegmentCheckStreamingClient(configManager, websocketManager, batch)
    {
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            throw new UsenetArticleNotFoundException(segmentId);
        }
    }

    private sealed class HeadFailingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : RecordingSegmentCheckStreamingClient(configManager, websocketManager)
    {
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            throw new TimeoutException("HEAD timed out");
        }
    }

    private sealed class HeadRecordingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : RecordingSegmentCheckStreamingClient(configManager, websocketManager)
    {
        public int HeadCalls { get; private set; }

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            HeadCalls++;
            return Task.FromResult(new UsenetHeadResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadFollows,
                ResponseMessage = "221 - Head retrieved",
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                }
            });
        }
    }

    private class RecordingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : UsenetStreamingClient(configManager, websocketManager)
    {
        public IReadOnlyList<string> CheckedSegments { get; protected set; } = [];
        public List<string[]> CheckCalls { get; } = [];

        public override Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            CheckedSegments = segmentIds.ToArray();
            CheckCalls.Add(CheckedSegments.ToArray());
            foreach (var _ in CheckedSegments)
                progress?.Report(1);

            return Task.FromResult(SegmentCheckBatch.FromResults(
                CheckedSegments
                    .Select(x => new SegmentCheckResult(x, SegmentCheckState.Exists, Provider: null, Error: null))
                    .ToArray()));
        }
    }

    private sealed class CandidateFallbackRecordingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        string encodedSegmentId,
        string presentCandidate)
        : RecordingSegmentCheckStreamingClient(configManager, websocketManager)
    {
        public override Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            CheckedSegments = segmentIds.ToArray();
            CheckCalls.Add(CheckedSegments.ToArray());
            foreach (var _ in CheckedSegments)
                progress?.Report(1);

            return Task.FromResult(SegmentCheckBatch.FromResults(
                CheckedSegments
                    .Select(segmentId => segmentId == encodedSegmentId
                        ? new SegmentCheckResult(
                            segmentId,
                            SegmentCheckState.Exists,
                            Provider: null,
                            Error: null,
                            CandidateSegmentId: presentCandidate)
                        : new SegmentCheckResult(
                            segmentId,
                            SegmentCheckState.Missing,
                            Provider: null,
                            Error: "missing",
                            CandidateSegmentId: segmentId))
                    .ToArray()));
        }
    }

    private sealed class CleanBatchWithoutEnumerableResultsStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : UsenetStreamingClient(configManager, websocketManager)
    {
        public IReadOnlyList<string> CheckedSegments { get; private set; } = [];

        public override Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            CheckedSegments = segmentIds.ToArray();
            progress?.Report(CheckedSegments.Count);
            return Task.FromResult(new SegmentCheckBatch(
                new ThrowOnEnumerationSegmentCheckResults(CheckedSegments.Count),
                Checked: CheckedSegments.Count,
                Missing: 0,
                ProviderErrors: 0,
                Unknown: 0));
        }
    }

    private sealed class ThrowOnEnumerationSegmentCheckResults(int count) : IReadOnlyList<SegmentCheckResult>
    {
        public int Count => count;

        public SegmentCheckResult this[int index] =>
            throw new InvalidOperationException("Clean directory verification should not inspect per-segment results.");

        public IEnumerator<SegmentCheckResult> GetEnumerator()
        {
            throw new InvalidOperationException("Clean directory verification should not enumerate per-segment results.");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class BlockingRecordingSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager)
        : RecordingSegmentCheckStreamingClient(configManager, websocketManager)
    {
        private readonly TaskCompletionSource _releaseChecks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<SegmentCheckBatch> CheckSegmentsAsync
        (
            IEnumerable<string> segmentIds,
            int concurrency,
            IProgress<int>? progress,
            CancellationToken cancellationToken
        )
        {
            FirstCallStarted.TrySetResult();
            await _releaseChecks.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.CheckSegmentsAsync(segmentIds, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        public void ReleaseChecks()
        {
            _releaseChecks.TrySetResult();
        }
    }
}

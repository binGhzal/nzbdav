using System.Data.Common;
using System.Text.Json;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using System.Reflection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
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
        var segmentId = $"{Guid.NewGuid():N}-definitive-missing";
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItem.Id,
            SegmentIds = [segmentId]
        });
        await dbContext.SaveChangesAsync();

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        var configManager = CreateAutomaticRepairEnabledConfig();
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
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
    public async Task PerformHealthCheckDoesNotEnqueueRepairForDefinitiveMissingSegmentsWhenDisabled()
    {
        var segmentId = $"{Guid.NewGuid():N}-missing-disabled";
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        dbContext.NzbFiles.Add(new DavNzbFile { Id = davItem.Id, SegmentIds = [segmentId] });
        await dbContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
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

        var configManager = CreateAutomaticRepairEnabledConfig();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, new WebsocketManager());
        var service = new HealthCheckService(
            configManager,
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
    public async Task PerformHealthCheckDoesNotEnqueueRepairWhenMetadataIsMissingAndRepairIsDisabled()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var davItem = CreateDavItem("/content/Movie.mkv");
        davItem.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.Items.Add(davItem);
        await dbContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, new WebsocketManager());
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
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
    public async Task PerformDirectoryHealthCheckTreatsAbsentSegmentResultsAsUnknownWithoutRepair()
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

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(
                $"{prefix}-segment-1",
                SegmentCheckState.ProviderError,
                Provider: "primary",
                Error: "timeout")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(
            new ConfigManager(),
            new WebsocketManager(),
            batch);
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

        var results = await dbContext.HealthCheckResults
            .OrderBy(x => x.Path)
            .ToListAsync();
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
            Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
            Assert.Contains("could not prove articles are missing", result.Message);
        });
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
    }

    [Fact]
    public async Task PerformDirectoryHealthCheckDoesNotRepairWhenMissingResultIsMixedWithUnknown()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);
        var prefix = Guid.NewGuid().ToString("N");
        var historyId = Guid.NewGuid();
        var mountFolder = CreateDirectory(
            $"/content/{prefix}",
            parentId: DavItem.ContentFolder.Id,
            historyId);
        var file = CreateDavItem(
            $"/content/{prefix}/Movie.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        dbContext.Items.AddRange(mountFolder, file);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = file.Id,
            SegmentIds = [$"{prefix}-missing", $"{prefix}-unknown"]
        });
        await dbContext.SaveChangesAsync();

        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(
                $"{prefix}-missing",
                SegmentCheckState.Missing,
                Provider: "primary",
                Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(
            new ConfigManager(),
            new WebsocketManager(),
            batch);
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

        var result = await dbContext.HealthCheckResults.SingleAsync();
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.None, result.RepairStatus);
        Assert.Contains("could not prove articles are missing", result.Message);
        Assert.Empty(await dbContext.WorkerJobs.ToListAsync());
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
    public void GetVerificationSegmentConcurrencySharesPostDownloadBudgetAcrossVerifyLane()
    {
        Assert.Equal(2, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 0,
            workerJobPriority: 0));
        Assert.Equal(2, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 0,
            workerJobPriority: 10));
        Assert.Equal(2, HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 3,
            workerJobPriority: 10));
    }

    [Fact]
    public void GetVerificationSegmentConcurrencyKeepsPostDownloadBudgetIndependentFromCurrentBackgroundWorkers()
    {
        var concurrency = HealthCheckService.GetVerificationSegmentConcurrency(
            segmentConcurrency: 2,
            postDownloadSegmentConcurrency: 8,
            maxVerifyJobs: 4,
            activeVerifyJobs: 3,
            workerJobPriority: 50);

        Assert.Equal(2, concurrency);
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
    public async Task StopAsyncWaitsForVerificationWorkerLeaseRelease()
    {
        var segmentId = $"{Guid.NewGuid():N}-shutdown";
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var target = CreateDavItem("/content/Shutdown/Movie.mkv");
            target.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            await setup.SaveChangesAsync();
            await new DavDatabaseClient(setup).EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                target.Id,
                priority: 0,
                now: DateTimeOffset.UtcNow);
        }

        var configManager = _fixture.CreateConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "queue.max-concurrent-verify", ConfigValue = "1" }
        ]);
        var coordinator = new BlockingReleaseWorkerJobCoordinator(
            new DatabaseWorkerJobCoordinator(
                new ConfigWorkerLaneCapacityPolicy(configManager),
                Options.Create(new WorkerLeaseOptions())));
        using var usenetClient = new BlockingRecordingSegmentCheckStreamingClient(
            configManager,
            new WebsocketManager());
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager(),
            coordinator);

        Task? stopTask = null;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await usenetClient.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stopTask = service.StopAsync(CancellationToken.None);
            await coordinator.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(stopTask.IsCompleted);
            Assert.False(coordinator.ReleaseCancellationToken.CanBeCanceled);

            coordinator.AllowRelease();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.AllowRelease();
            stopTask ??= service.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task VerificationWorkerErrorUsesUncancelledTokenForTerminalLeaseAcknowledgement()
    {
        WorkerJob workerJob;
        var segmentId = $"{Guid.NewGuid():N}-worker-error";
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var target = CreateDavItem("/content/WorkerError/Movie.mkv");
            target.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            await setup.SaveChangesAsync();

            workerJob = CreateLeasedWorkerJob(WorkerJob.JobKind.Verify);
            workerJob.TargetId = target.Id;
        }

        using var stoppingCts = new CancellationTokenSource();
        var configManager = _fixture.CreateConfigManager();
        var coordinator = new AcceptingWorkerJobCoordinator();
        using var usenetClient = new CancellingFailureSegmentCheckStreamingClient(
            configManager,
            new WebsocketManager(),
            stoppingCts.Cancel);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager(),
            coordinator);

        await InvokeRunVerificationWorkerAsync(service, workerJob, stoppingCts.Token);

        Assert.True(stoppingCts.IsCancellationRequested);
        Assert.Equal(1, coordinator.FailCalls);
        Assert.False(coordinator.FailureCancellationToken.CanBeCanceled);
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

    [Theory]
    [InlineData(SegmentCheckState.ProviderError)]
    [InlineData(SegmentCheckState.Unknown)]
    public async Task PostDownloadIndeterminateOutcomeRetriesVerifyWithoutChangingImportState(
        SegmentCheckState state)
    {
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-indeterminate";
        WorkerJob workerJob;
        Guid receiptId;
        Guid commandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var mountFolder = CreateDirectory("/content/tv/Indeterminate", DavItem.ContentFolder.Id, historyId);
            var file = CreateDavItem(
                "/content/tv/Indeterminate/Movie.mkv",
                parentId: mountFolder.Id,
                historyId: historyId);
            setup.Items.AddRange(mountFolder, file);
            setup.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, mountFolder.Id, "Indeterminate"));
            var receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = file.Id,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ImportedAt = DateTimeOffset.UtcNow
            };
            receiptId = receipt.Id;
            setup.ImportReceipts.Add(receipt);
            var command = CreateArrImportCommand(historyId, ArrImportCommandStatus.Pending);
            commandId = command.Id;
            setup.ArrImportCommands.Add(command);
            await setup.SaveChangesAsync();
            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                mountFolder.Id,
                priority: 50,
                payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson());
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                "verify-worker",
                TimeSpan.FromMinutes(5)));
        }

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, state, Provider: "primary", Error: "provider timeout")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await InvokeRunVerificationWorkerAsync(service, workerJob, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var savedJob = await assertionContext.WorkerJobs.AsNoTracking().SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Retry, savedJob.Status);
        Assert.Equal(WorkerJob.FailureClass.Provider, savedJob.FailureKind);
        Assert.Equal(
            HistoryItem.DownloadStatusOption.Completed,
            (await assertionContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId)).DownloadStatus);
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId)).State);
        Assert.Equal(
            ArrImportCommandStatus.Pending,
            (await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == commandId)).Status);
        Assert.Empty(await assertionContext.WorkerJobs.AsNoTracking()
            .Where(x => x.Kind == WorkerJob.JobKind.Repair)
            .ToListAsync());
    }

    [Fact]
    public async Task PrioritizedVerifyWithoutPostDownloadPayloadKeepsRegularWorkerSemantics()
    {
        var segmentId = $"{Guid.NewGuid():N}-regular-priority";
        WorkerJob workerJob;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var file = CreateDavItem("/content/RegularPriority.mkv");
            file.ReleaseDate = DateTimeOffset.UtcNow.AddDays(-1);
            setup.Items.Add(file);
            setup.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
            await setup.SaveChangesAsync();
            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                file.Id,
                priority: 10,
                payloadJson: null);
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                "verify-worker",
                TimeSpan.FromMinutes(5)));
        }

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(
                segmentId,
                SegmentCheckState.ProviderError,
                Provider: "primary",
                Error: "provider timeout")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await InvokeRunVerificationWorkerAsync(service, workerJob, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var savedJob = await assertionContext.WorkerJobs.AsNoTracking().SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Completed, savedJob.Status);
        Assert.Null(savedJob.FailureKind);
    }

    [Fact]
    public async Task PostDownloadConfirmedMissingWithoutRepairQuarantinesImportDomainAndFencesStaleArrLease()
    {
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-missing";
        WorkerJob workerJob;
        ArrImportCommand staleCommand;
        var receiptStates = new[]
        {
            ImportReceiptState.Available,
            ImportReceiptState.UnlinkClaimed,
            ImportReceiptState.Imported,
            ImportReceiptState.Removed
        };
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var mountFolder = CreateDirectory("/content/tv/Missing", DavItem.ContentFolder.Id, historyId);
            var file = CreateDavItem(
                "/content/tv/Missing/Movie.mkv",
                parentId: mountFolder.Id,
                historyId: historyId);
            setup.Items.AddRange(mountFolder, file);
            setup.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, mountFolder.Id, "Missing"));
            setup.ImportReceipts.AddRange(receiptStates.Select((state, index) => new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = index == 0 ? file.Id : Guid.NewGuid(),
                HistoryItemId = historyId,
                State = state,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ImportedAt = state == ImportReceiptState.Imported ? DateTimeOffset.UtcNow : null,
                RemovedAt = state == ImportReceiptState.Removed ? DateTimeOffset.UtcNow : null
            }));
            staleCommand = CreateArrImportCommand(historyId, ArrImportCommandStatus.Executing);
            staleCommand.LeaseToken = Guid.NewGuid();
            staleCommand.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            setup.ArrImportCommands.Add(staleCommand);
            await setup.SaveChangesAsync();
            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                mountFolder.Id,
                priority: 50,
                payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson());
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                "verify-worker",
                TimeSpan.FromMinutes(5)));
        }

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        var domainWasDurableBeforeWorkerFailure = false;
        var workerCoordinator = new InspectingWorkerJobCoordinator(
            new DatabaseWorkerJobCoordinator(
                new ConfigWorkerLaneCapacityPolicy(configManager),
                Options.Create(new WorkerLeaseOptions())),
            async ct =>
            {
                await using var observationContext = await _fixture.CreateMigratedContextAsync();
                var receipts = await observationContext.ImportReceipts
                    .AsNoTracking()
                    .Where(x => x.HistoryItemId == historyId)
                    .ToListAsync(ct);
                domainWasDurableBeforeWorkerFailure =
                    (await observationContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId, ct))
                    .DownloadStatus == HistoryItem.DownloadStatusOption.Failed
                    && receipts.Count(x => x.State == ImportReceiptState.VerificationQuarantined) == 3
                    && receipts.Count(x => x.State == ImportReceiptState.Removed) == 1
                    && (await observationContext.ArrImportCommands.AsNoTracking()
                        .SingleAsync(x => x.Id == staleCommand.Id, ct)).Status == ArrImportCommandStatus.Quarantined
                    && (await observationContext.WorkerJobs.AsNoTracking()
                        .SingleAsync(x => x.Id == workerJob.Id, ct)).Status == WorkerJob.JobStatus.Leased;
            });
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager(),
            workerCoordinator);

        await InvokeRunVerificationWorkerAsync(service, workerJob, CancellationToken.None);
        Assert.True(domainWasDurableBeforeWorkerFailure);

        await using (var assertionContext = await _fixture.CreateMigratedContextAsync())
        {
            var history = await assertionContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId);
            Assert.Equal(HistoryItem.DownloadStatusOption.Failed, history.DownloadStatus);
            Assert.Contains("disabled", history.FailMessage, StringComparison.OrdinalIgnoreCase);
            var receipts = await assertionContext.ImportReceipts.AsNoTracking()
                .Where(x => x.HistoryItemId == historyId)
                .OrderBy(x => x.State)
                .ToListAsync();
            Assert.Equal(3, receipts.Count(x => x.State == ImportReceiptState.VerificationQuarantined));
            Assert.Single(receipts, x => x.State == ImportReceiptState.Removed);
            var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == staleCommand.Id);
            Assert.Equal(ArrImportCommandStatus.Quarantined, command.Status);
            Assert.NotNull(command.VisibleAt);
            Assert.Null(command.LeaseToken);
            Assert.Null(command.LeaseExpiresAt);
            Assert.Contains("disabled", command.LastError, StringComparison.OrdinalIgnoreCase);
            var savedJob = await assertionContext.WorkerJobs.AsNoTracking().SingleAsync(x => x.Id == workerJob.Id);
            Assert.Equal(WorkerJob.JobStatus.Quarantined, savedJob.Status);
            Assert.Equal(WorkerJob.FailureClass.InvalidData, savedJob.FailureKind);
            Assert.Empty(await assertionContext.WorkerJobs.AsNoTracking()
                .Where(x => x.Kind == WorkerJob.JobKind.Repair)
                .ToListAsync());
        }

        await InvokeReleaseArrLeaseAsync(staleCommand, ArrImportCommandStatus.Dispatched);

        await using var fencedContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            ArrImportCommandStatus.Quarantined,
            (await fencedContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == staleCommand.Id)).Status);
    }

    [Fact]
    public async Task PostDownloadQuarantineSurvivesSabHistoryRemovalWithoutRemovalBroadcast()
    {
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-quarantine-remove";
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var directory = CreateDirectory("/content/tv/QuarantineRemove", DavItem.ContentFolder.Id, historyId);
        var file = CreateDavItem(
            "/content/tv/QuarantineRemove/Episode.mkv",
            parentId: directory.Id,
            historyId: historyId);
        dbContext.Items.AddRange(directory, file);
        dbContext.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
        dbContext.HistoryItems.Add(CreateCompletedHistory(historyId, directory.Id, "QuarantineRemove"));
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = file.Id,
            HistoryItemId = historyId,
            State = ImportReceiptState.Imported,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ImportedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        var importCommand = CreateArrImportCommand(historyId, ArrImportCommandStatus.Pending);
        dbContext.ArrImportCommands.Add(importCommand);
        await dbContext.SaveChangesAsync();

        var websocketManager = new WebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);
        var dbClient = new DavDatabaseClient(dbContext);
        await service.PerformHealthCheckAsync(
            directory,
            dbClient,
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            verificationQuarantineTarget: directory,
            automaticRepairEnabledOverride: false);
        dbContext.ChangeTracker.Clear();
        var quarantinedReceipt = await dbContext.ImportReceipts.AsNoTracking()
            .SingleAsync(x => x.HistoryItemId == historyId);
        var quarantineReason = quarantinedReceipt.Detail;

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(
            $"?value={historyId}&del_completed_files=1");
        var request = await RemoveFromHistoryRequest.New(httpContext);
        var response = await new RemoveFromHistoryController(
                httpContext,
                dbClient,
                configManager,
                websocketManager)
            .RemoveFromHistory(request);

        Assert.True(response.Status);
        dbContext.ChangeTracker.Clear();
        var history = await dbContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, history.DownloadStatus);
        Assert.Contains("confirmed missing", history.FailMessage, StringComparison.OrdinalIgnoreCase);
        var savedReceipt = await dbContext.ImportReceipts.AsNoTracking()
            .SingleAsync(x => x.HistoryItemId == historyId);
        Assert.Equal(ImportReceiptState.VerificationQuarantined, savedReceipt.State);
        Assert.Equal(quarantineReason, savedReceipt.Detail);
        Assert.Equal(
            ArrImportCommandStatus.Quarantined,
            (await dbContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == importCommand.Id)).Status);
        Assert.Empty(await dbContext.HistoryCleanupItems.AsNoTracking().ToListAsync());
        Assert.False(WasHistoryRemovalBroadcast(websocketManager));
    }

    [Fact]
    public async Task PostDownloadConfirmedMissingAfterHistoryRemovalKeepsDurableDiagnostics()
    {
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-late-missing";
        WorkerJob workerJob;
        Guid receiptId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var mountFolder = CreateDirectory("/content/tv/Late", DavItem.ContentFolder.Id, historyId);
            var file = CreateDavItem(
                "/content/tv/Late/Movie.mkv",
                parentId: mountFolder.Id,
                historyId: historyId);
            setup.Items.AddRange(mountFolder, file);
            setup.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
            var history = CreateCompletedHistory(historyId, mountFolder.Id, "Late");
            setup.HistoryItems.Add(history);
            var receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = file.Id,
                HistoryItemId = historyId,
                State = ImportReceiptState.Removed,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RemovedAt = DateTimeOffset.UtcNow
            };
            receiptId = receipt.Id;
            setup.ImportReceipts.Add(receipt);
            setup.ArrImportCommands.Add(CreateArrImportCommand(historyId, ArrImportCommandStatus.Dispatched));
            await setup.SaveChangesAsync();
            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                mountFolder.Id,
                priority: 50,
                payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson());
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                "verify-worker",
                TimeSpan.FromMinutes(5)));
            setup.HistoryItems.Remove(history);
            await setup.SaveChangesAsync();
        }

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await InvokeRunVerificationWorkerAsync(service, workerJob, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Null(await assertionContext.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId));
        Assert.Empty(await assertionContext.ArrImportCommands.AsNoTracking().ToListAsync());
        Assert.Equal(
            ImportReceiptState.Removed,
            (await assertionContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId)).State);
        Assert.Contains(
            "disabled",
            (await assertionContext.HealthCheckResults.AsNoTracking().OrderByDescending(x => x.CreatedAt).FirstAsync()).Message,
            StringComparison.OrdinalIgnoreCase);
        var savedJob = await assertionContext.WorkerJobs.AsNoTracking().SingleAsync(x => x.Id == workerJob.Id);
        Assert.Equal(WorkerJob.JobStatus.Quarantined, savedJob.Status);
        Assert.Equal(WorkerJob.FailureClass.InvalidData, savedJob.FailureKind);
    }

    [Fact]
    public async Task PostDownloadConfirmedMissingAfterNestedHistoryIdentityLossQuarantinesGrandchildReceipt()
    {
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-nested-late-missing";
        WorkerJob workerJob;
        Guid receiptId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var mountFolder = CreateDirectory("/content/tv/LateNested", DavItem.ContentFolder.Id, historyId);
            var seasonFolder = CreateDirectory("/content/tv/LateNested/Season 01", mountFolder.Id, historyId);
            var episode = CreateDavItem(
                "/content/tv/LateNested/Season 01/Episode.mkv",
                parentId: seasonFolder.Id,
                historyId: historyId);
            setup.Items.AddRange(mountFolder, seasonFolder, episode);
            setup.NzbFiles.Add(new DavNzbFile { Id = episode.Id, SegmentIds = [segmentId] });
            var history = CreateCompletedHistory(historyId, mountFolder.Id, "LateNested");
            setup.HistoryItems.Add(history);
            var receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = episode.Id,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ImportedAt = DateTimeOffset.UtcNow
            };
            receiptId = receipt.Id;
            setup.ImportReceipts.Add(receipt);
            await setup.SaveChangesAsync();

            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                mountFolder.Id,
                priority: 50,
                payloadJson: DavDatabaseClient.CreatePostDownloadVerifyPayloadJson());
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Verify,
                "verify-worker",
                TimeSpan.FromMinutes(5)));

            mountFolder.HistoryItemId = null;
            seasonFolder.HistoryItemId = null;
            episode.HistoryItemId = null;
            setup.HistoryItems.Remove(history);
            await setup.SaveChangesAsync();
        }

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, new WebsocketManager(), batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());

        await InvokeRunVerificationWorkerAsync(service, workerJob, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var savedReceipt = await assertionContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId);
        Assert.Equal(ImportReceiptState.VerificationQuarantined, savedReceipt.State);
        Assert.Contains("confirmed missing", savedReceipt.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            WorkerJob.JobStatus.Quarantined,
            (await assertionContext.WorkerJobs.AsNoTracking().SingleAsync(x => x.Id == workerJob.Id)).Status);
    }

    [Fact]
    public async Task QuarantineHealthStatusIsNotPublishedWhenSaveFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-save-failure";
        Guid targetId;
        Guid receiptId;
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/SaveFailure.mkv", historyId: historyId);
            targetId = target.Id;
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, target.Id, "SaveFailure"));
            var receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = target.Id,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ImportedAt = DateTimeOffset.UtcNow
            };
            receiptId = receipt.Id;
            setup.ImportReceipts.Add(receipt);
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var targetItem = await failingContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(failingContext),
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            verificationQuarantineTarget: targetItem,
            automaticRepairEnabledOverride: false));

        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
        Assert.Equal(
            HistoryItem.DownloadStatusOption.Completed,
            (await assertionContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId)).DownloadStatus);
        Assert.Equal(
            ImportReceiptState.Imported,
            (await assertionContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId)).State);
    }

    [Fact]
    public async Task QuarantineHealthStatusIsPublishedOnlyAfterTransactionCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var historyId = Guid.NewGuid();
        var segmentId = $"{Guid.NewGuid():N}-commit-order";
        Guid targetId;
        Guid receiptId;
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/CommitOrder.mkv", historyId: historyId);
            targetId = target.Id;
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, target.Id, "CommitOrder"));
            var receipt = new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = target.Id,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ImportedAt = DateTimeOffset.UtcNow
            };
            receiptId = receipt.Id;
            setup.ImportReceipts.Add(receipt);
            await setup.SaveChangesAsync();
        }

        var commitProbe = new TransactionCommitProbe();
        var operationOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(commitProbe)
            .Options;
        await using var operationContext = new DavDatabaseContext(operationOptions);
        var targetItem = await operationContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new CommitObservingWebsocketManager(() => commitProbe.WasCommitted);
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        var outcome = await service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(operationContext),
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            verificationQuarantineTarget: targetItem,
            automaticRepairEnabledOverride: false);

        Assert.Equal(HealthCheckService.VerificationOutcome.ConfirmedMissing, outcome);
        Assert.Single(websocketManager.CommitObservations);
        Assert.All(websocketManager.CommitObservations, Assert.True);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Single(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
        Assert.Equal(
            HistoryItem.DownloadStatusOption.Failed,
            (await assertionContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId)).DownloadStatus);
        Assert.Equal(
            ImportReceiptState.VerificationQuarantined,
            (await assertionContext.ImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId)).State);
    }

    [Fact]
    public async Task DirectoryQuarantineUsesOneBulkReceiptTransitionForMultipleMissingChildren()
    {
        var receiptUpdateCounter = new NonQueryCountingCommandInterceptor(commandText =>
            commandText.Contains("UPDATE \"ImportReceipts\"", StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(receiptUpdateCounter)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var historyId = Guid.NewGuid();
        var firstSegmentId = $"{Guid.NewGuid():N}-first-missing";
        var secondSegmentId = $"{Guid.NewGuid():N}-second-missing";
        var directory = CreateDirectory("/content/tv/BulkQuarantine", DavItem.ContentFolder.Id, historyId);
        var first = CreateDavItem(
            "/content/tv/BulkQuarantine/First.mkv",
            parentId: directory.Id,
            historyId: historyId);
        var second = CreateDavItem(
            "/content/tv/BulkQuarantine/Second.mkv",
            parentId: directory.Id,
            historyId: historyId);
        dbContext.Items.AddRange(directory, first, second);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile { Id = first.Id, SegmentIds = [firstSegmentId] },
            new DavNzbFile { Id = second.Id, SegmentIds = [secondSegmentId] });
        dbContext.HistoryItems.Add(CreateCompletedHistory(historyId, directory.Id, "BulkQuarantine"));
        dbContext.ImportReceipts.AddRange(new[] { first.Id, second.Id, Guid.NewGuid() }
            .Select(davItemId => new ImportReceipt
            {
                Id = Guid.NewGuid(),
                DavItemId = davItemId,
                HistoryItemId = historyId,
                State = ImportReceiptState.Imported,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ImportedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            }));
        dbContext.ArrImportCommands.Add(CreateArrImportCommand(historyId, ArrImportCommandStatus.Pending));
        await dbContext.SaveChangesAsync();
        receiptUpdateCounter.Reset();

        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(firstSegmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing"),
            new SegmentCheckResult(secondSegmentId, SegmentCheckState.Missing, Provider: "primary", Error: "missing")
        ]);
        var websocketManager = new WebsocketManager();
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        var outcome = await service.PerformHealthCheckAsync(
            directory,
            new DavDatabaseClient(dbContext),
            concurrency: 2,
            CancellationToken.None,
            skipReleaseDateProbe: true,
            verificationQuarantineTarget: directory,
            automaticRepairEnabledOverride: false);

        Assert.Equal(HealthCheckService.VerificationOutcome.ConfirmedMissing, outcome);
        Assert.Equal(1, receiptUpdateCounter.Count);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            3,
            await dbContext.ImportReceipts.CountAsync(x =>
                x.HistoryItemId == historyId
                && x.State == ImportReceiptState.VerificationQuarantined));
        Assert.Equal(
            HistoryItem.DownloadStatusOption.Failed,
            (await dbContext.HistoryItems.SingleAsync(x => x.Id == historyId)).DownloadStatus);
        Assert.Equal(
            ArrImportCommandStatus.Quarantined,
            (await dbContext.ArrImportCommands.SingleAsync(x => x.HistoryItemId == historyId)).Status);
        Assert.Equal(2, await dbContext.HealthCheckResults.CountAsync());
    }

    [Theory]
    [InlineData(SegmentCheckState.ProviderError)]
    [InlineData(SegmentCheckState.Unknown)]
    public async Task InconclusiveHealthStatusIsNotPublishedWhenSaveFails(SegmentCheckState state)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var segmentId = $"{Guid.NewGuid():N}-inconclusive-save-failure";
        Guid targetId;
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/InconclusiveSaveFailure.mkv");
            targetId = target.Id;
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var targetItem = await failingContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, state, Provider: "primary", Error: "provider failure")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(failingContext),
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true));

        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(SegmentCheckState.ProviderError)]
    [InlineData(SegmentCheckState.Unknown)]
    public async Task CallerOwnedTransactionRollbackDoesNotPublishInconclusiveHealthStatus(
        SegmentCheckState state)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        var segmentId = $"{Guid.NewGuid():N}-inconclusive-rollback";
        Guid targetId;
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/InconclusiveRollback.mkv");
            targetId = target.Id;
            setup.Items.Add(target);
            setup.NzbFiles.Add(new DavNzbFile { Id = target.Id, SegmentIds = [segmentId] });
            await setup.SaveChangesAsync();
        }

        await using var operationContext = new DavDatabaseContext(options);
        var targetItem = await operationContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentId, state, Provider: "primary", Error: "provider failure")
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);
        await using var transaction = await operationContext.Database.BeginTransactionAsync();

        var outcome = await service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(operationContext),
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true);
        await transaction.RollbackAsync();

        Assert.Equal(HealthCheckService.VerificationOutcome.Indeterminate, outcome);
        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TemporaryMetadataHealthStatusIsNotPublishedWhenSaveFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        Guid targetId;
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/TemporaryMetadataFailure.mkv");
            targetId = target.Id;
            setup.Items.Add(target);
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var targetItem = await failingContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);
        var method = typeof(HealthCheckService).GetMethod(
            "RecordTemporaryMetadataUnavailableAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var recordTask = Assert.IsAssignableFrom<Task>(method.Invoke(service, [
            targetItem,
            new DavDatabaseClient(failingContext),
            CancellationToken.None,
            null,
            "blob store unavailable"
        ]));

        await Assert.ThrowsAsync<DbUpdateException>(async () => await recordTask);

        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CallerOwnedTransactionRollbackDoesNotPublishTemporaryMetadataHealthStatus()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        Guid targetId;
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var target = CreateDavItem("/content/tv/TemporaryMetadataRollback.mkv");
            targetId = target.Id;
            setup.Items.Add(target);
            await setup.SaveChangesAsync();
        }

        await using var operationContext = new DavDatabaseContext(options);
        var targetItem = await operationContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);
        var method = typeof(HealthCheckService).GetMethod(
            "RecordTemporaryMetadataUnavailableAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await using var transaction = await operationContext.Database.BeginTransactionAsync();
        var recordTask = Assert.IsAssignableFrom<Task>(method.Invoke(service, [
            targetItem,
            new DavDatabaseClient(operationContext),
            CancellationToken.None,
            null,
            "blob store unavailable"
        ]));

        await recordTask;
        await transaction.RollbackAsync();

        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyHealthStatusIsNotPublishedWhenFinalSaveFails(bool directoryTarget)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        Guid targetId;
        string[] segmentIds;
        await using (var setup = new DavDatabaseContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            (targetId, segmentIds) = await SeedHealthyVerificationTargetAsync(setup, directoryTarget);
        }

        var failingOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        await using var failingContext = new DavDatabaseContext(failingOptions);
        var targetItem = await failingContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults(segmentIds
            .Select(x => new SegmentCheckResult(x, SegmentCheckState.Exists, Provider: "primary", Error: null))
            .ToArray());
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(failingContext),
            concurrency: 2,
            CancellationToken.None,
            skipReleaseDateProbe: true));

        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(baseOptions);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerOwnedTransactionRollbackDoesNotPublishHealthyHealthStatus(bool directoryTarget)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        Guid targetId;
        string[] segmentIds;
        await using (var setup = new DavDatabaseContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            (targetId, segmentIds) = await SeedHealthyVerificationTargetAsync(setup, directoryTarget);
        }

        await using var operationContext = new DavDatabaseContext(options);
        var targetItem = await operationContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new RecordingTopicWebsocketManager();
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults(segmentIds
            .Select(x => new SegmentCheckResult(x, SegmentCheckState.Exists, Provider: "primary", Error: null))
            .ToArray());
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);
        await using var transaction = await operationContext.Database.BeginTransactionAsync();

        var outcome = await service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(operationContext),
            concurrency: 2,
            CancellationToken.None,
            skipReleaseDateProbe: true);
        await transaction.RollbackAsync();

        Assert.Equal(HealthCheckService.VerificationOutcome.Healthy, outcome);
        Assert.DoesNotContain(WebsocketTopic.HealthItemStatus, websocketManager.Topics);
        await using var assertionContext = new DavDatabaseContext(options);
        Assert.Empty(await assertionContext.HealthCheckResults.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task HealthyHealthStatusIsPublishedOnlyAfterResultIsDurable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new DavDatabaseContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var (targetId, segmentIds) = await SeedHealthyVerificationTargetAsync(dbContext, directoryTarget: false);
        var targetItem = await dbContext.Items.SingleAsync(x => x.Id == targetId);
        var websocketManager = new DurabilityObservingWebsocketManager(() =>
            dbContext.HealthCheckResults.AsNoTracking().Any(x => x.DavItemId == targetId));
        var configManager = new ConfigManager();
        var batch = SegmentCheckBatch.FromResults([
            new SegmentCheckResult(segmentIds[0], SegmentCheckState.Exists, Provider: "primary", Error: null)
        ]);
        using var usenetClient = new FixedSegmentCheckStreamingClient(configManager, websocketManager, batch);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        var outcome = await service.PerformHealthCheckAsync(
            targetItem,
            new DavDatabaseClient(dbContext),
            concurrency: 1,
            CancellationToken.None,
            skipReleaseDateProbe: true);

        Assert.Equal(HealthCheckService.VerificationOutcome.Healthy, outcome);
        Assert.Equal([true], websocketManager.DurabilityObservations);
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

        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(),
            usenetClient,
            new QueueWorkLaneCoordinator(),
            new WebsocketManager());
        var leasedJob = await InvokeLeaseNextRepairJobAsync(
            service, [activeTargetId], CancellationToken.None);

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

    [Fact]
    public async Task RejectedHealthWorkerCompletionIsReportedAsLostOwnership()
    {
        var coordinator = new RejectingWorkerJobCoordinator();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(), usenetClient, new QueueWorkLaneCoordinator(), new WebsocketManager(), coordinator);
        var lease = ToWorkerLease(CreateLeasedWorkerJob(WorkerJob.JobKind.Verify));

        var accepted = await InvokeTryCompleteWorkerJobAsync(service, lease);

        Assert.False(accepted);
        Assert.Equal(1, coordinator.CompleteCalls);
        Assert.Equal(0, coordinator.FailCalls);
    }

    [Fact]
    public async Task RejectedHealthWorkerFailureDoesNotInferRetryOrQuarantine()
    {
        var coordinator = new RejectingWorkerJobCoordinator();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(), usenetClient, new QueueWorkLaneCoordinator(), new WebsocketManager(), coordinator);
        var lease = ToWorkerLease(CreateLeasedWorkerJob(WorkerJob.JobKind.Verify));

        var status = await InvokeFailLeasedWorkerJobAsync(service, lease);

        Assert.Null(status);
        Assert.Equal(0, coordinator.CompleteCalls);
        Assert.Equal(1, coordinator.FailCalls);
    }

    [Fact]
    public async Task CompletedPostDownloadVerificationDoesNotMutateArrImportScheduling()
    {
        await DrainArrImportWakeSignalAsync();
        WorkerJob job;
        Guid importCommandId;
        Guid unrelatedCommandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            (job, importCommandId) = AddDeferredArrImport(setup, attempts: 1, name: "Example");
            (_, unrelatedCommandId) = AddDeferredArrImport(setup, attempts: 1, name: "Unrelated");
            await setup.SaveChangesAsync();
        }

        var coordinator = new AcceptingWorkerJobCoordinator();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(), usenetClient, new QueueWorkLaneCoordinator(), new WebsocketManager(), coordinator);

        Assert.True(await InvokeTryCompleteWorkerJobAsync(service, ToWorkerLease(job)));

        Assert.False(await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == importCommandId);
        Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, command.Status);
        Assert.True(command.NextAttemptAt > DateTimeOffset.UtcNow);
        var unrelated = await assertionContext.ArrImportCommands.AsNoTracking()
            .SingleAsync(x => x.Id == unrelatedCommandId);
        Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, unrelated.Status);
        Assert.True(unrelated.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task QuarantinedIndeterminatePostDownloadVerificationDoesNotMutateArrImportScheduling()
    {
        await DrainArrImportWakeSignalAsync();
        WorkerJob job;
        Guid importCommandId;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            (job, importCommandId) = AddDeferredArrImport(setup, attempts: 3);
            await setup.SaveChangesAsync();
        }

        var coordinator = new AcceptingWorkerJobCoordinator();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(new ConfigManager(), new WebsocketManager());
        var service = new HealthCheckService(
            new ConfigManager(), usenetClient, new QueueWorkLaneCoordinator(), new WebsocketManager(), coordinator);

        var status = await InvokeFailLeasedWorkerJobAsync(service, ToWorkerLease(job));

        Assert.Equal(WorkerJob.JobStatus.Quarantined, status);
        Assert.False(await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var command = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync(x => x.Id == importCommandId);
        Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, command.Status);
        Assert.True(command.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CompletedPostDownloadVerificationPublishesHistoryUsingDownloadFolderFallback()
    {
        WorkerJob job;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var historyId = Guid.NewGuid();
            var mountFolder = CreateDirectory(
                "/content/tv/Fallback",
                DavItem.ContentFolder.Id,
                historyId: null);
            setup.Items.Add(mountFolder);
            setup.HistoryItems.Add(new HistoryItem
            {
                Id = historyId,
                CreatedAt = DateTime.UtcNow,
                FileName = "Fallback.nzb",
                JobName = "Fallback",
                Category = "tv",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 1024,
                DownloadTimeSeconds = 1,
                DownloadDirId = mountFolder.Id
            });
            job = CreateLeasedWorkerJob(WorkerJob.JobKind.Verify);
            job.TargetId = mountFolder.Id;
            job.PayloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson();
            setup.WorkerJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        Assert.True(await InvokeTryCompleteWorkerJobAsync(service, ToWorkerLease(job)));

        Assert.True(WasHistoryAdditionBroadcast(websocketManager));
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            WorkerJob.JobStatus.Completed,
            (await assertionContext.WorkerJobs.SingleAsync(x => x.Id == job.Id)).Status);
    }

    [Fact]
    public async Task CompletedRepairPublishesHistoryAfterDeletingTargetFile()
    {
        WorkerJob workerJob;
        var historyId = Guid.NewGuid();
        var mountFolder = CreateDirectory(
            "/content/tv/DeletedTarget",
            DavItem.ContentFolder.Id,
            historyId);
        var target = CreateDavItem(
            "/content/tv/DeletedTarget/Movie.sample.mkv",
            parentId: mountFolder.Id,
            historyId: historyId);
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            setup.Items.AddRange(mountFolder, target);
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, mountFolder.Id, "DeletedTarget"));
            await setup.SaveChangesAsync();

            var dbClient = new DavDatabaseClient(setup);
            await dbClient.EnqueueWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                target.Id,
                priority: 0,
                now: DateTimeOffset.UtcNow);
            workerJob = Assert.IsType<WorkerJob>(await dbClient.LeaseNextWorkerJobAsync(
                WorkerJob.JobKind.Repair,
                owner: "repair-worker",
                leaseDuration: TimeSpan.FromMinutes(30)));
        }

        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager,
            usenetClient,
            new QueueWorkLaneCoordinator(),
            websocketManager);

        await InvokeRunRepairWorkerAsync(service, workerJob, CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Null(await assertionContext.Items.SingleOrDefaultAsync(x => x.Id == target.Id));
        Assert.Equal(
            WorkerJob.JobStatus.Completed,
            (await assertionContext.WorkerJobs.SingleAsync(x => x.Id == workerJob.Id)).Status);
        Assert.True(WasHistoryAdditionBroadcast(websocketManager));
    }

    [Fact]
    public async Task VisibilityNotifierPublishesEveryHistoryMappedToSharedDownloadFolder()
    {
        var primaryHistoryId = Guid.NewGuid();
        var duplicateHistoryIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        DavItem mountFolder;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            mountFolder = CreateDirectory(
                "/content/tv/Shared",
                DavItem.ContentFolder.Id,
                primaryHistoryId);
            setup.Items.Add(mountFolder);
            setup.HistoryItems.AddRange(
                new[] { primaryHistoryId }.Concat(duplicateHistoryIds).Select((historyId, index) =>
                    new HistoryItem
                    {
                        Id = historyId,
                        CreatedAt = DateTime.UtcNow.AddSeconds(index),
                        FileName = $"Shared-{index}.nzb",
                        JobName = $"Shared {index}",
                        Category = "tv",
                        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                        TotalSegmentBytes = 1024,
                        DownloadTimeSeconds = 1,
                        DownloadDirId = mountFolder.Id
                    }));
            await setup.SaveChangesAsync();
        }

        var websocketManager = new RecordingHistoryWebsocketManager();
        var notifier = new HistoryVisibilityNotifier(
            _fixture.CreateConfigManager(),
            websocketManager);

        Assert.True(await notifier.PublishForDavItemIfVisibleAsync(mountFolder.Id));

        Assert.Equal(3, websocketManager.HistoryAddMessages.Count);
        Assert.Equal(3, websocketManager.HistoryAddMessages.Distinct().Count());
    }

    [Fact]
    public async Task QuarantinedRepairPublishesHistoryAfterLastBlockerClears()
    {
        WorkerJob job;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var historyId = Guid.NewGuid();
            var target = CreateDavItem("/content/tv/Quarantined.mkv", historyId: historyId);
            setup.Items.Add(target);
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, target.Id, "Quarantined"));
            job = CreateLeasedWorkerJob(WorkerJob.JobKind.Repair);
            job.TargetId = target.Id;
            job.Attempts = 3;
            setup.WorkerJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager, usenetClient, new QueueWorkLaneCoordinator(), websocketManager);

        var status = await InvokeFailLeasedWorkerJobAsync(
            service,
            ToWorkerLease(job),
            WorkerJob.JobKind.Repair);

        Assert.Equal(WorkerJob.JobStatus.Quarantined, status);
        Assert.True(WasHistoryAdditionBroadcast(websocketManager));
    }

    [Fact]
    public async Task CancelledRepairPublishesHistoryAfterLastBlockerClears()
    {
        WorkerJob job;
        await using (var setup = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var historyId = Guid.NewGuid();
            var target = CreateDavItem("/content/tv/Cancelled.mkv", historyId: historyId);
            setup.Items.Add(target);
            setup.HistoryItems.Add(CreateCompletedHistory(historyId, target.Id, "Cancelled"));
            job = CreateLeasedWorkerJob(WorkerJob.JobKind.Repair);
            job.TargetId = target.Id;
            job.CancelRequestedAt = DateTimeOffset.UtcNow;
            setup.WorkerJobs.Add(job);
            await setup.SaveChangesAsync();
        }

        var configManager = _fixture.CreateConfigManager();
        var websocketManager = new WebsocketManager();
        using var usenetClient = new RecordingSegmentCheckStreamingClient(configManager, websocketManager);
        var service = new HealthCheckService(
            configManager, usenetClient, new QueueWorkLaneCoordinator(), websocketManager);

        await InvokeFinishWorkerCancellationAsync(
            service,
            ToWorkerLease(job),
            WorkerJob.JobKind.Repair,
            renewalRejected: true);

        Assert.True(WasHistoryAdditionBroadcast(websocketManager));
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(
            WorkerJob.JobStatus.Cancelled,
            (await assertionContext.WorkerJobs.SingleAsync(x => x.Id == job.Id)).Status);
    }

    private static (WorkerJob Job, Guid ImportCommandId) AddDeferredArrImport(
        DavDatabaseContext dbContext,
        int attempts,
        string name = "Example")
    {
        var historyId = Guid.NewGuid();
        var target = CreateDirectory($"/content/tv/{name}", DavItem.ContentFolder.Id, historyId);
        var now = DateTimeOffset.UtcNow;
        dbContext.Items.Add(target);
        dbContext.HistoryItems.Add(new HistoryItem
        {
            Id = historyId,
            CreatedAt = DateTime.UtcNow,
            FileName = $"{name}.nzb",
            JobName = name,
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1,
            DownloadDirId = target.Id
        });
        var command = new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyId,
            Category = "tv",
            RequiredInvalidationPathsJson = "[]",
            Status = ArrImportCommandStatus.WaitingForInvalidation,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now.AddMinutes(2)
        };
        dbContext.ArrImportCommands.Add(command);
        var job = CreateLeasedWorkerJob(WorkerJob.JobKind.Verify);
        job.TargetId = target.Id;
        job.PayloadJson = DavDatabaseClient.CreatePostDownloadVerifyPayloadJson();
        job.Attempts = attempts;
        return (job, command.Id);
    }

    private ConfigManager CreateAutomaticRepairEnabledConfig()
    {
        var configManager = _fixture.CreateConfigManager(_fixture.CreateLibraryDirectory());
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "repair.enable", ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = "arr.instances",
                ConfigValue = JsonSerializer.Serialize(new ArrConfig
                {
                    SonarrInstances =
                    [
                        new ArrConfig.ConnectionDetails
                        {
                            Host = "http://sonarr.test",
                            ApiKey = "test-key"
                        }
                    ]
                })
            }
        ]);
        Assert.True(configManager.IsRepairJobEnabled());
        return configManager;
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

    private static async Task<(Guid TargetId, string[] SegmentIds)> SeedHealthyVerificationTargetAsync(
        DavDatabaseContext dbContext,
        bool directoryTarget)
    {
        var prefix = Guid.NewGuid().ToString("N");
        if (!directoryTarget)
        {
            var file = CreateDavItem($"/content/{prefix}/Movie.mkv");
            var segmentId = $"{prefix}-segment";
            dbContext.Items.Add(file);
            dbContext.NzbFiles.Add(new DavNzbFile { Id = file.Id, SegmentIds = [segmentId] });
            await dbContext.SaveChangesAsync();
            return (file.Id, [segmentId]);
        }

        var directory = CreateDirectory($"/content/{prefix}", DavItem.ContentFolder.Id);
        var first = CreateDavItem($"/content/{prefix}/First.mkv", parentId: directory.Id);
        var second = CreateDavItem($"/content/{prefix}/Second.mkv", parentId: directory.Id);
        var segmentIds = new[] { $"{prefix}-first", $"{prefix}-second" };
        dbContext.Items.AddRange(directory, first, second);
        dbContext.NzbFiles.AddRange(
            new DavNzbFile { Id = first.Id, SegmentIds = [segmentIds[0]] },
            new DavNzbFile { Id = second.Id, SegmentIds = [segmentIds[1]] });
        await dbContext.SaveChangesAsync();
        return (directory.Id, segmentIds);
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
            [ToWorkerLease(workerJob), 1, new NoopDisposable(), ct])!;
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
        var task = (Task)method.Invoke(service, [ToWorkerLease(workerJob), ct])!;
        await task.ConfigureAwait(false);
    }

    private static async Task<WorkerLease?> InvokeLeaseNextRepairJobAsync(
        HealthCheckService service,
        IReadOnlyCollection<Guid> activeHealthCheckIds,
        CancellationToken ct)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "LeaseNextRepairJobAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<WorkerLease?>)method.Invoke(service, [activeHealthCheckIds, ct])!;
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
        var task = (Task<bool>)method.Invoke(service, [ToWorkerLease(workerJob), ct])!;
        return await task.ConfigureAwait(false);
    }

    private static WorkerLease ToWorkerLease(WorkerJob job)
    {
        return new WorkerLease(
            new WorkerLeaseIdentity(
                job.Id,
                job.LeaseOwner!,
                job.LeaseToken!.Value,
                job.LeaseGeneration),
            job.Kind,
            job.TargetId,
            job.Priority,
            job.Attempts,
            job.PayloadJson,
            job.LeaseExpiresAt!.Value,
            job.CancelRequestedAt != null);
    }

    private static async Task InvokeReleaseArrLeaseAsync(
        ArrImportCommand command,
        ArrImportCommandStatus status)
    {
        var method = typeof(ArrImportCommandService).GetMethod(
            "ReleaseLeaseAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method.Invoke(null,
        [
            command,
            status,
            DateTimeOffset.UtcNow,
            null,
            command.ResultsJson,
            DateTimeOffset.UtcNow,
            true,
            CancellationToken.None
        ])!;
    }

    private static async Task<bool> InvokeTryCompleteWorkerJobAsync(
        HealthCheckService service,
        WorkerLease lease)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "TryCompleteWorkerJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<bool>)method.Invoke(service, [lease, CancellationToken.None])!;
    }

    private static async Task<WorkerJob.JobStatus?> InvokeFailLeasedWorkerJobAsync(
        HealthCheckService service,
        WorkerLease lease,
        WorkerJob.JobKind kind = WorkerJob.JobKind.Verify)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "FailLeasedWorkerJobAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<WorkerJob.JobStatus?>)method.Invoke(service,
            [lease, new InvalidOperationException("failure"), kind, CancellationToken.None])!;
    }

    private static async Task InvokeFinishWorkerCancellationAsync(
        HealthCheckService service,
        WorkerLease lease,
        WorkerJob.JobKind kind,
        bool renewalRejected)
    {
        var method = typeof(HealthCheckService).GetMethod(
            "FinishWorkerCancellationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method.Invoke(service, [lease, kind, renewalRejected])!;
    }

    private static HistoryItem CreateCompletedHistory(Guid historyId, Guid downloadDirId, string name) => new()
    {
        Id = historyId,
        CreatedAt = DateTime.UtcNow,
        FileName = $"{name}.nzb",
        JobName = name,
        Category = "tv",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        TotalSegmentBytes = 1024,
        DownloadTimeSeconds = 1,
        DownloadDirId = downloadDirId
    };

    private static ArrImportCommand CreateArrImportCommand(
        Guid historyItemId,
        ArrImportCommandStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyItemId,
            Category = "tv",
            RequiredInvalidationPathsJson = "[]",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            ResultsJson = "[]"
        };
    }

    private static WorkerJob CreateLeasedWorkerJob(WorkerJob.JobKind kind)
    {
        return new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            TargetId = Guid.NewGuid(),
            Status = WorkerJob.JobStatus.Leased,
            LeaseOwner = "worker",
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            Attempts = 1
        };
    }

    private static async Task DrainArrImportWakeSignalAsync()
    {
        while (await ArrImportCommandWakeSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
        }
    }

    private static bool WasHistoryAdditionBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemAdded);
    }

    private static bool WasHistoryRemovalBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemRemoved);
    }

    private sealed class AcceptingWorkerJobCoordinator : IWorkerJobCoordinator
    {
        public int FailCalls { get; private set; }
        public CancellationToken FailureCancellationToken { get; private set; }

        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind, string owner, int capacity, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkerLease>>([]);
        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease, string progressJson, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease, string? resultJson, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<bool> FailAsync(
            WorkerLeaseIdentity lease, WorkerJob.FailureClass failureKind, string error,
            DateTimeOffset nextAttemptAt, int maxAttempts, DateTimeOffset now, CancellationToken ct)
        {
            FailCalls++;
            FailureCancellationToken = ct;
            return Task.FromResult(true);
        }
        public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(true);
    }

    private sealed class RejectingWorkerJobCoordinator : IWorkerJobCoordinator
    {
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }

        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind, string owner, int capacity, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkerLease>>([]);
        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease, string progressJson, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease, string? resultJson, DateTimeOffset now, CancellationToken ct)
        {
            CompleteCalls++;
            return Task.FromResult(false);
        }
        public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<bool> FailAsync(
            WorkerLeaseIdentity lease, WorkerJob.FailureClass failureKind, string error,
            DateTimeOffset nextAttemptAt, int maxAttempts, DateTimeOffset now, CancellationToken ct)
        {
            FailCalls++;
            return Task.FromResult(false);
        }
        public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class BlockingReleaseWorkerJobCoordinator(IWorkerJobCoordinator inner)
        : IWorkerJobCoordinator
    {
        private readonly TaskCompletionSource _allowRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReleaseCancellationToken { get; private set; }

        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind,
            string owner,
            int capacity,
            DateTimeOffset now,
            CancellationToken ct) => inner.LeaseAsync(kind, owner, capacity, now, ct);

        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            inner.RenewAsync(lease, now, ct);

        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease,
            string progressJson,
            DateTimeOffset now,
            CancellationToken ct) => inner.ReportProgressAsync(lease, progressJson, now, ct);

        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease,
            string? resultJson,
            DateTimeOffset now,
            CancellationToken ct) => inner.CompleteAsync(lease, resultJson, now, ct);

        public async Task<bool> ReleaseAsync(
            WorkerLeaseIdentity lease,
            DateTimeOffset now,
            CancellationToken ct)
        {
            ReleaseCancellationToken = ct;
            ReleaseStarted.TrySetResult();
            await _allowRelease.Task.ConfigureAwait(false);
            return await inner.ReleaseAsync(lease, now, ct).ConfigureAwait(false);
        }

        public Task<bool> FailAsync(
            WorkerLeaseIdentity lease,
            WorkerJob.FailureClass failureKind,
            string error,
            DateTimeOffset nextAttemptAt,
            int maxAttempts,
            DateTimeOffset now,
            CancellationToken ct) => inner.FailAsync(
            lease,
            failureKind,
            error,
            nextAttemptAt,
            maxAttempts,
            now,
            ct);

        public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
            inner.RequestCancellationAsync(jobId, now, ct);

        public void AllowRelease()
        {
            _allowRelease.TrySetResult();
        }
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

    private sealed class InspectingWorkerJobCoordinator(
        IWorkerJobCoordinator inner,
        Func<CancellationToken, Task> beforeFail) : IWorkerJobCoordinator
    {
        public Task<IReadOnlyList<WorkerLease>> LeaseAsync(
            WorkerJob.JobKind kind,
            string owner,
            int capacity,
            DateTimeOffset now,
            CancellationToken ct) => inner.LeaseAsync(kind, owner, capacity, now, ct);

        public Task<bool> RenewAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            inner.RenewAsync(lease, now, ct);

        public Task<bool> ReportProgressAsync(
            WorkerLeaseIdentity lease,
            string progressJson,
            DateTimeOffset now,
            CancellationToken ct) => inner.ReportProgressAsync(lease, progressJson, now, ct);

        public Task<bool> CompleteAsync(
            WorkerLeaseIdentity lease,
            string? resultJson,
            DateTimeOffset now,
            CancellationToken ct) => inner.CompleteAsync(lease, resultJson, now, ct);

        public Task<bool> ReleaseAsync(WorkerLeaseIdentity lease, DateTimeOffset now, CancellationToken ct) =>
            inner.ReleaseAsync(lease, now, ct);

        public async Task<bool> FailAsync(
            WorkerLeaseIdentity lease,
            WorkerJob.FailureClass failureKind,
            string error,
            DateTimeOffset nextAttemptAt,
            int maxAttempts,
            DateTimeOffset now,
            CancellationToken ct)
        {
            await beforeFail(ct);
            return await inner.FailAsync(
                lease,
                failureKind,
                error,
                nextAttemptAt,
                maxAttempts,
                now,
                ct);
        }

        public Task<bool> RequestCancellationAsync(Guid jobId, DateTimeOffset now, CancellationToken ct) =>
            inner.RequestCancellationAsync(jobId, now, ct);
    }

    private sealed class RecordingHistoryWebsocketManager : WebsocketManager
    {
        public List<string> HistoryAddMessages { get; } = [];

        public override Task SendMessage(WebsocketTopic topic, string message)
        {
            if (ReferenceEquals(topic, WebsocketTopic.HistoryItemAdded))
                HistoryAddMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTopicWebsocketManager : WebsocketManager
    {
        public List<WebsocketTopic> Topics { get; } = [];

        public override Task SendMessage(WebsocketTopic topic, string message)
        {
            Topics.Add(topic);
            return Task.CompletedTask;
        }
    }

    private sealed class DurabilityObservingWebsocketManager(Func<bool> isDurable) : WebsocketManager
    {
        public List<bool> DurabilityObservations { get; } = [];

        public override Task SendMessage(WebsocketTopic topic, string message)
        {
            if (ReferenceEquals(topic, WebsocketTopic.HealthItemStatus))
                DurabilityObservations.Add(isDurable());
            return Task.CompletedTask;
        }
    }

    private sealed class CommitObservingWebsocketManager(Func<bool> wasCommitted) : WebsocketManager
    {
        public List<bool> CommitObservations { get; } = [];

        public override Task SendMessage(WebsocketTopic topic, string message)
        {
            if (ReferenceEquals(topic, WebsocketTopic.HealthItemStatus))
                CommitObservations.Add(wasCommitted());
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionCommitProbe : DbTransactionInterceptor
    {
        private int _wasCommitted;

        public bool WasCommitted => Volatile.Read(ref _wasCommitted) == 1;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _wasCommitted, 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("forced quarantine save failure"));
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

    private sealed class NonQueryCountingCommandInterceptor(Func<string, bool> predicate) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset()
        {
            Volatile.Write(ref _count, 0);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            CountIfMatched(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CountIfMatched(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
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

    private sealed class CancellingFailureSegmentCheckStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        Action cancelHost)
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
            cancelHost();
            throw new InvalidOperationException("Verification failed after host cancellation.");
        }
    }
}

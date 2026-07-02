using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
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
        var service = new HealthCheckService(new ConfigManager(), usenetClient, new WebsocketManager());

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
        var service = new HealthCheckService(new ConfigManager(), usenetClient, new WebsocketManager());

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
        var service = new HealthCheckService(new ConfigManager(), usenetClient, new WebsocketManager());

        await service.PerformHealthCheckAsync(davItem, dbClient, concurrency: 1, CancellationToken.None);

        var result = await dbContext.HealthCheckResults.SingleAsync();
        var repairJob = await dbContext.WorkerJobs.SingleAsync();
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, result.Result);
        Assert.Equal(HealthCheckResult.RepairAction.ActionNeeded, result.RepairStatus);
        Assert.Equal(WorkerJob.JobKind.Repair, repairJob.Kind);
        Assert.Equal(WorkerJob.JobStatus.Pending, repairJob.Status);
        Assert.Equal(davItem.Id, repairJob.TargetId);
    }

    private static DavItem CreateDavItem(string path)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = Path.GetFileName(path),
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = path
        };
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
}

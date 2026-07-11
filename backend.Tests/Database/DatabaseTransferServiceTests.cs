using System.Text.Json;
using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DatabaseTransferServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DatabaseTransferServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExportImportJson_RoundTripsApplicationRows()
    {
        var leaseToken = Guid.NewGuid();
        var leaseTimestamp = DateTimeOffset.UtcNow;
        await using (var source = await _fixture.ResetAndCreateMigratedContextAsync())
        {
            var queueItem = new QueueItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "Example.nzb",
                JobName = "Example",
                Category = "tv",
                NzbFileSize = 100,
                TotalSegmentBytes = 200,
                Priority = QueueItem.PriorityOption.High,
                PostProcessing = QueueItem.PostProcessingOption.None
            };
            source.QueueItems.Add(queueItem);
            source.QueuePriorityHints.Add(new QueuePriorityHint
            {
                QueueItemId = queueItem.Id,
                Score = 500,
                EffectivePriority = QueueItem.PriorityOption.High,
                ApplyToScheduling = true,
                ReasonsJson = """["test"]""",
                Source = "test",
                ComputedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            source.ArrSearchNudgeCommands.Add(new ArrSearchNudgeCommand
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = "sonarr:test",
                InstanceHost = "http://sonarr:8989",
                CommandName = "EpisodeSearch",
                TargetsJson = "[123]",
                Mode = "report",
                Status = "planned",
                CooldownKey = "sonarr:test:123",
                ReasonsJson = """["recently-aired"]""",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAllowedAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            source.ArrDownloadCorrelations.Add(new ArrDownloadCorrelation
            {
                Id = Guid.NewGuid(),
                QueueItemId = queueItem.Id,
                ArrApp = "sonarr",
                InstanceKey = "sonarr:test",
                InstanceHost = "http://sonarr:8989",
                DownloadId = "download-1",
                MediaKey = "sonarr:episode:123",
                EpisodeId = 123,
                Source = "manual",
                ManualLock = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            source.WorkerJobs.Add(new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = WorkerJob.JobKind.Verify,
                Status = WorkerJob.JobStatus.Leased,
                TargetId = Guid.NewGuid(),
                Priority = 10,
                Attempts = 2,
                CreatedAt = leaseTimestamp,
                UpdatedAt = leaseTimestamp,
                AvailableAt = leaseTimestamp,
                LeaseExpiresAt = leaseTimestamp.AddMinutes(5),
                LeaseOwner = "transfer-test-worker",
                LeaseToken = leaseToken,
                LeaseGeneration = 2,
                LastHeartbeatAt = leaseTimestamp,
                StartedAt = leaseTimestamp.AddMinutes(-1),
                CancelRequestedAt = leaseTimestamp,
                FailureKind = WorkerJob.FailureClass.Provider,
                ProgressJson = "{\"completed\":42}",
                ProgressUpdatedAt = leaseTimestamp,
                ResultJson = "{\"result\":\"pending\"}",
                PayloadJson = "{\"source\":\"test\"}"
            });
            await source.SaveChangesAsync();
        }

        var exportPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        await using (var source = await _fixture.CreateMigratedContextAsync())
        {
            await DatabaseTransferService.ExportJsonAsync(source, exportPath);
        }

        await _fixture.ResetAsync();
        await using (var target = await _fixture.CreateMigratedContextAsync())
        {
            var result = await DatabaseTransferService.ImportJsonAsync(target, exportPath, replace: true);
            Assert.True(result.ImportedRows > 0);
        }

        await using var imported = await _fixture.CreateMigratedContextAsync();
        Assert.Equal("Example.nzb", (await imported.QueueItems.SingleAsync()).FileName);
        Assert.Equal(500, (await imported.QueuePriorityHints.SingleAsync()).Score);
        Assert.Equal("EpisodeSearch", (await imported.ArrSearchNudgeCommands.SingleAsync()).CommandName);
        var correlation = await imported.ArrDownloadCorrelations.SingleAsync();
        Assert.Equal("manual", correlation.Source);
        Assert.True(correlation.ManualLock);
        var workerJob = await imported.WorkerJobs.SingleAsync();
        Assert.Equal(leaseToken, workerJob.LeaseToken);
        Assert.Equal(2, workerJob.LeaseGeneration);
        Assert.Equal(leaseTimestamp, workerJob.LastHeartbeatAt);
        Assert.Equal(leaseTimestamp.AddMinutes(-1), workerJob.StartedAt);
        Assert.Equal(leaseTimestamp, workerJob.CancelRequestedAt);
        Assert.Equal(WorkerJob.FailureClass.Provider, workerJob.FailureKind);
        Assert.Equal("{\"completed\":42}", workerJob.ProgressJson);
        Assert.Equal(leaseTimestamp, workerJob.ProgressUpdatedAt);
        Assert.Equal("{\"result\":\"pending\"}", workerJob.ResultJson);
    }

    [Fact]
    public async Task ImportJsonAsync_AcceptsVersion1WorkerJobsWithoutRenewableLeaseState()
    {
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        var snapshotPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-v1-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        var snapshot = JsonSerializer.Serialize(new
        {
            Version = 1,
            ExportedAt = now,
            Provider = "sqlite",
            WorkerJobs = new[]
            {
                new
                {
                    Id = jobId,
                    Kind = (int)WorkerJob.JobKind.Download,
                    Status = (int)WorkerJob.JobStatus.Pending,
                    TargetId = Guid.NewGuid(),
                    Priority = 1,
                    Attempts = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AvailableAt = now,
                    LeaseExpiresAt = (DateTimeOffset?)null,
                    CompletedAt = (DateTimeOffset?)null,
                    LeaseOwner = (string?)null,
                    LastError = (string?)null,
                    PayloadJson = "{\"legacy\":true}"
                }
            }
        });
        await File.WriteAllTextAsync(snapshotPath, snapshot);

        await using var target = await _fixture.ResetAndCreateMigratedContextAsync();
        var result = await DatabaseTransferService.ImportJsonAsync(target, snapshotPath, replace: true);

        Assert.Equal(2, DatabaseTransferSnapshot.CurrentVersion);
        Assert.Equal(1, result.ImportedRows);
        var workerJob = await target.WorkerJobs.SingleAsync(x => x.Id == jobId);
        Assert.Null(workerJob.LeaseToken);
        Assert.Equal(0, workerJob.LeaseGeneration);
        Assert.Null(workerJob.LastHeartbeatAt);
        Assert.Null(workerJob.StartedAt);
        Assert.Null(workerJob.CancelRequestedAt);
        Assert.Null(workerJob.FailureKind);
        Assert.Null(workerJob.ProgressJson);
        Assert.Null(workerJob.ProgressUpdatedAt);
        Assert.Null(workerJob.ResultJson);
    }
}

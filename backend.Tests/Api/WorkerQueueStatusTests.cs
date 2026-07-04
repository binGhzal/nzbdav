using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Api;

public sealed class WorkerQueueStatusTests
{
    [Fact]
    public void FromStatsReportsIndependentMaximaAndSaturatedStates()
    {
        var now = DateTimeOffset.UtcNow;
        var stats = new DavDatabaseClient.WorkerJobQueueStats(
            Download: BuildStats(WorkerJob.JobKind.Download, pending: 2, retry: 0, leased: 1, quarantined: 0),
            Verify: BuildStats(WorkerJob.JobKind.Verify, pending: 3, retry: 1, leased: 2, quarantined: 0),
            Repair: BuildStats(WorkerJob.JobKind.Repair, pending: 1, retry: 0, leased: 1, quarantined: 0));

        var status = WorkerQueueStatus.FromStats(
            downloadActive: 1,
            downloadWaiting: 2,
            maxDownloadWorkers: 4,
            maxVerifyWorkers: 2,
            maxRepairWorkers: 1,
            downloadsPaused: false,
            healthWorkers: new HealthCheckService.WorkerSnapshot(VerifyActive: 2, RepairActive: 1),
            healthQueue: new DavDatabaseClient.HealthWorkerQueueStats(VerifyReady: 3, RepairActionNeeded: 1),
            durableJobs: stats);

        Assert.Equal(4, status.DownloadMax);
        Assert.Equal("active", status.DownloadState);
        Assert.Equal(2, status.VerifyMax);
        Assert.Equal("saturated", status.VerifyState);
        Assert.Equal(1, status.RepairMax);
        Assert.Equal("saturated", status.RepairState);
    }

    [Fact]
    public void FromStatsReportsPausedDownloadLaneWithoutPausingVerifyAndRepair()
    {
        var stats = new DavDatabaseClient.WorkerJobQueueStats(
            Download: BuildStats(WorkerJob.JobKind.Download, pending: 1, retry: 0, leased: 0, quarantined: 0),
            Verify: BuildStats(WorkerJob.JobKind.Verify, pending: 1, retry: 0, leased: 0, quarantined: 0),
            Repair: BuildStats(WorkerJob.JobKind.Repair, pending: 0, retry: 1, leased: 0, quarantined: 0));

        var status = WorkerQueueStatus.FromStats(
            downloadActive: 0,
            downloadWaiting: 1,
            maxDownloadWorkers: 2,
            maxVerifyWorkers: 2,
            maxRepairWorkers: 2,
            downloadsPaused: true,
            healthWorkers: new HealthCheckService.WorkerSnapshot(VerifyActive: 0, RepairActive: 0),
            healthQueue: new DavDatabaseClient.HealthWorkerQueueStats(VerifyReady: 1, RepairActionNeeded: 0),
            durableJobs: stats);

        Assert.Equal("paused", status.DownloadState);
        Assert.Equal("ready", status.VerifyState);
        Assert.Equal("retrying", status.RepairState);
    }

    private static DavDatabaseClient.WorkerJobKindStats BuildStats
    (
        WorkerJob.JobKind kind,
        int pending,
        int retry,
        int leased,
        int quarantined
    )
    {
        var rows = new List<DavDatabaseClient.WorkerJobStatusCount>
        {
            new(kind, WorkerJob.JobStatus.Pending, pending),
            new(kind, WorkerJob.JobStatus.Retry, retry),
            new(kind, WorkerJob.JobStatus.Leased, leased),
            new(kind, WorkerJob.JobStatus.Quarantined, quarantined)
        };
        var readyRows = new List<DavDatabaseClient.WorkerJobReadyCount>
        {
            new(kind, pending + retry)
        };
        return DavDatabaseClient.WorkerJobKindStats.FromRows(kind, rows, readyRows);
    }
}

using System.Reflection;
using System.Text.Json;
using backend.Tests.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;

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
        var receiptId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var importCommandId = Guid.NewGuid();
        var maintenanceRunId = Guid.NewGuid();
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
            source.HistoryItems.Add(new HistoryItem
            {
                Id = historyId,
                CreatedAt = DateTime.Now,
                FileName = "Imported.nzb",
                JobName = "Imported",
                Category = "tv",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                TotalSegmentBytes = 200,
                DownloadTimeSeconds = 1
            });
            source.ImportReceipts.Add(new ImportReceipt
            {
                Id = receiptId,
                DavItemId = Guid.NewGuid(),
                HistoryItemId = historyId,
                State = ImportReceiptState.NeedsReview,
                CreatedAt = leaseTimestamp,
                UpdatedAt = leaseTimestamp,
                Detail = "transfer test"
            });
            source.ArrImportCommands.Add(new ArrImportCommand
            {
                Id = importCommandId,
                HistoryItemId = historyId,
                Category = "tv",
                RequiredInvalidationPathsJson = "[\"/content/tv/Imported\"]",
                Status = ArrImportCommandStatus.WaitingForInvalidation,
                CreatedAt = leaseTimestamp,
                UpdatedAt = leaseTimestamp,
                NextAttemptAt = leaseTimestamp,
                ResultsJson = "[]"
            });
            source.MaintenanceRuns.Add(new MaintenanceRun
            {
                Id = maintenanceRunId,
                Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
                Status = MaintenanceRunStatus.Completed,
                RequestedBy = "test",
                CreatedAt = leaseTimestamp,
                UpdatedAt = leaseTimestamp,
                CompletedAt = leaseTimestamp,
                ProgressCurrent = 1,
                ProgressTotal = 1,
                Message = "Done."
            });
            await source.SaveChangesAsync();
        }

        var exportPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        await using (var source = await _fixture.CreateMigratedContextAsync())
        {
            await DatabaseTransferService.ExportJsonAsync(source, exportPath);
            await source.ImportReceipts.Where(x => x.Id == receiptId).ExecuteDeleteAsync();
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
        var receipt = await imported.ImportReceipts.SingleAsync(x => x.Id == receiptId);
        Assert.Equal(ImportReceiptState.NeedsReview, receipt.State);
        var importCommand = await imported.ArrImportCommands.SingleAsync(x => x.Id == importCommandId);
        Assert.Equal(ArrImportCommandStatus.WaitingForInvalidation, importCommand.Status);
        Assert.Equal("tv", importCommand.Category);
        var maintenanceRun = await imported.MaintenanceRuns.SingleAsync(x => x.Id == maintenanceRunId);
        Assert.Equal(MaintenanceRunStatus.Completed, maintenanceRun.Status);
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

    [Fact]
    public async Task ExportJsonAsync_OmitsReservedImportStateAndAdjustsTotalRows()
    {
        var exportPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        await using var source = await _fixture.ResetAndCreateMigratedContextAsync();
        await DeleteRawConfigAsync(source, TransferV3ReservedConfigPolicy.ImportStateKey, OrdinaryConfigKey);
        await InsertRawConfigAsync(
            source,
            TransferV3ReservedConfigPolicy.ImportStateKey,
            TransferV3ImportStateCodec.FreshCanonicalJson);
        await InsertRawConfigAsync(source, OrdinaryConfigKey, "ordinary");
        var expectedConfigCount = await source.ConfigItems.CountAsync() - 1;

        try
        {
            await DatabaseTransferService.ExportJsonAsync(source, exportPath);

            var snapshot = JsonSerializer.Deserialize<DatabaseTransferSnapshot>(
                await File.ReadAllTextAsync(exportPath));
            Assert.NotNull(snapshot);
            Assert.Equal(expectedConfigCount, snapshot.ConfigItems.Count);
            Assert.DoesNotContain(
                snapshot.ConfigItems,
                item => item.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey);
            Assert.Equal(
                snapshot.Accounts.Count + snapshot.Items.Count + snapshot.NzbFiles.Count
                + snapshot.RarFiles.Count + snapshot.MultipartFiles.Count + snapshot.QueueItems.Count
                + snapshot.HistoryItems.Count + snapshot.QueueNzbContents.Count
                + snapshot.HealthCheckResults.Count + snapshot.HealthCheckStats.Count
                + expectedConfigCount + snapshot.BlobCleanupItems.Count + snapshot.HistoryCleanupItems.Count
                + snapshot.DavCleanupItems.Count + snapshot.NzbNames.Count
                + snapshot.NzbBlobCleanupItems.Count + snapshot.RcloneInvalidationItems.Count
                + snapshot.WorkerJobs.Count + snapshot.RepairRuns.Count + snapshot.RepairEntryHealth.Count
                + snapshot.RepairBrokenFiles.Count + snapshot.ArrDownloadCorrelations.Count
                + snapshot.QueuePriorityHints.Count + snapshot.ArrSearchNudgeCommands.Count
                + snapshot.ArrDownloadLifecycleEvents.Count + snapshot.ImportReceipts.Count
                + snapshot.ArrImportCommands.Count + snapshot.MaintenanceRuns.Count,
                snapshot.TotalRows);
        }
        finally
        {
            await DeleteRawConfigAsync(source, TransferV3ReservedConfigPolicy.ImportStateKey, OrdinaryConfigKey);
            File.Delete(exportPath);
        }
    }

    [Fact]
    public async Task ImportJsonAsync_RejectsReservedStateBeforeMigrationTargetOrMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "input.json");
        var targetPath = Path.Combine(directory, "must-not-be-created.sqlite");
        var snapshot = new DatabaseTransferSnapshot
        {
            ConfigItems =
            [
                new ConfigItem
                {
                    ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
                    ConfigValue = TransferV3ImportStateCodec.FreshCanonicalJson
                }
            ]
        };
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(snapshot));
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={targetPath}")
            .Options;
        await using var target = new DavDatabaseContext(options);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                DatabaseTransferService.ImportJsonAsync(target, inputPath, replace: true));

            Assert.Equal(TransferV3ReservedConfigPolicy.LegacySnapshotMessage, error.Message);
            Assert.False(File.Exists(targetPath));
            Assert.Empty(target.ChangeTracker.Entries());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportJsonAsync_ReplacePreservesTargetLocalImportState()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        var restorePath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"transfer-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        var snapshot = new DatabaseTransferSnapshot
        {
            ConfigItems = [new ConfigItem { ConfigName = OrdinaryConfigKey, ConfigValue = "source" }]
        };
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(snapshot));
        await using var target = await _fixture.ResetAndCreateMigratedContextAsync();
        await DeleteRawConfigAsync(target, TransferV3ReservedConfigPolicy.ImportStateKey, OrdinaryConfigKey);
        await DatabaseTransferService.ExportJsonAsync(target, restorePath);
        await InsertRawConfigAsync(
            target,
            TransferV3ReservedConfigPolicy.ImportStateKey,
            TransferV3ImportStateCodec.FreshCanonicalJson);
        await InsertRawConfigAsync(target, OrdinaryConfigKey, "target");

        try
        {
            await DatabaseTransferService.ImportJsonAsync(target, inputPath, replace: true);

            target.ChangeTracker.Clear();
            Assert.Equal(
                TransferV3ImportStateCodec.FreshCanonicalJson,
                (await target.ConfigItems.SingleAsync(x =>
                    x.ConfigName == TransferV3ReservedConfigPolicy.ImportStateKey)).ConfigValue);
            Assert.Equal(
                "source",
                (await target.ConfigItems.SingleAsync(x => x.ConfigName == OrdinaryConfigKey)).ConfigValue);
        }
        finally
        {
            await DeleteRawConfigAsync(target, TransferV3ReservedConfigPolicy.ImportStateKey, OrdinaryConfigKey);
            target.ChangeTracker.Clear();
            await DatabaseTransferService.ImportJsonAsync(target, restorePath, replace: true);
            File.Delete(inputPath);
            File.Delete(restorePath);
        }
    }

    [Fact]
    public async Task TargetEmptiness_IgnoresOnlyTheExactReservedKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DavDatabaseContext(options);
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            PRAGMA foreign_keys=OFF;
            DELETE FROM DavItems;
            DELETE FROM QueueItems;
            DELETE FROM HistoryItems;
            DELETE FROM ImportReceipts;
            DELETE FROM ArrImportCommands;
            DELETE FROM MaintenanceRuns;
            DELETE FROM ConfigItems;
            DELETE FROM Accounts;
            """);
        await InsertRawConfigAsync(
            context,
            TransferV3ReservedConfigPolicy.ImportStateKey,
            TransferV3ImportStateCodec.FreshCanonicalJson);

        Assert.False(await InvokeHasApplicationRowsAsync(context));

        await DeleteRawConfigAsync(context, TransferV3ReservedConfigPolicy.ImportStateKey);
        await InsertRawConfigAsync(context, "Database.import-state", "ordinary");
        Assert.True(await InvokeHasApplicationRowsAsync(context));
    }

    private static async Task<bool> InvokeHasApplicationRowsAsync(DavDatabaseContext context)
    {
        var method = typeof(DatabaseTransferService).GetMethod(
            "HasApplicationRowsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsType<Task<bool>>(method.Invoke(null, [context, CancellationToken.None]));
        return await task;
    }

    private static Task<int> InsertRawConfigAsync(DavDatabaseContext context, string name, string value) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ({name}, {value})");

    private static Task<int> DeleteRawConfigAsync(DavDatabaseContext context, params string[] names) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ConfigItems WHERE ConfigName IN ({names[0]}, {names.ElementAtOrDefault(1)})");

    private const string OrdinaryConfigKey = "transfer-v3-v2-probe";
}

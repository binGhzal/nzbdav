using backend.Tests.Services;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.Maintenance;
using NzbWebDAV.Api.Controllers.Repair;
using NzbWebDAV.Api.Controllers.GetHealthCheckHistory;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Mount;
using NzbWebDAV.Security;
using NzbWebDAV.Services;

namespace backend.Tests.Security;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class PublicDiagnosticProjectionTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public PublicDiagnosticProjectionTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void LegacyFailureModelsProjectOnlyFixedDiagnostics()
    {
        var raw = PublicFailureCanary.Composite;
        var history = GetHistoryResponse.HistorySlot.FromHistoryItem(
            new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "fixture.nzb",
                JobName = "fixture",
                Category = "fixture",
                DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
                FailMessage = raw,
            },
            null,
            new ConfigManager());
        var maintenance = MaintenanceRunDto.FromModel(new MaintenanceRun
        {
            Id = Guid.NewGuid(),
            Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
            Status = MaintenanceRunStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = raw,
            Error = raw,
        });
        var repair = RepairRunDto.FromModel(new RepairRun
        {
            Id = Guid.NewGuid(),
            Status = RepairRun.RepairRunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = raw,
        });
        var broken = RepairBrokenFileDto.FromModel(new RepairBrokenFile
        {
            Id = Guid.NewGuid(),
            RepairRunId = Guid.NewGuid(),
            DavItemId = Guid.NewGuid(),
            Path = "/safe-compatibility-path",
            Reason = raw,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var health = GetHealthCheckHistoryResponse.Project(new HealthCheckResult
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            DavItemId = Guid.NewGuid(),
            Path = "/safe-compatibility-path",
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
            Message = raw,
        });

        PublicFailureCanary.AssertSafe(history.FailMessage);
        PublicFailureCanary.AssertSafe(maintenance.Message);
        PublicFailureCanary.AssertSafe(maintenance.Error);
        PublicFailureCanary.AssertSafe(repair.Message);
        PublicFailureCanary.AssertSafe(broken.Reason);
        PublicFailureCanary.AssertSafe(health.Message);
        Assert.True(history.FailMessage == PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure));
        Assert.True(maintenance.Error == PublicDiagnosticContract.Message(PublicDiagnosticKind.MaintenanceFailure));
        Assert.True(repair.Message == PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure));
        Assert.True(broken.Reason == PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure));
        Assert.True(health.Message == PublicDiagnosticContract.HealthRepairActionRequired);
    }

    [Fact]
    public void LegacyHistoryPreservesOnlyTheClosedPostDownloadVerificationFailure()
    {
        const string verificationFailure = PublicDiagnosticContract.PostDownloadVerificationFailure;
        var history = GetHistoryResponse.HistorySlot.FromHistoryItem(
            new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "fixture.nzb",
                JobName = "fixture",
                Category = "fixture",
                DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
                FailMessage = verificationFailure,
            },
            null,
            new ConfigManager());

        Assert.Equal(verificationFailure, history.FailMessage);
        PublicFailureCanary.AssertSafe(history.FailMessage);
    }

    [Fact]
    public void StatusDiagnosticsProjectOnlyFixedFailureCategories()
    {
        var raw = PublicFailureCanary.Composite;
        var now = DateTimeOffset.UtcNow;
        var rclone = RcloneInvalidationStatus.FromSnapshots(
            new NzbWebDAV.Database.DavDatabaseClient.RcloneInvalidationStats(
                Pending: 1,
                Ready: 1,
                Failed: 1,
                WholeCacheVisibilityFencePending: false,
                MaxAttempts: 1,
                LastError: raw,
                OldestPendingAt: now),
            new RcloneRuntimeSnapshot(
                VisibilityFenceRequired: true,
                WholeCacheVisibilityFencePending: false,
                VisibilityFenceGeneration: 1,
                RemoteControlEnabled: true,
                HostConfigured: true,
                LastAttemptAt: now,
                LastSuccessfulConfiguredCallAt: null,
                LastError: raw),
            now);
        var mount = MountDiagnosticStatus.FromSnapshot(new MountStatusSnapshot(
            Type: "dfs",
            Directory: "/safe-compatibility-path",
            Enabled: true,
            Ready: false,
            State: "failed",
            Message: raw,
            FuseErrors: 1,
            ActiveOperations: 0,
            WaitingOperations: 0,
            LastInvalidationAt: null,
            UpdatedAt: now,
            Cache: null));
        var readyMountWithFuseError = MountDiagnosticStatus.FromSnapshot(new MountStatusSnapshot(
            Type: "dfs",
            Directory: "/safe-compatibility-path",
            Enabled: true,
            Ready: true,
            State: "ready",
            Message: raw,
            FuseErrors: 1,
            ActiveOperations: 0,
            WaitingOperations: 0,
            LastInvalidationAt: null,
            UpdatedAt: now,
            Cache: null));
        var arr = ArrImportCommandDiagnosticStatus.FromStats(
            new NzbWebDAV.Database.DavDatabaseClient.ArrImportCommandStats(
                Pending: 0,
                WaitingForInvalidation: 0,
                Executing: 0,
                Retry: 1,
                Dispatched: 0,
                NoRoute: 0,
                Quarantined: 1,
                OldestActiveAt: now,
                LastError: raw,
                LastQuarantineReason: raw),
            now);

        PublicFailureCanary.AssertSafe(rclone.LastError);
        PublicFailureCanary.AssertSafe(rclone.RuntimeLastError);
        PublicFailureCanary.AssertSafe(mount.Message);
        PublicFailureCanary.AssertSafe(readyMountWithFuseError.Message);
        PublicFailureCanary.AssertSafe(arr.LastError);
        PublicFailureCanary.AssertSafe(arr.LastQuarantineReason);
        Assert.True(rclone.LastError == "rclone_invalidation_legacy_failure");
        Assert.True(rclone.RuntimeLastError == "rclone_invalidation_legacy_failure");
        Assert.True(mount.Message == PublicDiagnosticContract.Message(PublicDiagnosticKind.MountFailure));
        Assert.True(readyMountWithFuseError.Message == PublicDiagnosticContract.Message(PublicDiagnosticKind.MountFailure));
        Assert.True(arr.LastError == PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure));
        Assert.True(arr.LastQuarantineReason == PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure));
    }

    [Fact]
    public async Task SearchNudgeSaveAndLegacyProjectionUseFixedFailure()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var command = new ArrSearchNudgeCommand
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = "fixture",
            InstanceHost = "http://127.0.0.1",
            CommandName = "EpisodeSearch",
            TargetsJson = "[]",
            Mode = "report",
            Status = "failed",
            Score = 0,
            ReasonsJson = "[]",
            Error = PublicFailureCanary.Composite,
            CreatedAt = now,
            NextAllowedAt = now,
        };
        dbContext.ArrSearchNudgeCommands.Add(command);

        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.ArrSearchNudgeCommands.AsNoTracking().SingleAsync();
        PublicFailureCanary.AssertSafe(stored.Error);
        Assert.True(stored.Error == PublicDiagnosticContract.Message(PublicDiagnosticKind.SearchNudgeFailure));

        await dbContext.ArrSearchNudgeCommands
            .Where(x => x.Id == command.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Error, PublicFailureCanary.Composite));
        var service = new ArrOperationsService(new ConfigManager());
        var projected = Assert.Single(await service.GetSearchNudgeCommandsAsync(
            dbContext,
            limit: 10,
            status: null,
            arrApp: null,
            mode: null,
            commandName: null,
            search: null));
        PublicFailureCanary.AssertSafe(projected.Error);
        Assert.True(projected.Error == PublicDiagnosticContract.Message(PublicDiagnosticKind.SearchNudgeFailure));
    }

    [Theory]
    [InlineData(PublicDiagnosticContract.ConfirmedMissingArticles)]
    [InlineData(PublicDiagnosticContract.ConfirmedMissingArticlesOperatorReview)]
    [InlineData(PublicDiagnosticContract.ConfirmedMissingAfterDownloadOperatorReview)]
    [InlineData("ARR import failed.")]
    public void VerificationQuarantineDetailPreservesOnlyClosedSafeValues(string detail)
    {
        Assert.Equal(detail, PublicDiagnosticContract.VerificationQuarantineDetail(detail));
    }

    [Theory]
    [InlineData("confirmed missing articles ")]
    [InlineData("Confirmed missing articles")]
    [InlineData("confirmed missing articles\r\noperator supplied")]
    public void VerificationQuarantineDetailCollapsesNearMissAndHostileValues(string detail)
    {
        Assert.Equal(
            PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure),
            PublicDiagnosticContract.VerificationQuarantineDetail(detail));
        PublicFailureCanary.AssertSafe(PublicDiagnosticContract.VerificationQuarantineDetail(detail));
        Assert.Equal(
            PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure),
            PublicDiagnosticContract.VerificationQuarantineDetail(PublicFailureCanary.Composite));
    }

    [Fact]
    public async Task SearchNudgeNetworkAliasMatchesOnlyCanonicalFailedDiagnostics()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var canonical = PublicDiagnosticContract.Message(PublicDiagnosticKind.SearchNudgeFailure);
        var expected = NewSearchNudge("failed", canonical, now);
        var nonFailed = NewSearchNudge("planned", canonical, now.AddMinutes(-1));
        var legacyRaw = NewSearchNudge("failed", PublicFailureCanary.Composite, now.AddMinutes(-2));
        dbContext.ArrSearchNudgeCommands.AddRange(expected, nonFailed, legacyRaw);
        await dbContext.SaveChangesAsync();
        await dbContext.ArrSearchNudgeCommands
            .Where(x => x.Id == legacyRaw.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Error, PublicFailureCanary.Composite));
        var service = new ArrOperationsService(new ConfigManager());

        var network = await service.GetSearchNudgeCommandsAsync(
            dbContext, 10, null, null, null, null, "network");
        var canonicalSubstring = await service.GetSearchNudgeCommandsAsync(
            dbContext, 10, "failed", null, null, null, "ARR");
        var nearMiss = await service.GetSearchNudgeCommandsAsync(
            dbContext, 10, null, null, null, null, "networked");
        var hostile = await service.GetSearchNudgeCommandsAsync(
            dbContext, 10, null, null, null, null, PublicFailureCanary.Composite);

        var item = Assert.Single(network);
        Assert.Equal(expected.Id.ToString(), item.Id);
        Assert.Equal(canonical, item.Error);
        Assert.Equal(expected.Id.ToString(), Assert.Single(canonicalSubstring).Id);
        Assert.Empty(nearMiss);
        Assert.Empty(hostile);
        PublicFailureCanary.AssertSafe(item.Error);
    }

    [Fact]
    public void ClosedOperationalDiagnosticGrammarsPreserveOnlySafeCategories()
    {
        Assert.Equal(
            PublicDiagnosticContract.HealthMissingRepairQueued,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthMissingRepairQueued,
                HealthCheckResult.RepairAction.ActionNeeded));
        Assert.Equal(
            PublicDiagnosticContract.HealthVerificationInconclusive,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthVerificationInconclusive,
                HealthCheckResult.RepairAction.None));
        Assert.Equal(
            PublicDiagnosticContract.RepairSearchInitiated,
            PublicDiagnosticContract.HealthDetail(
                PublicFailureCanary.Composite,
                HealthCheckResult.RepairAction.Repaired));
        Assert.Equal(
            PublicDiagnosticContract.HealthFileRemoved,
            PublicDiagnosticContract.HealthDetail(
                PublicFailureCanary.Composite,
                HealthCheckResult.RepairAction.Deleted));
        Assert.Equal(
            PublicDiagnosticContract.HealthRepairActionRequired,
            PublicDiagnosticContract.HealthDetail(
                PublicFailureCanary.Composite,
                HealthCheckResult.RepairAction.ActionNeeded));

        var validArr =
            "sonarr:0123456789abcdef:queue-timeout+refresh-http; radarr:fedcba9876543210:invalid-command";
        Assert.Equal(validArr, PublicDiagnosticContract.ArrImportFailureDetail(validArr));
        Assert.Equal("worker-error", PublicDiagnosticContract.ArrImportFailureDetail("worker-error"));
        Assert.All(
            new[]
            {
                "sonarr:0123456789ABCDEF:queue-timeout",
                "sonarr:0123456789abcdef:unknown-reason",
                "sonarr:http://user:pass@arr.internal:queue-timeout",
                "sonarr:0123456789abcdef:queue-timeout+queue-timeout",
                "route-category-owner-missing ",
                PublicFailureCanary.Composite,
            },
            value => Assert.Equal(
                PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure),
                PublicDiagnosticContract.ArrImportFailureDetail(value)));

        var repairEntries = new[]
        {
            (RepairEntryHealth.RepairEntryState.Pending, PublicDiagnosticContract.RepairPending),
            (RepairEntryHealth.RepairEntryState.Checking, PublicDiagnosticContract.RepairChecking),
            (RepairEntryHealth.RepairEntryState.Healthy, PublicDiagnosticContract.HealthHealthy),
            (RepairEntryHealth.RepairEntryState.Missing, PublicDiagnosticContract.RepairMissing),
            (RepairEntryHealth.RepairEntryState.ProviderError, PublicDiagnosticContract.RepairProviderCheckFailed),
            (RepairEntryHealth.RepairEntryState.Unknown, PublicDiagnosticContract.RepairInconclusive),
            (RepairEntryHealth.RepairEntryState.Repaired, PublicDiagnosticContract.RepairSearchInitiated),
            (RepairEntryHealth.RepairEntryState.Deleted, PublicDiagnosticContract.RepairFileRemoved),
            (RepairEntryHealth.RepairEntryState.ActionNeeded, PublicDiagnosticContract.RepairActionRequired),
            (RepairEntryHealth.RepairEntryState.Cancelled, PublicDiagnosticContract.RepairCancelled),
        };
        foreach (var (state, expected) in repairEntries)
        {
            var actual = PublicDiagnosticContract.RepairEntryDetail(state, PublicFailureCanary.Composite);
            Assert.Equal(expected, actual);
            PublicFailureCanary.AssertSafe(actual);
        }
        Assert.Equal(
            PublicDiagnosticContract.RepairVerificationQuarantined,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.ProviderError,
                PublicDiagnosticContract.RepairVerificationQuarantined));
        Assert.Equal(
            PublicDiagnosticContract.RepairVerificationRetryScheduled,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.ProviderError,
                PublicDiagnosticContract.RepairVerificationRetryScheduled));
    }

    [Fact]
    public void StateDerivedDiagnosticsNeverReturnBlankForMissingInput()
    {
        var healthCases = new[]
        {
            (HealthCheckResult.RepairAction.None,
                PublicDiagnosticContract.Message(PublicDiagnosticKind.HealthFailure)),
            (HealthCheckResult.RepairAction.ActionNeeded,
                PublicDiagnosticContract.HealthRepairActionRequired),
            (HealthCheckResult.RepairAction.Repaired,
                PublicDiagnosticContract.RepairSearchInitiated),
            (HealthCheckResult.RepairAction.Deleted,
                PublicDiagnosticContract.HealthFileRemoved),
        };
        foreach (var (repairAction, expected) in healthCases)
            foreach (var input in new string?[] { null, "", " " })
                Assert.Equal(expected, PublicDiagnosticContract.HealthDetail(input, repairAction));

        var runCases = new[]
        {
            (RepairRun.RepairRunStatus.Running, PublicDiagnosticContract.RepairRunInProgress),
            (RepairRun.RepairRunStatus.Completed, PublicDiagnosticContract.RepairRunCompleted),
            (RepairRun.RepairRunStatus.Cancelled, PublicDiagnosticContract.RepairRunCancelled),
            (RepairRun.RepairRunStatus.Failed,
                PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure)),
        };
        foreach (var (status, expected) in runCases)
            foreach (var input in new string?[] { null, "", " " })
                Assert.Equal(expected, PublicDiagnosticContract.RepairRunDetail(status, input));
    }

    [Fact]
    public void HealthAndRepairEntryDetailsPreserveOnlyExactStateCompatibleMeanings()
    {
        const string providerUnavailable = PublicDiagnosticContract.HealthProviderUnavailable;
        Assert.Equal(
            providerUnavailable,
            PublicDiagnosticContract.HealthDetail(
                providerUnavailable,
                HealthCheckResult.RepairAction.None));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataUnavailable,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthMetadataUnavailable,
                HealthCheckResult.RepairAction.None));
        Assert.Equal(
            PublicDiagnosticContract.HealthVerificationInconclusive,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthVerificationInconclusive,
                HealthCheckResult.RepairAction.None));

        Assert.Equal(
            PublicDiagnosticContract.HealthMissingRepairQueued,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Missing,
                PublicDiagnosticContract.HealthMissingRepairQueued));
        Assert.Equal(
            PublicDiagnosticContract.HealthMissingReviewRequired,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Missing,
                PublicDiagnosticContract.HealthMissingReviewRequired));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataMissingRepairQueued,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthMetadataMissingRepairQueued,
                HealthCheckResult.RepairAction.ActionNeeded));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataMissingReviewRequired,
            PublicDiagnosticContract.HealthDetail(
                PublicDiagnosticContract.HealthMetadataMissingReviewRequired,
                HealthCheckResult.RepairAction.ActionNeeded));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataMissingRepairQueued,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Missing,
                PublicDiagnosticContract.HealthMetadataMissingRepairQueued));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataMissingReviewRequired,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Missing,
                PublicDiagnosticContract.HealthMetadataMissingReviewRequired));
        Assert.Equal(
            providerUnavailable,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.ProviderError,
                providerUnavailable));
        Assert.Equal(
            PublicDiagnosticContract.HealthMetadataUnavailable,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.ProviderError,
                PublicDiagnosticContract.HealthMetadataUnavailable));
        Assert.Equal(
            PublicDiagnosticContract.HealthVerificationInconclusive,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Unknown,
                PublicDiagnosticContract.HealthVerificationInconclusive));

        Assert.Equal(
            PublicDiagnosticContract.RepairMissing,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Missing,
                $"{PublicDiagnosticContract.HealthMissingReviewRequired} "));
        Assert.Equal(
            PublicDiagnosticContract.RepairProviderCheckFailed,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.ProviderError,
                $"{providerUnavailable} "));
        Assert.Equal(
            PublicDiagnosticContract.RepairInconclusive,
            PublicDiagnosticContract.RepairEntryDetail(
                RepairEntryHealth.RepairEntryState.Unknown,
                $"{PublicDiagnosticContract.HealthVerificationInconclusive} "));
        PublicFailureCanary.AssertSafe(PublicDiagnosticContract.RepairEntryDetail(
            RepairEntryHealth.RepairEntryState.ProviderError,
            PublicFailureCanary.Composite));
    }

    [Fact]
    public void RepairRunProjectionCanonicalizesEveryStatusWithoutLegacyText()
    {
        var now = DateTimeOffset.UtcNow;
        var cases = new[]
        {
            (RepairRun.RepairRunStatus.Running, PublicDiagnosticContract.RepairRunInProgress),
            (RepairRun.RepairRunStatus.Completed, PublicDiagnosticContract.RepairRunCompleted),
            (RepairRun.RepairRunStatus.Cancelled, PublicDiagnosticContract.RepairRunCancelled),
            (RepairRun.RepairRunStatus.Failed, PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure)),
        };

        foreach (var (status, expected) in cases)
        {
            var projected = RepairRunDto.FromModel(new RepairRun
            {
                Id = Guid.NewGuid(),
                Status = status,
                StartedAt = now,
                UpdatedAt = now,
                Message = PublicFailureCanary.Composite,
            });

            Assert.Equal(expected, projected.Message);
            PublicFailureCanary.AssertSafe(projected.Message);
        }

        var noEligible = RepairRunDto.FromModel(new RepairRun
        {
            Id = Guid.NewGuid(),
            Status = RepairRun.RepairRunStatus.Completed,
            StartedAt = now,
            UpdatedAt = now,
            Message = PublicDiagnosticContract.RepairRunNoEligibleFiles,
        });
        Assert.Equal(PublicDiagnosticContract.RepairRunNoEligibleFiles, noEligible.Message);
    }

    [Fact]
    public void HealthProjectionCanonicalizesEveryResultAndRepairState()
    {
        var cases = new[]
        {
            (HealthCheckResult.HealthResult.Healthy, HealthCheckResult.RepairAction.None,
                PublicDiagnosticContract.HealthHealthy),
            (HealthCheckResult.HealthResult.Unhealthy, HealthCheckResult.RepairAction.None,
                PublicDiagnosticContract.Message(PublicDiagnosticKind.HealthFailure)),
            (HealthCheckResult.HealthResult.Unhealthy, HealthCheckResult.RepairAction.ActionNeeded,
                PublicDiagnosticContract.HealthRepairActionRequired),
            (HealthCheckResult.HealthResult.Unhealthy, HealthCheckResult.RepairAction.Repaired,
                PublicDiagnosticContract.RepairSearchInitiated),
            (HealthCheckResult.HealthResult.Unhealthy, HealthCheckResult.RepairAction.Deleted,
                PublicDiagnosticContract.HealthFileRemoved),
        };

        foreach (var (result, repairStatus, expected) in cases)
        {
            var projected = GetHealthCheckHistoryResponse.Project(new HealthCheckResult
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                DavItemId = Guid.NewGuid(),
                Path = "/safe-compatibility-path",
                Result = result,
                RepairStatus = repairStatus,
                Message = PublicFailureCanary.Composite,
            });

            Assert.Equal(expected, projected.Message);
            PublicFailureCanary.AssertSafe(projected.Message);
        }
    }

    [Fact]
    public async Task SaveBoundarySanitizesAddedImportReceiptDetailForEveryState()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var candidates = Enum.GetValues<ImportReceiptState>()
            .Select(state => NewImportReceipt(state, now, PublicFailureCanary.Composite))
            .ToList();
        var candidateIds = candidates.Select(x => x.Id).ToArray();
        dbContext.ImportReceipts.AddRange(candidates);

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var receipts = await dbContext.ImportReceipts.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.State);
        foreach (var state in Enum.GetValues<ImportReceiptState>())
        {
            var expected = state is ImportReceiptState.NeedsReview
                or ImportReceiptState.VerificationQuarantined
                ? PublicDiagnosticContract.ArrImportFailureMessage
                : null;
            Assert.Equal(expected, receipts[state].Detail);
            PublicFailureCanary.AssertSafe(receipts[state].Detail);
        }
    }

    [Fact]
    public async Task SaveBoundarySanitizesModifiedImportReceiptDetailForEveryState()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var candidates = Enum.GetValues<ImportReceiptState>()
            .Select(state => NewImportReceipt(state, now, detail: null))
            .ToList();
        var candidateIds = candidates.Select(x => x.Id).ToArray();
        dbContext.ImportReceipts.AddRange(candidates);
        await dbContext.SaveChangesAsync();

        foreach (var receipt in await dbContext.ImportReceipts
                     .Where(x => candidateIds.Contains(x.Id))
                     .ToListAsync())
            receipt.Detail = PublicDiagnosticContract.ConfirmedMissingArticles;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var receipts = await dbContext.ImportReceipts.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.State);
        foreach (var state in Enum.GetValues<ImportReceiptState>())
        {
            var expected = state switch
            {
                ImportReceiptState.NeedsReview => PublicDiagnosticContract.ArrImportFailureMessage,
                ImportReceiptState.VerificationQuarantined =>
                    PublicDiagnosticContract.ConfirmedMissingArticles,
                _ => null,
            };
            Assert.Equal(expected, receipts[state].Detail);
            PublicFailureCanary.AssertSafe(receipts[state].Detail);
        }
    }

    [Fact]
    public async Task HistoryFailureBoundaryPreservesOnlyTheClosedPostDownloadVerificationFailure()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        const string verificationFailure = PublicDiagnosticContract.PostDownloadVerificationFailure;
        var verificationHistoryId = Guid.NewGuid();
        var rawHistoryId = Guid.NewGuid();
        dbContext.HistoryItems.AddRange(
            NewFailedHistory(verificationHistoryId, verificationFailure),
            NewFailedHistory(rawHistoryId, PublicFailureCanary.Composite));

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(
            verificationFailure,
            (await dbContext.HistoryItems.AsNoTracking()
                .SingleAsync(x => x.Id == verificationHistoryId)).FailMessage);
        Assert.Equal(
            PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure),
            (await dbContext.HistoryItems.AsNoTracking()
                .SingleAsync(x => x.Id == rawHistoryId)).FailMessage);
    }

    [Fact]
    public async Task SynchronousSaveBoundarySanitizesFailedHistory()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var historyId = Guid.NewGuid();
        dbContext.HistoryItems.Add(NewFailedHistory(historyId, PublicFailureCanary.Composite));

        dbContext.SaveChanges();
        dbContext.ChangeTracker.Clear();

        var saved = await dbContext.HistoryItems.AsNoTracking().SingleAsync(x => x.Id == historyId);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure), saved.FailMessage);
        PublicFailureCanary.AssertSafe(saved.FailMessage);
    }

    [Fact]
    public async Task SaveBoundarySanitizesEveryDurableDiagnosticAndCanonicalizesPositiveStates()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow;
        var raw = PublicFailureCanary.Composite;
        var historyId = Guid.NewGuid();
        var repairRunId = Guid.NewGuid();

        dbContext.HistoryItems.AddRange(
            new HistoryItem
            {
                Id = historyId,
                CreatedAt = now.UtcDateTime,
                FileName = "failed.nzb",
                JobName = "failed",
                Category = "fixture",
                DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
                FailMessage = raw,
            },
            new HistoryItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = now.UtcDateTime,
                FileName = "completed.nzb",
                JobName = "completed",
                Category = "fixture",
                DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                FailMessage = "positive-history-message",
            });
        dbContext.MaintenanceRuns.AddRange(
            new MaintenanceRun
            {
                Id = Guid.NewGuid(),
                Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
                Status = MaintenanceRunStatus.Failed,
                CreatedAt = now,
                UpdatedAt = now,
                Message = raw,
                Error = raw,
            },
            new MaintenanceRun
            {
                Id = Guid.NewGuid(),
                Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
                Status = MaintenanceRunStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now,
                Message = "positive-maintenance-message",
            });
        dbContext.ArrSearchNudgeCommands.Add(new ArrSearchNudgeCommand
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = "fixture",
            InstanceHost = "http://127.0.0.1",
            CommandName = "EpisodeSearch",
            Status = "failed",
            Error = raw,
            CreatedAt = now,
            NextAllowedAt = now,
        });
        dbContext.ArrImportCommands.Add(new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = historyId,
            Category = "fixture",
            Status = ArrImportCommandStatus.Retry,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            LastError = raw,
        });
        dbContext.ImportReceipts.Add(new ImportReceipt
        {
            Id = Guid.NewGuid(),
            DavItemId = Guid.NewGuid(),
            HistoryItemId = historyId,
            State = ImportReceiptState.VerificationQuarantined,
            CreatedAt = now,
            UpdatedAt = now,
            Detail = raw,
        });
        dbContext.RcloneInvalidationItems.Add(new RcloneInvalidationItem
        {
            Id = Guid.NewGuid(),
            Path = "/fixture",
            CreatedAt = now,
            NextAttemptAt = now,
            LastError = raw,
        });
        dbContext.WorkerJobs.Add(new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = WorkerJob.JobKind.Repair,
            Status = WorkerJob.JobStatus.Retry,
            TargetId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now,
            LastError = raw,
        });
        dbContext.HealthCheckResults.AddRange(
            new HealthCheckResult
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                DavItemId = Guid.NewGuid(),
                Path = "/failed",
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = raw,
            },
            new HealthCheckResult
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                DavItemId = Guid.NewGuid(),
                Path = "/healthy",
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = "positive-health-message",
            });
        dbContext.RepairRuns.AddRange(
            new RepairRun
            {
                Id = repairRunId,
                Status = RepairRun.RepairRunStatus.Failed,
                StartedAt = now,
                UpdatedAt = now,
                Message = raw,
            },
            new RepairRun
            {
                Id = Guid.NewGuid(),
                Status = RepairRun.RepairRunStatus.Completed,
                StartedAt = now,
                UpdatedAt = now,
                Message = "positive-repair-message",
            });
        dbContext.RepairEntryHealth.AddRange(
            new RepairEntryHealth
            {
                Id = Guid.NewGuid(),
                RepairRunId = repairRunId,
                DavItemId = Guid.NewGuid(),
                Path = "/failed",
                State = RepairEntryHealth.RepairEntryState.ProviderError,
                Message = raw,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new RepairEntryHealth
            {
                Id = Guid.NewGuid(),
                RepairRunId = repairRunId,
                DavItemId = Guid.NewGuid(),
                Path = "/healthy",
                State = RepairEntryHealth.RepairEntryState.Healthy,
                Message = "positive-entry-message",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new RepairEntryHealth
            {
                Id = Guid.NewGuid(),
                RepairRunId = repairRunId,
                DavItemId = Guid.NewGuid(),
                Path = "/repaired",
                State = RepairEntryHealth.RepairEntryState.Repaired,
                Message = $"Triggered new ARR search through {raw}",
                CreatedAt = now,
                UpdatedAt = now,
            });
        dbContext.RepairBrokenFiles.Add(new RepairBrokenFile
        {
            Id = Guid.NewGuid(),
            RepairRunId = repairRunId,
            DavItemId = Guid.NewGuid(),
            Path = "/broken",
            Reason = raw,
            CreatedAt = now,
        });
        dbContext.ArrDownloadLifecycleEvents.AddRange(
            new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = "fixture",
                State = "Failed",
                StateReason = raw,
                CreatedAt = now,
            },
            new ArrDownloadLifecycleEvent
            {
                Id = Guid.NewGuid(),
                ArrApp = "sonarr",
                InstanceKey = "fixture",
                State = "Completed",
                StateReason = "positive-lifecycle-message",
                CreatedAt = now,
            });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.All(await dbContext.HistoryItems.Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed).ToListAsync(),
            x => Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure), x.FailMessage));
        Assert.Equal("positive-history-message", (await dbContext.HistoryItems.SingleAsync(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)).FailMessage);
        var failedMaintenance = await dbContext.MaintenanceRuns.SingleAsync(x => x.Status == MaintenanceRunStatus.Failed);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.MaintenanceFailure), failedMaintenance.Message);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.MaintenanceFailure), failedMaintenance.Error);
        Assert.Equal("positive-maintenance-message", (await dbContext.MaintenanceRuns.SingleAsync(x => x.Status == MaintenanceRunStatus.Completed)).Message);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.SearchNudgeFailure), (await dbContext.ArrSearchNudgeCommands.SingleAsync()).Error);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure), (await dbContext.ArrImportCommands.SingleAsync()).LastError);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.ArrImportFailure),
            (await dbContext.ImportReceipts.SingleAsync(x => x.HistoryItemId == historyId
                && x.State == ImportReceiptState.VerificationQuarantined)).Detail);
        Assert.Equal("rclone_invalidation_legacy_failure", (await dbContext.RcloneInvalidationItems.SingleAsync()).LastError);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.WorkerFailure), (await dbContext.WorkerJobs.SingleAsync()).LastError);
        Assert.Equal(PublicDiagnosticContract.HealthRepairActionRequired, (await dbContext.HealthCheckResults.SingleAsync(x => x.Result == HealthCheckResult.HealthResult.Unhealthy)).Message);
        Assert.Equal(PublicDiagnosticContract.HealthHealthy, (await dbContext.HealthCheckResults.SingleAsync(x => x.Result == HealthCheckResult.HealthResult.Healthy)).Message);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure), (await dbContext.RepairRuns.SingleAsync(x => x.Status == RepairRun.RepairRunStatus.Failed)).Message);
        Assert.Equal(PublicDiagnosticContract.RepairRunCompleted, (await dbContext.RepairRuns.SingleAsync(x => x.Status == RepairRun.RepairRunStatus.Completed)).Message);
        Assert.Equal(PublicDiagnosticContract.RepairProviderCheckFailed, (await dbContext.RepairEntryHealth.SingleAsync(x => x.State == RepairEntryHealth.RepairEntryState.ProviderError)).Message);
        Assert.Equal(PublicDiagnosticContract.HealthHealthy, (await dbContext.RepairEntryHealth.SingleAsync(x => x.State == RepairEntryHealth.RepairEntryState.Healthy)).Message);
        Assert.Equal(PublicDiagnosticContract.RepairSearchInitiated, (await dbContext.RepairEntryHealth.SingleAsync(x => x.State == RepairEntryHealth.RepairEntryState.Repaired)).Message);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.RepairFailure), (await dbContext.RepairBrokenFiles.SingleAsync()).Reason);
        Assert.Equal(PublicDiagnosticContract.Message(PublicDiagnosticKind.QueueFailure), (await dbContext.ArrDownloadLifecycleEvents.SingleAsync(x => x.State == "Failed")).StateReason);
        Assert.Equal("positive-lifecycle-message", (await dbContext.ArrDownloadLifecycleEvents.SingleAsync(x => x.State == "Completed")).StateReason);
    }

    private static ArrSearchNudgeCommand NewSearchNudge(
        string status,
        string error,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.NewGuid(),
            ArrApp = "sonarr",
            InstanceKey = $"fixture:{Guid.NewGuid():N}",
            InstanceHost = "http://127.0.0.1",
            CommandName = "EpisodeSearch",
            TargetsJson = "[]",
            Mode = "report",
            Status = status,
            Score = 0,
            ReasonsJson = "[]",
            Error = error,
            CreatedAt = createdAt,
            NextAllowedAt = createdAt,
        };

    private static HistoryItem NewFailedHistory(Guid id, string failMessage) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = "failed.nzb",
        JobName = "failed",
        Category = "fixture",
        DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
        FailMessage = failMessage,
    };

    private static ImportReceipt NewImportReceipt(
        ImportReceiptState state,
        DateTimeOffset now,
        string? detail) => new()
        {
            Id = Guid.NewGuid(),
            DavItemId = Guid.NewGuid(),
            HistoryItemId = Guid.NewGuid(),
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
            Detail = detail,
        };
}

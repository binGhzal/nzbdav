using backend.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestDoubles;

namespace backend.Tests.Coordination;

public sealed class DatabaseWorkerJobCoordinatorPostgreSqlTests
{
    private const long LaneLockNamespace = 0x4E5A424400000000;

    [PostgreSqlFact]
    public async Task AcquisitionRetriesAForced40001ExactlyOnceAndRecordsTelemetry()
    {
        await WithSchemaAsync(async (adminConnection, schemaName, options) =>
        {
            await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 1);
            await InstallForcedSerializationFailureAsync(adminConnection, schemaName);
            await using var context = new PostgreSqlDavDatabaseContext(options);
            var coordinator = CreateCoordinator(context, capacity: 1);
            var now = DateTimeOffset.UtcNow;
            var retriesBefore = DatabaseTelemetry.Shared.GetSnapshot().LeaseRetries;

            var lease = Assert.Single(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

            Assert.Equal(2L, await ReadSequenceValueAsync(
                adminConnection, schemaName, "lease_retry_attempts"));
            Assert.Equal(
                retriesBefore + 1,
                DatabaseTelemetry.Shared.GetSnapshot().LeaseRetries);
            await AssertUsableLeaseAsync(coordinator, lease, now);
        });
    }

    [PostgreSqlFact]
    public async Task SameLaneDistinctCandidatesShareCapacityAcrossOwners()
    {
        await WithSchemaAsync(async (_, _, options) =>
        {
            await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 2);
            await using var firstContext = new PostgreSqlDavDatabaseContext(options);
            await using var secondContext = new PostgreSqlDavDatabaseContext(options);
            var firstCoordinator = CreateCoordinator(firstContext, capacity: 2);
            var secondCoordinator = CreateCoordinator(secondContext, capacity: 2);
            var now = DateTimeOffset.UtcNow;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstTask = AcquireAfterGateAsync(firstCoordinator, "worker-a", now, gate.Task);
            var secondTask = AcquireAfterGateAsync(secondCoordinator, "worker-b", now, gate.Task);
            gate.SetResult();
            var results = await Task.WhenAll(firstTask, secondTask);
            var leases = results.SelectMany(result => result).ToArray();

            Assert.All(results, result => Assert.Single(result));
            Assert.Equal(2, leases.Select(lease => lease.Identity.JobId).Distinct().Count());
            Assert.Equal(new[] { "worker-a", "worker-b" },
                leases.Select(lease => lease.Identity.Owner).OrderBy(owner => owner).ToArray());
            await using var verificationContext = new PostgreSqlDavDatabaseContext(options);
            Assert.Equal(2, await verificationContext.WorkerJobs.CountAsync(job =>
                job.Kind == WorkerJob.JobKind.Verify
                && job.Status == WorkerJob.JobStatus.Leased
                && job.LeaseExpiresAt > now));
            var verificationCoordinator = CreateCoordinator(verificationContext, capacity: 2);
            foreach (var lease in leases)
                await AssertUsableLeaseAsync(verificationCoordinator, lease, now);
        });
    }

    [PostgreSqlFact]
    public async Task SameLaneConcurrentClaimersDoNotExceedConfiguredCapacity()
    {
        await WithSchemaAsync(async (_, _, options) =>
        {
            await using var firstContext = new PostgreSqlDavDatabaseContext(options);
            await using var secondContext = new PostgreSqlDavDatabaseContext(options);
            var firstCoordinator = CreateCoordinator(firstContext, capacity: 1);
            var secondCoordinator = CreateCoordinator(secondContext, capacity: 1);
            var baseline = DateTimeOffset.UtcNow;

            for (var iteration = 0; iteration < 12; iteration++)
            {
                var now = baseline.AddSeconds(iteration);
                await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 2, seededAt: now);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var firstTask = AcquireAfterGateAsync(
                    firstCoordinator, $"worker-a-{iteration}", now, gate.Task);
                var secondTask = AcquireAfterGateAsync(
                    secondCoordinator, $"worker-b-{iteration}", now, gate.Task);
                gate.SetResult();
                var results = await Task.WhenAll(firstTask, secondTask);

                Assert.Equal(1, results.Sum(result => result.Count));
                await using var verificationContext = new PostgreSqlDavDatabaseContext(options);
                Assert.Equal(1, await verificationContext.WorkerJobs.CountAsync(job =>
                    job.Kind == WorkerJob.JobKind.Verify
                    && job.Status == WorkerJob.JobStatus.Leased
                    && job.LeaseExpiresAt > now));
            }
        });
    }

    [PostgreSqlFact]
    public async Task PostgreSqlLaneLocksKeepDownloadIndependentFromBlockedVerifyLane()
    {
        await WithSchemaAsync(async (adminConnection, schemaName, baseOptions) =>
        {
            await SeedJobsAsync(baseOptions, WorkerJob.JobKind.Verify, 1);
            await SeedJobsAsync(baseOptions, WorkerJob.JobKind.Download, 1, clearExisting: false);
            var baseConnectionString = baseOptions.Extensions
                .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
                .Single().ConnectionString!;
            var verifyApplicationName = $"verify_lane_{Guid.NewGuid():N}";
            var verifyOptions = CreateOptions(baseConnectionString, verifyApplicationName);
            var downloadOptions = CreateOptions(baseConnectionString, $"download_lane_{Guid.NewGuid():N}");
            await using var blockerConnection = new NpgsqlConnection(baseConnectionString);
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
            await ExecuteNonQueryAsync(blockerConnection,
                $"SELECT pg_advisory_xact_lock({GetLaneLockKey(WorkerJob.JobKind.Verify)})");
            await using var verifyContext = new PostgreSqlDavDatabaseContext(verifyOptions);
            await using var downloadContext = new PostgreSqlDavDatabaseContext(downloadOptions);
            var policy = new FixedCapacityPolicy(1);
            var leaseOptions = Options.Create(new WorkerLeaseOptions());
            var verifyCoordinator = new DatabaseWorkerJobCoordinator(verifyContext, policy, leaseOptions);
            var downloadCoordinator = new DatabaseWorkerJobCoordinator(downloadContext, policy, leaseOptions);
            var now = DateTimeOffset.UtcNow;

            var verifyTask = verifyCoordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "verify-worker", 1, now, CancellationToken.None);
            await WaitForAdvisoryLockAsync(adminConnection, verifyApplicationName, CancellationToken.None);
            Assert.False(verifyTask.IsCompleted);

            var downloadLease = Assert.Single(await downloadCoordinator.LeaseAsync(
                    WorkerJob.JobKind.Download, "download-worker", 1, now, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(verifyTask.IsCompleted);

            await blockerTransaction.CommitAsync();
            var verifyLease = Assert.Single(await verifyTask.WaitAsync(TimeSpan.FromSeconds(2)));
            await AssertUsableLeaseAsync(downloadCoordinator, downloadLease, now);
            await AssertUsableLeaseAsync(verifyCoordinator, verifyLease, now);
        });
    }

    [PostgreSqlFact]
    public async Task DownloadPauseAtTheNextMicrosecondRemainsIneligibleUntilThatBoundary()
    {
        await WithSchemaAsync(async (_, _, options) =>
        {
            var rawNow = new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero).AddTicks(9);
            await SeedJobsAsync(
                options,
                WorkerJob.JobKind.Download,
                1,
                seededAt: rawNow);
            var localRawNow = rawNow.LocalDateTime;
            var pauseUntil = DateTime.SpecifyKind(
                new DateTime(localRawNow.Ticks - localRawNow.Ticks % 10 + 10),
                DateTimeKind.Unspecified);
            await using var context = new PostgreSqlDavDatabaseContext(options);
            await context.QueueItems.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PauseUntil, pauseUntil));
            var coordinator = CreateCoordinator(context, capacity: 1);

            Assert.Empty(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Download,
                "worker-before-boundary",
                1,
                rawNow,
                CancellationToken.None));
            Assert.Single(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Download,
                "worker-at-boundary",
                1,
                rawNow.AddTicks(1),
                CancellationToken.None));
        });
    }

    [PostgreSqlFact]
    public async Task ExpiredCancellationSweepIsIsolatedToTheClaimedLane()
    {
        await WithSchemaAsync(async (_, _, options) =>
        {
            var now = DateTimeOffset.UtcNow;
            await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 1, seededAt: now);
            await SeedJobsAsync(
                options,
                WorkerJob.JobKind.Download,
                1,
                clearExisting: false,
                seededAt: now);
            await using var context = new PostgreSqlDavDatabaseContext(options);
            var coordinator = CreateCoordinator(context, capacity: 1);
            var verifyLease = Assert.Single(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "verify-worker", 1, now, CancellationToken.None));
            var downloadLease = Assert.Single(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Download, "download-worker", 1, now, CancellationToken.None));
            Assert.True(await coordinator.RequestCancellationAsync(
                verifyLease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));
            Assert.True(await coordinator.RequestCancellationAsync(
                downloadLease.Identity.JobId, now.AddSeconds(1), CancellationToken.None));

            Assert.Empty(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify,
                "verify-sweeper",
                1,
                now.AddMinutes(3),
                CancellationToken.None));

            context.ChangeTracker.Clear();
            var verify = await context.WorkerJobs.AsNoTracking()
                .SingleAsync(job => job.Id == verifyLease.Identity.JobId);
            var download = await context.WorkerJobs.AsNoTracking()
                .SingleAsync(job => job.Id == downloadLease.Identity.JobId);
            Assert.Equal(WorkerJob.JobStatus.Cancelled, verify.Status);
            Assert.Equal(WorkerJob.JobStatus.Leased, download.Status);
            Assert.NotNull(download.CancelRequestedAt);
            Assert.NotNull(download.LeaseExpiresAt);
        });
    }

    [PostgreSqlFact]
    public async Task WaitingClaimSeesJobCommittedBeforeLaneLockIsReleased()
    {
        await WithSchemaAsync(async (adminConnection, _, baseOptions) =>
        {
            var baseConnectionString = baseOptions.Extensions
                .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
                .Single().ConnectionString!;
            await using var blockerConnection = new NpgsqlConnection(baseConnectionString);
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
            await ExecuteNonQueryAsync(
                blockerConnection,
                $"SELECT pg_advisory_xact_lock({GetLaneLockKey(WorkerJob.JobKind.Verify)})");

            var claimantApplicationName = $"fresh_snapshot_{Guid.NewGuid():N}";
            var claimantOptions = CreateOptions(baseConnectionString, claimantApplicationName);
            await using var claimantContext = new PostgreSqlDavDatabaseContext(claimantOptions);
            var coordinator = CreateCoordinator(claimantContext, capacity: 1);
            var now = DateTimeOffset.UtcNow;
            var leaseTask = coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify,
                "waiting-worker",
                1,
                now,
                CancellationToken.None);
            await WaitForAdvisoryLockAsync(
                adminConnection,
                claimantApplicationName,
                CancellationToken.None);

            var insertedJob = new WorkerJob
            {
                Id = Guid.NewGuid(),
                Kind = WorkerJob.JobKind.Verify,
                Status = WorkerJob.JobStatus.Pending,
                TargetId = Guid.NewGuid(),
                Priority = 10,
                CreatedAt = now,
                UpdatedAt = now,
                AvailableAt = now
            };
            var blockerOptions = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
                .UseNpgsql(blockerConnection)
                .Options;
            await using (var blockerContext = new PostgreSqlDavDatabaseContext(blockerOptions))
            {
                await blockerContext.Database.UseTransactionAsync(blockerTransaction);
                blockerContext.WorkerJobs.Add(insertedJob);
                await blockerContext.SaveChangesAsync();
            }

            Assert.False(leaseTask.IsCompleted);
            await blockerTransaction.CommitAsync();

            var lease = Assert.Single(await leaseTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(insertedJob.Id, lease.Identity.JobId);
            await AssertUsableLeaseAsync(coordinator, lease, now);
        });
    }

    [PostgreSqlFact]
    public async Task GeneratedPostgreSqlClaimsUseDeterministicOrderAndNeverServerRandom()
    {
        await WithSchemaAsync(async (_, _, baseOptions) =>
        {
            await SeedJobsAsync(baseOptions, WorkerJob.JobKind.Verify, 3);
            var connectionString = baseOptions.Extensions
                .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
                .Single().ConnectionString!;
            var capture = new CommandCaptureInterceptor();
            var options = CreateOptions(
                connectionString,
                $"ordered_claim_{Guid.NewGuid():N}",
                capture);
            await using var context = new PostgreSqlDavDatabaseContext(options);
            var coordinator = CreateCoordinator(context, capacity: 2);
            capture.Clear();

            Assert.Equal(2, (await coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify,
                "ordered-worker",
                2,
                DateTimeOffset.UtcNow,
                CancellationToken.None)).Count);

            Assert.DoesNotContain(capture.Commands, command =>
                command.CommandText.Contains("random(", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(capture.Commands, command =>
                command.CommandText.Contains("WorkerJobs", StringComparison.Ordinal)
                && command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"Priority\" DESC", StringComparison.Ordinal)
                && command.CommandText.Contains("\"AvailableAt\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"CreatedAt\"", StringComparison.Ordinal));
        });
    }

    private static async Task WithSchemaAsync(
        Func<NpgsqlConnection, string, DbContextOptions<PostgreSqlDavDatabaseContext>, Task> test)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("worker_coordinator");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var connection = await schema.OpenConnectionAsync();
        var options = CreateOptions(schema.ConnectionString, $"schema_setup_{Guid.NewGuid():N}");
        await test(connection, schema.SchemaName, options);
    }

    private static DbContextOptions<PostgreSqlDavDatabaseContext> CreateOptions(
        string connectionString,
        string applicationName,
        params IInterceptor[] interceptors)
    {
        var namedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        var builder = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                namedConnectionString,
                postgres => postgres.MigrationsHistoryTable(DatabaseMigrationPolicy.PostgreSqlHistoryTableName));
        if (interceptors.Length != 0) builder.AddInterceptors(interceptors);
        return builder.Options;
    }

    private static async Task SeedJobsAsync(
        DbContextOptions<PostgreSqlDavDatabaseContext> options,
        WorkerJob.JobKind kind,
        int count,
        bool clearExisting = true,
        DateTimeOffset? seededAt = null)
    {
        await using var context = new PostgreSqlDavDatabaseContext(options);
        if (clearExisting) await context.WorkerJobs.ExecuteDeleteAsync();
        var now = seededAt ?? DateTimeOffset.UtcNow;
        var jobs = Enumerable.Range(0, count).Select(index => new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = Guid.NewGuid(),
            Priority = count - index,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now
        }).ToArray();
        context.WorkerJobs.AddRange(jobs);
        if (kind == WorkerJob.JobKind.Download)
        {
            var localCreatedAt = DateTime.SpecifyKind(
                new DateTime(now.LocalDateTime.Ticks - now.LocalDateTime.Ticks % 10),
                DateTimeKind.Unspecified);
            context.QueueItems.AddRange(jobs.Select((job, index) => new QueueItem
            {
                Id = job.TargetId,
                CreatedAt = localCreatedAt,
                FileName = $"download-{job.TargetId:N}.nzb",
                JobName = $"download-{job.TargetId:N}",
                NzbFileSize = 1,
                TotalSegmentBytes = 1,
                Category = "movies",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None
            }));
        }
        await context.SaveChangesAsync();
    }

    private static DatabaseWorkerJobCoordinator CreateCoordinator(
        DavDatabaseContext context,
        int capacity)
    {
        return new DatabaseWorkerJobCoordinator(
            context,
            new FixedCapacityPolicy(capacity),
            Options.Create(new WorkerLeaseOptions()));
    }

    private static async Task<IReadOnlyList<WorkerLease>> AcquireAfterGateAsync(
        IWorkerJobCoordinator coordinator,
        string owner,
        DateTimeOffset now,
        Task gate)
    {
        await gate;
        return await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, owner, 1, now, CancellationToken.None);
    }

    private static async Task AssertUsableLeaseAsync(
        IWorkerJobCoordinator coordinator,
        WorkerLease lease,
        DateTimeOffset now)
    {
        Assert.True(await coordinator.RenewAsync(
            lease.Identity, now.AddSeconds(10), CancellationToken.None));
        Assert.True(await coordinator.CompleteAsync(
            lease.Identity, null, now.AddSeconds(11), CancellationToken.None));
    }

    private static async Task InstallForcedSerializationFailureAsync(
        NpgsqlConnection connection,
        string schemaName)
    {
        await ExecuteNonQueryAsync(connection, $$"""
            CREATE SEQUENCE "{{schemaName}}".lease_retry_attempts;
            CREATE OR REPLACE FUNCTION "{{schemaName}}".force_one_serialization_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE attempt bigint;
            BEGIN
                attempt := nextval('"{{schemaName}}".lease_retry_attempts');
                IF attempt = 1 THEN
                    RAISE EXCEPTION 'forced serialization failure' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER force_one_serialization_failure
            BEFORE UPDATE ON "{{schemaName}}"."WorkerJobs"
            FOR EACH ROW
            WHEN (OLD."Status" <> 1 AND NEW."Status" = 1)
            EXECUTE FUNCTION "{{schemaName}}".force_one_serialization_failure();
            """);
    }

    private static async Task<long> ReadSequenceValueAsync(
        NpgsqlConnection connection,
        string schemaName,
        string sequenceName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT last_value FROM \"{schemaName}\".\"{sequenceName}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForAdvisoryLockAsync(
        NpgsqlConnection connection,
        string applicationName,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @applicationName
                      AND wait_event_type = 'Lock'
                      AND wait_event = 'advisory')
                """;
            command.Parameters.AddWithValue("applicationName", applicationName);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static long GetLaneLockKey(WorkerJob.JobKind kind) => LaneLockNamespace + (int)kind;

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedCapacityPolicy(int capacity) : IWorkerLaneCapacityPolicy
    {
        public int GetMaximum(WorkerJob.JobKind kind) => capacity;
    }
}

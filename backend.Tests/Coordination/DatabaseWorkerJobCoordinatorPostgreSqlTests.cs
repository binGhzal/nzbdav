using backend.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Coordination;

public sealed class DatabaseWorkerJobCoordinatorPostgreSqlTests
{
    private const long LaneLockNamespace = 0x4E5A424400000000;

    [PostgreSqlFact]
    public async Task SerializableAcquisitionRetriesAForced40001ExactlyOnceAndReturnsUsableLease()
    {
        await WithSchemaAsync(async (adminConnection, schemaName, options) =>
        {
            await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 1);
            await InstallForcedSerializationFailureAsync(adminConnection, schemaName);
            await using var context = new DavDatabaseContext(options);
            var coordinator = CreateCoordinator(context, capacity: 1);
            var now = DateTimeOffset.UtcNow;

            var lease = Assert.Single(await coordinator.LeaseAsync(
                WorkerJob.JobKind.Verify, "worker-a", 1, now, CancellationToken.None));

            Assert.Equal(2L, await ReadSequenceValueAsync(
                adminConnection, schemaName, "lease_retry_attempts"));
            await AssertUsableLeaseAsync(coordinator, lease, now);
        });
    }

    [PostgreSqlFact]
    public async Task SameLaneDistinctCandidatesShareCapacityAcrossOwners()
    {
        await WithSchemaAsync(async (_, _, options) =>
        {
            await SeedJobsAsync(options, WorkerJob.JobKind.Verify, 2);
            await using var firstContext = new DavDatabaseContext(options);
            await using var secondContext = new DavDatabaseContext(options);
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
            await using var verificationContext = new DavDatabaseContext(options);
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
            await using var verifyContext = new DavDatabaseContext(verifyOptions);
            await using var downloadContext = new DavDatabaseContext(downloadOptions);
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

    private static async Task WithSchemaAsync(
        Func<NpgsqlConnection, string, DbContextOptions<DavDatabaseContext>, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.TestConnectionStringVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"worker_coordinator_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteNonQueryAsync(adminConnection, $"CREATE SCHEMA \"{schemaName}\"");

        try
        {
            var schemaConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schemaName
            }.ConnectionString;
            var options = CreateOptions(schemaConnectionString, $"schema_setup_{Guid.NewGuid():N}");
            await using (var migrationContext = new DavDatabaseContext(options))
                await migrationContext.Database.MigrateAsync();
            await test(adminConnection, schemaName, options);
        }
        finally
        {
            await ExecuteNonQueryAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE");
        }
    }

    private static DbContextOptions<DavDatabaseContext> CreateOptions(
        string connectionString,
        string applicationName)
    {
        var namedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        return new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseNpgsql(namedConnectionString)
            .Options;
    }

    private static async Task SeedJobsAsync(
        DbContextOptions<DavDatabaseContext> options,
        WorkerJob.JobKind kind,
        int count,
        bool clearExisting = true)
    {
        await using var context = new DavDatabaseContext(options);
        if (clearExisting) await context.WorkerJobs.ExecuteDeleteAsync();
        var now = DateTimeOffset.UtcNow;
        context.WorkerJobs.AddRange(Enumerable.Range(0, count).Select(index => new WorkerJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = WorkerJob.JobStatus.Pending,
            TargetId = Guid.NewGuid(),
            Priority = count - index,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now
        }));
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

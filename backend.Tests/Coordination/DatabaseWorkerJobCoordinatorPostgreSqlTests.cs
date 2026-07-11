using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using backend.Tests.Database;

namespace backend.Tests.Coordination;

public sealed class DatabaseWorkerJobCoordinatorPostgreSqlTests
{
    [PostgreSqlFact]
    public async Task SerializableAcquisitionRetriesContentionWithoutOversubscribingOrReturningStaleLeases()
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
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseNpgsql(schemaConnectionString)
                .Options;
            await using (var migrationContext = new DavDatabaseContext(options))
                await migrationContext.Database.MigrateAsync();
            await InstallContentionTriggerAsync(adminConnection, schemaName);

            await AssertContentionAsync(options, candidateCount: 1, capacity: 1);
            await AssertContentionAsync(options, candidateCount: 2, capacity: 2);
        }
        finally
        {
            await ExecuteNonQueryAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE");
        }
    }

    private static async Task AssertContentionAsync(
        DbContextOptions<DavDatabaseContext> options,
        int candidateCount,
        int capacity)
    {
        await using (var resetContext = new DavDatabaseContext(options))
        {
            await resetContext.WorkerJobs.ExecuteDeleteAsync();
            var now = DateTimeOffset.UtcNow;
            resetContext.WorkerJobs.AddRange(Enumerable.Range(0, candidateCount).Select(index =>
                new WorkerJob
                {
                    Id = Guid.NewGuid(),
                    Kind = WorkerJob.JobKind.Verify,
                    Status = WorkerJob.JobStatus.Pending,
                    TargetId = Guid.NewGuid(),
                    Priority = candidateCount - index,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AvailableAt = now
                }));
            await resetContext.SaveChangesAsync();
        }

        await using var firstContext = new DavDatabaseContext(options);
        await using var secondContext = new DavDatabaseContext(options);
        var policy = new FixedCapacityPolicy(capacity);
        var leaseOptions = Options.Create(new WorkerLeaseOptions());
        var firstCoordinator = new DatabaseWorkerJobCoordinator(firstContext, policy, leaseOptions);
        var secondCoordinator = new DatabaseWorkerJobCoordinator(secondContext, policy, leaseOptions);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nowForLease = DateTimeOffset.UtcNow;

        var firstTask = AcquireAfterGateAsync(firstCoordinator, "worker-a", capacity, nowForLease, gate.Task);
        var secondTask = AcquireAfterGateAsync(secondCoordinator, "worker-b", capacity, nowForLease, gate.Task);
        gate.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);
        var leases = results.SelectMany(result => result).ToArray();

        Assert.Equal(capacity, leases.Length);
        Assert.Equal(capacity, leases.Select(lease => lease.Identity.JobId).Distinct().Count());
        Assert.All(leases, lease => Assert.Contains(lease.Identity.Owner, new[] { "worker-a", "worker-b" }));

        await using var verificationContext = new DavDatabaseContext(options);
        Assert.Equal(capacity, await verificationContext.WorkerJobs.CountAsync(job =>
            job.Kind == WorkerJob.JobKind.Verify
            && job.Status == WorkerJob.JobStatus.Leased
            && job.LeaseExpiresAt > nowForLease));
        var verificationCoordinator = new DatabaseWorkerJobCoordinator(
            verificationContext, policy, leaseOptions);
        foreach (var lease in leases)
        {
            Assert.True(await verificationCoordinator.RenewAsync(
                lease.Identity, nowForLease.AddSeconds(10), CancellationToken.None));
            Assert.True(await verificationCoordinator.CompleteAsync(
                lease.Identity, null, nowForLease.AddSeconds(11), CancellationToken.None));
        }
    }

    private static async Task<IReadOnlyList<WorkerLease>> AcquireAfterGateAsync(
        IWorkerJobCoordinator coordinator,
        string owner,
        int capacity,
        DateTimeOffset now,
        Task gate)
    {
        await gate;
        return await coordinator.LeaseAsync(
            WorkerJob.JobKind.Verify, owner, capacity, now, CancellationToken.None);
    }

    private static async Task InstallContentionTriggerAsync(
        NpgsqlConnection connection,
        string schemaName)
    {
        await ExecuteNonQueryAsync(connection, $$"""
            CREATE OR REPLACE FUNCTION "{{schemaName}}".delay_worker_lease_update()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_sleep(0.15);
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER delay_worker_lease_update
            BEFORE UPDATE ON "{{schemaName}}"."WorkerJobs"
            FOR EACH ROW
            WHEN (OLD."Status" <> 1 AND NEW."Status" = 1)
            EXECUTE FUNCTION "{{schemaName}}".delay_worker_lease_update();
            """);
    }

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

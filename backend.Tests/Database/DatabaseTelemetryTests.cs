using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;

namespace backend.Tests.Database;

public sealed class DatabaseTelemetryTests
{
    [Fact]
    public void SnapshotReportsRetryCountersAndLatencyPercentiles()
    {
        var telemetry = new DatabaseTelemetry(sampleCapacity: 256);
        for (var milliseconds = 1; milliseconds <= 100; milliseconds++)
        {
            telemetry.RecordQuery(TimeSpan.FromMilliseconds(milliseconds));
            telemetry.RecordTransaction(TimeSpan.FromMilliseconds(milliseconds * 2));
        }
        telemetry.RecordBusyRetry();
        telemetry.RecordBusyRetry();
        telemetry.RecordLeaseRetry();

        var snapshot = telemetry.GetSnapshot();

        Assert.Equal(2, snapshot.BusyRetries);
        Assert.Equal(1, snapshot.LeaseRetries);
        Assert.Equal(100, snapshot.Query.Count);
        Assert.Equal(95, snapshot.Query.P95Milliseconds);
        Assert.Equal(99, snapshot.Query.P99Milliseconds);
        Assert.Equal(100, snapshot.Transaction.Count);
        Assert.Equal(190, snapshot.Transaction.P95Milliseconds);
        Assert.Equal(198, snapshot.Transaction.P99Milliseconds);
    }

    [Fact]
    public async Task EfInterceptorsObserveSynchronousAndAsynchronousCommandsAndTransactions()
    {
        var telemetry = new DatabaseTelemetry(sampleCapacity: 32);
        var directory = Path.Combine(Path.GetTempPath(), $"nzbdav-db-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "telemetry.sqlite"),
                Pooling = false
            }.ToString();
            var options = new DbContextOptionsBuilder<TelemetryContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(
                    new DatabaseCommandTelemetryInterceptor(telemetry),
                    new DatabaseTransactionTelemetryInterceptor(telemetry))
                .Options;

            await using var context = new TelemetryContext(options);
            context.Database.ExecuteSqlRaw("CREATE TABLE Samples (Id INTEGER PRIMARY KEY);");
            await context.Database.ExecuteSqlRawAsync("SELECT 1;");
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await context.Database.ExecuteSqlRawAsync("INSERT INTO Samples DEFAULT VALUES;");
                await transaction.CommitAsync();
            }

            var snapshot = telemetry.GetSnapshot();
            Assert.True(snapshot.Query.Count >= 3);
            Assert.True(snapshot.Transaction.Count >= 1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StorageSnapshotReportsSqliteWalFreelistAndCheckpointState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nzbdav-db-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "storage.sqlite");
            var options = new DbContextOptionsBuilder<TelemetryContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false
                }.ToString())
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .Options;
            await using var context = new TelemetryContext(options);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE Samples (Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO Samples (Value) VALUES ('one');");
            var beforeCapture = await ReadCheckpointNoopAsync(context.Database.GetDbConnection());

            var snapshot = await DatabaseStorageTelemetry.CaptureAsync(
                context,
                cacheDuration: TimeSpan.Zero);
            var afterCapture = await ReadCheckpointNoopAsync(context.Database.GetDbConnection());

            Assert.Equal("sqlite", snapshot.Provider);
            Assert.True(snapshot.DatabaseBytes > 0);
            Assert.True(snapshot.PageCount > 0);
            Assert.True(snapshot.PageSizeBytes > 0);
            Assert.True(snapshot.WalBytes >= 0);
            Assert.True(snapshot.FreelistPages >= 0);
            Assert.True(snapshot.CheckpointBacklogBytes >= 0);
            Assert.Equal(beforeCapture, afterCapture);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(long Busy, long Log, long Checkpointed)> ReadCheckpointNoopAsync(
        System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(NOOP);";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private sealed class TelemetryContext(DbContextOptions<TelemetryContext> options) : DbContext(options);
}

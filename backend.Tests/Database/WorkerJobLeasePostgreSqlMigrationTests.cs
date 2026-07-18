using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class WorkerJobLeasePostgreSqlMigrationTests
{
    [PostgreSqlFact]
    public async Task Migration_CreatesRenewableWorkerLeaseSchema()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("worker_lease");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await using var dbContext = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        AssertNoPendingMigrationChanges(dbContext);
        await using var connection = await schema.OpenConnectionAsync();

            var columns = await ReadWorkerJobColumnsAsync(connection, schema.SchemaName);
            var additiveColumns = new[]
            {
                "LeaseToken",
                "LeaseGeneration",
                "LastHeartbeatAt",
                "StartedAt",
                "CancelRequestedAt",
                "FailureKind",
                "ProgressJson",
                "ProgressUpdatedAt",
                "ResultJson"
            };
            foreach (var columnName in additiveColumns)
                Assert.Contains(columnName, columns.Keys);

            AssertColumn(columns, "LeaseToken", "uuid", isNullable: true);
            AssertColumn(columns, "FailureKind", "integer", isNullable: true);
            AssertColumn(columns, "LastHeartbeatAt", "bigint", isNullable: true);
            AssertColumn(columns, "StartedAt", "bigint", isNullable: true);
            AssertColumn(columns, "CancelRequestedAt", "bigint", isNullable: true);
            AssertColumn(columns, "ProgressUpdatedAt", "bigint", isNullable: true);
            AssertColumn(columns, "ProgressJson", "character varying", isNullable: true);
            AssertColumn(columns, "ResultJson", "character varying", isNullable: true);
            AssertColumn(columns, "LeaseGeneration", "bigint", isNullable: false);
            Assert.Contains("0", columns["LeaseGeneration"].ColumnDefault ?? string.Empty);

            Assert.True(await WorkerJobLeaseIndexExistsAsync(connection, schema.SchemaName));
    }

    private static async Task<Dictionary<string, ColumnInfo>> ReadWorkerJobColumnsAsync
    (
        NpgsqlConnection connection,
        string schemaName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = @schemaName
              AND table_name = 'WorkerJobs'
            """;
        command.Parameters.AddWithValue("schemaName", schemaName);

        var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0), new ColumnInfo(
                reader.GetString(1),
                reader.GetString(2) == "YES",
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return columns;
    }

    private static async Task<bool> WorkerJobLeaseIndexExistsAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = @schemaName
                  AND tablename = 'WorkerJobs'
                  AND indexname = 'IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration'
            )
            """;
        command.Parameters.AddWithValue("schemaName", schemaName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static void AssertColumn
    (
        IReadOnlyDictionary<string, ColumnInfo> columns,
        string columnName,
        string dataType,
        bool isNullable
    )
    {
        var column = columns[columnName];
        Assert.Equal(dataType, column.DataType);
        Assert.Equal(isNullable, column.IsNullable);
    }

    private static void AssertNoPendingMigrationChanges(DavDatabaseContext dbContext)
    {
        var snapshotModel = dbContext.GetService<IModelRuntimeInitializer>().Initialize(
            dbContext.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,
            designTime: true,
            validationLogger: null);
        var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        var operations = dbContext.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), designTimeModel.GetRelationalModel())
            .ToArray();

        Assert.True(operations.Length == 0,
            $"Pending Npgsql migration operations: {string.Join("; ", operations.Select(DescribeOperation))}");
    }

    private static string DescribeOperation(MigrationOperation operation)
    {
        return operation switch
        {
            RenameIndexOperation rename => $"RenameIndex {rename.Table}.{rename.Name} to {rename.NewName}",
            AlterColumnOperation alter =>
                $"AlterColumn {alter.Table}.{alter.Name} from {alter.OldColumn.ColumnType} to {alter.ColumnType}",
            _ => operation.GetType().Name
        };
    }

    private sealed record ColumnInfo(string DataType, bool IsNullable, string? ColumnDefault);
}

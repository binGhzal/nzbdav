using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using backend.Tests.Services;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class MaintenanceRunSchemaTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public MaintenanceRunSchemaTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MigrationCreatesMaintenanceRunsAndEnforcesOneActiveSlot()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO MaintenanceRuns
                (Id, Kind, Status, ActiveSlot, RequestedBy, CreatedAt, UpdatedAt,
                 ProgressCurrent, ProgressTotal)
            VALUES
                ({0}, 0, 0, 1, 'manual', {1}, {1}, 0, NULL);
            """,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.UtcTicks);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO MaintenanceRuns
                    (Id, Kind, Status, ActiveSlot, RequestedBy, CreatedAt, UpdatedAt,
                     ProgressCurrent, ProgressTotal)
                VALUES
                    ({0}, 1, 0, 1, 'manual', {1}, {1}, 0, NULL);
                """,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.UtcTicks));

        Assert.Equal(19, error.SqliteErrorCode);
    }
}

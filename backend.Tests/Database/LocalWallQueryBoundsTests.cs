using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class LocalWallQueryBoundsTests
{
    [Fact]
    public void PostgreSqlExclusiveLowerFloorsAndInclusiveLowerCeilingsAtMicrosecondPrecision()
    {
        using var context = CreatePostgreSqlContext();
        var floor = new DateTime(2026, 7, 12, 12, 30, 0, DateTimeKind.Unspecified);
        var subMicrosecond = floor.AddTicks(9);

        Assert.Equal(floor,
            LocalWallQueryBounds.NormalizeExclusiveLowerBound(context, subMicrosecond));
        Assert.Equal(floor,
            LocalWallQueryBounds.NormalizeInclusiveUpperBound(context, subMicrosecond));
        Assert.Equal(floor.AddTicks(10),
            LocalWallQueryBounds.NormalizeInclusiveLowerBound(context, subMicrosecond));
        Assert.Equal(floor.AddTicks(10),
            LocalWallQueryBounds.NormalizeExclusiveUpperBound(context, subMicrosecond));
    }

    [Fact]
    public void SqliteBoundsPreserveEveryTickAndNormalizeKindOnly()
    {
        using var context = new DavDatabaseContext(
            new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        var instant = new DateTime(2026, 7, 12, 12, 30, 0, DateTimeKind.Utc).AddTicks(9);
        var expected = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified);

        Assert.Equal(expected,
            LocalWallQueryBounds.NormalizeExclusiveLowerBound(context, instant));
        Assert.Equal(expected,
            LocalWallQueryBounds.NormalizeInclusiveLowerBound(context, instant));
        Assert.Equal(expected,
            LocalWallQueryBounds.NormalizeExclusiveUpperBound(context, instant));
        Assert.Equal(expected,
            LocalWallQueryBounds.NormalizeInclusiveUpperBound(context, instant));
    }

    private static PostgreSqlDavDatabaseContext CreatePostgreSqlContext()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "not-opened",
            Username = "not-opened",
            Password = "not-opened",
            Timezone = TimeZoneInfo.Local.Id,
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;
        return new PostgreSqlDavDatabaseContext(
            new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
                .UseNpgsql(connectionString)
                .Options);
    }
}

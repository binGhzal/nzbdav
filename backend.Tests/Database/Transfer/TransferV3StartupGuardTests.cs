using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3StartupGuardTests
{
    public static TheoryData<string> PresentValues => new()
    {
        { "{\"formatVersion\":3,\"state\":\"fresh\"}" },
        { "{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}" },
        { "{\"formatVersion\":3,\"state\":\"database-verified\",\"manifestSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}" },
        { "{\"formatVersion\":3,\"state\":\"failed\",\"manifestSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}" },
        { "malformed" },
        { new string('x', 64 * 1024) },
    };

    [Fact]
    public async Task MissingDatabaseFileIsAllowedWithoutCreatingAnything()
    {
        using var fixture = new SqliteGuardFixture();

        await TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None);

        Assert.False(File.Exists(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
    }

    [Fact]
    public async Task EmptyAndHistoricalDatabasesWithoutConfigItemsAreAllowedUnchanged()
    {
        using (var empty = new SqliteGuardFixture())
        {
            await empty.ExecuteAsync("VACUUM;");
            var emptyBefore = File.ReadAllBytes(empty.DatabasePath);

            await TransferV3StartupGuard.EnsureAllowedAsync(empty.DatabasePath, CancellationToken.None);

            Assert.Equal(emptyBefore, File.ReadAllBytes(empty.DatabasePath));
        }

        using var historical = new SqliteGuardFixture();
        await historical.ExecuteAsync("CREATE TABLE Historical(Id INTEGER PRIMARY KEY, Value TEXT);");
        var historicalBefore = File.ReadAllBytes(historical.DatabasePath);

        await TransferV3StartupGuard.EnsureAllowedAsync(historical.DatabasePath, CancellationToken.None);

        Assert.Equal(historicalBefore, File.ReadAllBytes(historical.DatabasePath));
        Assert.Equal(0, await historical.ScalarLongAsync("SELECT COUNT(*) FROM Historical;"));
    }

    [Fact]
    public async Task CurrentDatabaseWithoutReservedMarkerIsAllowedUnchanged()
    {
        using var fixture = new SqliteGuardFixture();
        await fixture.CreateConfigItemsAsync(("ordinary.setting", "value"));
        var before = File.ReadAllBytes(fixture.DatabasePath);

        await TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal("value", await fixture.ScalarStringAsync(
            "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = 'ordinary.setting';"));
    }

    [Theory]
    [MemberData(nameof(PresentValues))]
    public async Task AnyExactReservedKeyPresenceRefusesWithoutParsingOrChangingItsValue(string value)
    {
        using var fixture = new SqliteGuardFixture();
        await fixture.CreateConfigItemsAsync((TransferV3ReservedConfigPolicy.ImportStateKey, value));
        var before = File.ReadAllBytes(fixture.DatabasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None));

        Assert.Equal(TransferV3StartupGuard.RefusalMessage, error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(value, await fixture.ScalarStringAsync(
            "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = $key;",
            ("$key", TransferV3ReservedConfigPolicy.ImportStateKey)));
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.DatabasePath, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkerCommittedOnlyToLiveWalIsNotMissed()
    {
        using var fixture = new SqliteGuardFixture();
        await using var writer = fixture.CreateConnection();
        await writer.OpenAsync();
        await using (var setup = writer.CreateCommand())
        {
            setup.CommandText =
                "PRAGMA journal_mode=WAL;"
                + "CREATE TABLE ConfigItems(ConfigName TEXT NOT NULL PRIMARY KEY, ConfigValue TEXT);"
                + "PRAGMA wal_checkpoint(TRUNCATE);";
            await setup.ExecuteNonQueryAsync();
        }

        await using (var insert = writer.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ($key, 'malformed');";
            insert.Parameters.AddWithValue("$key", TransferV3ReservedConfigPolicy.ImportStateKey);
            await insert.ExecuteNonQueryAsync();
        }

        var mainBefore = File.ReadAllBytes(fixture.DatabasePath);
        Assert.True(new FileInfo(fixture.DatabasePath + "-wal").Length > 0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None));

        Assert.Equal(TransferV3StartupGuard.RefusalMessage, error.Message);
        Assert.Equal(mainBefore, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void PresenceQueryIsBoundedAndNeverRequestsConfigValue()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer/TransferV3StartupGuard.cs"));

        Assert.Contains("SELECT 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigValue", source, StringComparison.Ordinal);
        Assert.Contains("Pooling = false", source, StringComparison.Ordinal);
        Assert.Contains("Mode = SqliteOpenMode.ReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("Cache = SqliteCacheMode.Private", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Immutable", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CREATE TABLE ConfigItems(ConfigValue TEXT);")]
    [InlineData("CREATE VIEW ConfigItems AS SELECT 'value' AS ConfigValue;")]
    public async Task IncompatibleConfigItemsSchemaFailsClosedWithRedactedDiagnostic(string schema)
    {
        using var fixture = new SqliteGuardFixture();
        await fixture.ExecuteAsync(schema);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None));

        Assert.Equal(TransferV3StartupGuard.ValidationFailureMessage, error.Message);
        Assert.DoesNotContain("ConfigName", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.DatabasePath, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeldExclusiveLockCannotExceedTheCompleteGuardBound()
    {
        using var fixture = new SqliteGuardFixture();
        await fixture.CreateConfigItemsAsync(("ordinary.setting", "value"));
        await using var blocker = fixture.CreateConnection();
        await blocker.OpenAsync();
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.CommandText = "BEGIN EXCLUSIVE;";
        await lockCommand.ExecuteNonQueryAsync();

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3StartupGuard.EnsureAllowedAsync(fixture.DatabasePath, CancellationToken.None));
        stopwatch.Stop();

        Assert.Equal(TransferV3StartupGuard.ValidationFailureMessage, error.Message);
        Assert.True(
            stopwatch.Elapsed < TransferV3StartupGuard.MaximumElapsed,
            $"Guard exceeded its full bound: {stopwatch.Elapsed}.");
    }

    private sealed class SqliteGuardFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"nzbdav-transfer-v3-startup-{Guid.NewGuid():N}");

        public SqliteGuardFixture()
        {
            Directory.CreateDirectory(_directory);
        }

        public string DatabasePath => Path.Combine(_directory, "db.sqlite");

        public SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());

        public async Task CreateConfigItemsAsync(params (string Key, string Value)[] items)
        {
            await ExecuteAsync(
                "CREATE TABLE ConfigItems(ConfigName TEXT NOT NULL PRIMARY KEY, ConfigValue TEXT);");
            foreach (var (key, value) in items)
            {
                await ExecuteAsync(
                    "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ($key, $value);",
                    ("$key", key),
                    ("$value", value));
            }
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarLongAsync(string sql)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<string> ScalarStringAsync(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return Convert.ToString(await command.ExecuteScalarAsync())!;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}

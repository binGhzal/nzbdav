using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ImportStateStoreTests
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DigestB =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    public static TheoryData<string, string, int> TransitionMatrix => new()
    {
        { "fresh", "fresh", 0 },
        { "fresh", "importing", 1 },
        { "fresh", "database-verified", 0 },
        { "fresh", "failed", 0 },
        { "importing", "fresh", 0 },
        { "importing", "importing", 0 },
        { "importing", "database-verified", 1 },
        { "importing", "failed", 1 },
        { "database-verified", "fresh", 0 },
        { "database-verified", "importing", 0 },
        { "database-verified", "database-verified", 0 },
        { "database-verified", "failed", 0 },
        { "failed", "fresh", 0 },
        { "failed", "importing", 0 },
        { "failed", "database-verified", 0 },
        { "failed", "failed", 0 },
    };

    [Theory]
    [MemberData(nameof(TransitionMatrix))]
    public async Task TryTransitionAsyncImplementsTheCompleteLegalTransitionMatrix(
        string currentName,
        string nextName,
        int expectedChangedRows)
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var current = State(currentName, DigestA);
        var next = State(nextName, DigestA);
        await database.SetTextValueAsync(TransferV3ImportStateCodec.Serialize(current));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(current, next, CancellationToken.None);

        Assert.Equal(expectedChangedRows, changedRows);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(expectedChangedRows == 1 ? next : current),
            await database.ReadValueBytesAsync());
    }

    [Theory]
    [InlineData("importing", "database-verified")]
    [InlineData("importing", "failed")]
    public async Task TryTransitionAsyncRejectsDigestSwitchesWithoutChangingBytes(
        string currentName,
        string nextName)
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var current = State(currentName, DigestA);
        var next = State(nextName, DigestB);
        var before = TransferV3ImportStateCodec.Serialize(current);
        await database.SetTextValueAsync(before);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(current, next, CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(before, await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task TryTransitionAsyncRequiresTheExactExpectedCanonicalBytes()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var actual = TransferV3ImportState.Importing(DigestA);
        var expected = TransferV3ImportState.Fresh();
        var next = TransferV3ImportState.Importing(DigestB);
        var before = TransferV3ImportStateCodec.Serialize(actual);
        await database.SetTextValueAsync(before);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(expected, next, CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(before, await database.ReadValueBytesAsync());
    }

    [Theory]
    [MemberData(nameof(NoncanonicalCurrentValues))]
    public async Task TryTransitionAsyncDoesNotMaterializeOrChangeNoncanonicalCurrentText(string rawValue)
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var before = Encoding.UTF8.GetBytes(rawValue);
        await database.SetTextValueAsync(before);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(before, await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task TryTransitionAsyncChangesZeroRowsForMissingOrDeletedState()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Null(await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task IllegalTransitionReturnsZeroWithoutCreatingOrOpeningTheDatabase()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nzbdav-transfer-v3-state-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "must-not-exist.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connectionString)
            .Options;
        try
        {
            await using var context = new DavDatabaseContext(options);
            var store = new TransferV3ImportStateStore(context);

            var changedRows = await store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Fresh(),
                CancellationToken.None);

            Assert.Equal(0, changedRows);
            Assert.False(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsBlobStorageWithoutChangingIt()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var blob = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh());
        await database.SetBlobValueAsync(blob);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal("blob", await database.ReadValueStorageTypeAsync());
        Assert.Equal(blob, await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsCollationEqualButByteDifferentKey()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync(configNameCollation: "NOCASE");
        var fresh = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh());
        await database.SetTextValueAsync(fresh, "DATABASE.IMPORT-STATE");
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(fresh, await database.ReadValueBytesAsync("DATABASE.IMPORT-STATE"));
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsCollationEqualButByteDifferentValue()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync(
            configValueCollation: "NOCASE");
        var noncanonicalFresh = "{\"formatVersion\":3,\"state\":\"FRESH\"}";
        var before = Encoding.UTF8.GetBytes(noncanonicalFresh);
        await database.SetTextValueAsync(before);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(before, await database.ReadValueBytesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFreshToImportingAttemptsHaveExactlyOneWinner(bool differentDigests)
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var contextA = database.CreateContext();
        await using var contextB = database.CreateContext();
        var storeA = new TransferV3ImportStateStore(contextA);
        var storeB = new TransferV3ImportStateStore(contextB);
        using var barrier = new Barrier(2);

        var first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await storeA.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await storeB.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(differentDigests ? DigestB : DigestA),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Sum());
        var actual = await database.ReadValueBytesAsync();
        var expectedA = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestA));
        var expectedB = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestB));
        Assert.True(actual!.SequenceEqual(expectedA) || actual.SequenceEqual(expectedB));
    }

    [Fact]
    public async Task ConstructorRejectsTrackedReservedEntityBeforeOpeningTheCallerConnection()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
            ConfigValue = TransferV3ImportStateCodec.FreshCanonicalJson,
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            new TransferV3ImportStateStore(context));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task ConstructorRejectsAnAlreadyOpenCallerConnectionWithoutClosingIt()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var callerConnection = context.Database.GetDbConnection();
        await callerConnection.OpenAsync();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new TransferV3ImportStateStore(context));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Open, callerConnection.State);
    }

    [Fact]
    public async Task TryTransitionAsyncRechecksTrackedReservedStateBeforeOwnedConnectionAccess()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        context.ConfigItems.Add(new ConfigItem
        {
            ConfigName = TransferV3ReservedConfigPolicy.ImportStateKey,
            ConfigValue = TransferV3ImportStateCodec.FreshCanonicalJson,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()),
            await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsAnEfTransactionWithoutChangingCallerState()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        await using var transaction = await context.Database.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.NotNull(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Open, context.Database.GetDbConnection().State);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()),
            await database.ReadValueBytesAsync());
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsRawCallerTransactionAndLeavesItOpenAndUsable()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        var callerConnection = (SqliteConnection)context.Database.GetDbConnection();
        await callerConnection.OpenAsync();
        await using var transaction = await callerConnection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Open, callerConnection.State);
        await using var command = callerConnection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsAlreadyOpenCallerConnectionWithoutClosingIt()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        var callerConnection = context.Database.GetDbConnection();
        await callerConnection.OpenAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Open, callerConnection.State);
    }

    [Fact]
    public async Task TryTransitionAsyncRejectsAmbientTransactionScopeBeforeDatabaseAccess()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        using var scope = new TransactionScope(
            TransactionScopeOption.Required,
            TransactionScopeAsyncFlowOption.Enabled);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SuccessfulTransitionIsImmediatelyVisibleFromANewConnection()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh()));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(1, changedRows);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestA)),
            await database.ReadValueBytesAsync());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SqliteLockContentionTerminatesWithinTheDeclaredFullInvocationBound()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var fresh = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh());
        await database.SetTextValueAsync(fresh);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        await using var blocker = new SqliteConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE";
        await lockCommand.ExecuteNonQueryAsync();
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        stopwatch.Stop();
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.Zero,
            TransferV3ImportStateStore.SqliteFullInvocationTimeoutUpperBound);
        Assert.DoesNotContain(DigestA, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(fresh, await database.ReadValueBytesAsync());
        await using var rollback = blocker.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CancellationIsObservedBetweenBoundedSqliteBusyRetries()
    {
        await using var database = await OwnedSqliteStateDatabase.CreateAsync();
        var fresh = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh());
        await database.SetTextValueAsync(fresh);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        await using var blocker = new SqliteConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE";
        await lockCommand.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                cancellation.Token));

        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Equal(fresh, await database.ReadValueBytesAsync());
        await using var rollback = blocker.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
    }

    [Fact]
    public void StoreExposesOnlyCasAndUsesParameterizedExactByteSql()
    {
        var declaredMethods = typeof(TransferV3ImportStateStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == typeof(TransferV3ImportStateStore))
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(nameof(TransferV3ImportStateStore.TryTransitionAsync), declaredMethods);
        Assert.DoesNotContain(declaredMethods, name =>
            name.Contains("Read", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GetState", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("typeof(\"ConfigName\") = 'text'", TransferV3ImportStateStore.SqliteCasSql);
        Assert.Contains("CAST(\"ConfigName\" AS BLOB) = @key", TransferV3ImportStateStore.SqliteCasSql);
        Assert.Contains("typeof(\"ConfigValue\") = 'text'", TransferV3ImportStateStore.SqliteCasSql);
        Assert.Contains("CAST(\"ConfigValue\" AS BLOB) = @expected", TransferV3ImportStateStore.SqliteCasSql);
        Assert.Contains("@next", TransferV3ImportStateStore.SqliteCasSql);
        Assert.DoesNotContain(DigestA, TransferV3ImportStateStore.SqliteCasSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", TransferV3ImportStateStore.SqliteCasSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", TransferV3ImportStateStore.SqliteCasSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN", TransferV3ImportStateStore.SqliteCasSql, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> NoncanonicalCurrentValues => new()
    {
        "",
        " {\"formatVersion\":3,\"state\":\"fresh\"}",
        "{\"state\":\"fresh\",\"formatVersion\":3}",
        "{\"formatVersion\":3,\"state\":\"fresh\",\"unknown\":true}",
        new string('x', TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes + 100_000),
    };

    private static TransferV3ImportState State(string name, string digest) => name switch
    {
        "fresh" => TransferV3ImportState.Fresh(),
        "importing" => TransferV3ImportState.Importing(digest),
        "database-verified" => TransferV3ImportState.DatabaseVerified(digest),
        "failed" => TransferV3ImportState.Failed(digest),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    internal sealed class OwnedSqliteStateDatabase : IAsyncDisposable
    {
        private OwnedSqliteStateDatabase(string directory, string databasePath)
        {
            Directory = directory;
            DatabasePath = databasePath;
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
        }

        private string Directory { get; }
        private string DatabasePath { get; }
        internal string ConnectionString { get; }

        internal static async Task<OwnedSqliteStateDatabase> CreateAsync(
            string configNameCollation = "BINARY",
            string configValueCollation = "BINARY")
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "nzbdav-transfer-v3-state-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var database = new OwnedSqliteStateDatabase(
                directory,
                Path.Combine(directory, "state.sqlite"));
            await using var connection = new SqliteConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE "ConfigItems" (
                    "ConfigName" TEXT COLLATE {configNameCollation} NOT NULL PRIMARY KEY,
                    "ConfigValue" TEXT COLLATE {configValueCollation} NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            return database;
        }

        internal DavDatabaseContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new DavDatabaseContext(options);
        }

        internal Task SetTextValueAsync(
            byte[] value,
            string key = TransferV3ReservedConfigPolicy.ImportStateKey) =>
            SetValueAsync(Encoding.UTF8.GetString(value), key);

        internal Task SetBlobValueAsync(
            byte[] value,
            string key = TransferV3ReservedConfigPolicy.ImportStateKey) =>
            SetValueAsync(value, key);

        private async Task SetValueAsync(object value, string key)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT OR REPLACE INTO \"ConfigItems\" (\"ConfigName\", \"ConfigValue\") VALUES (@key, @value)";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<byte[]?> ReadValueBytesAsync(
            string key = TransferV3ReservedConfigPolicy.ImportStateKey)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CAST(\"ConfigValue\" AS BLOB) FROM \"ConfigItems\" WHERE \"ConfigName\" = @key";
            command.Parameters.AddWithValue("@key", key);
            return await command.ExecuteScalarAsync() as byte[];
        }

        internal async Task<string?> ReadValueStorageTypeAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT typeof(\"ConfigValue\") FROM \"ConfigItems\" WHERE \"ConfigName\" = @key";
            command.Parameters.AddWithValue("@key", TransferV3ReservedConfigPolicy.ImportStateKey);
            return (string?)await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}

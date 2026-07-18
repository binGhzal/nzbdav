using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;
using backend.Tests.Database;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ImportStateStorePostgreSqlTests
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DigestB =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [PostgreSqlFact]
    public async Task PostgreSqlCasUsesExactBytesAndIsVisibleFromANewConnection()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_cas");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(1, changedRows);
        Assert.Equal(
            Utf8String(TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestA))),
            await ReadStateAsync(schema));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPostgreSqlFreshTransitionsHaveExactlyOneWinner(
        bool differentDigests)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_race");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var contextA = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        await using var contextB = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
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
        var stored = await ReadStateAsync(schema);
        Assert.Contains(stored, new[]
        {
            Utf8String(TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestA))),
            Utf8String(TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestB))),
        });
    }

    [PostgreSqlFact]
    public async Task PostgreSqlCasRejectsCollationEqualButByteDifferentReservedKey()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_collation");
        await schema.ExecuteAsync(
            """
            CREATE COLLATION state_nondeterministic (
                provider = icu,
                locale = 'und-u-ks-level2',
                deterministic = false
            );
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE state_nondeterministic PRIMARY KEY,
                "ConfigValue" text NOT NULL
            );
            """);
        await SetStateAsync(
            schema,
            TransferV3ImportStateCodec.FreshCanonicalJson,
            "DATABASE.IMPORT-STATE");
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(
            TransferV3ImportStateCodec.FreshCanonicalJson,
            await ReadStateAsync(schema, "DATABASE.IMPORT-STATE"));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlCasRejectsCollationEqualButByteDifferentExpectedValue()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_value_collation");
        await schema.ExecuteAsync(
            """
            CREATE COLLATION state_nondeterministic (
                provider = icu,
                locale = 'und-u-ks-level2',
                deterministic = false
            );
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE "C" PRIMARY KEY,
                "ConfigValue" text COLLATE state_nondeterministic NOT NULL
            );
            """);
        const string noncanonicalFresh = "{\"formatVersion\":3,\"state\":\"FRESH\"}";
        await SetStateAsync(schema, noncanonicalFresh);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var store = new TransferV3ImportStateStore(context);

        var changedRows = await store.TryTransitionAsync(
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(noncanonicalFresh, await ReadStateAsync(schema));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlHeldRowLockTerminatesWithinPositiveCommandTimeout()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_lock");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var store = new TransferV3ImportStateStore(context);
        await using var blocker = await schema.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText =
                "SELECT 1 FROM \"ConfigItems\" WHERE \"ConfigName\" = @key FOR UPDATE";
            lockCommand.Parameters.AddWithValue("@key", TransferV3ReservedConfigPolicy.ImportStateKey);
            await lockCommand.ExecuteScalarAsync();
        }

        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));
        stopwatch.Stop();

        Assert.True(TransferV3ImportStateStore.PostgreSqlCommandTimeoutSeconds > 0);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        var npgsqlError = Assert.IsAssignableFrom<NpgsqlException>(error);
        Assert.True(
            npgsqlError.InnerException is TimeoutException or OperationCanceledException
            || npgsqlError is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled },
            $"Unexpected PostgreSQL timeout shape: {npgsqlError.GetType().FullName} / " +
            $"{npgsqlError.InnerException?.GetType().FullName ?? "no inner exception"}.");
        Assert.DoesNotContain(DigestA, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(TransferV3ImportStateCodec.FreshCanonicalJson, await ReadStateAsync(schema));
        await blockerTransaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TransactionBoundReadAndCasStayInsideCallerTransactionUntilRollback()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_bound");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var initial = await TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
            connection,
            transaction,
            commandTimeoutSeconds: 2,
            CancellationToken.None);
        var changedRows = await TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
            connection,
            transaction,
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            commandTimeoutSeconds: 2,
            CancellationToken.None);
        var changed = await TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
            connection,
            transaction,
            commandTimeoutSeconds: 2,
            CancellationToken.None);

        Assert.Equal(TransferV3ImportState.Fresh(), initial);
        Assert.Equal(1, changedRows);
        Assert.Equal(TransferV3ImportState.Importing(DigestA), changed);
        Assert.Equal(
            TransferV3ImportStateCodec.FreshCanonicalJson,
            await ReadStateAsync(schema));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);

        await transaction.RollbackAsync();

        Assert.Equal(
            TransferV3ImportStateCodec.FreshCanonicalJson,
            await ReadStateAsync(schema));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [PostgreSqlFact]
    public async Task TransactionBoundCasReturnsExactMismatchAndDuplicateCounts()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_counts");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE "C" NOT NULL,
                "ConfigValue" text NOT NULL
            )
            """);
        var importingA = Utf8String(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Importing(DigestA)));
        await SetStateAsync(schema, importingA);
        await SetStateAsync(schema, importingA);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var mismatchRows =
            await TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                connection,
                transaction,
                TransferV3ImportState.Importing(DigestB),
                TransferV3ImportState.DatabaseVerified(DigestB),
                commandTimeoutSeconds: 2,
                CancellationToken.None);
        var duplicateRows =
            await TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                connection,
                transaction,
                TransferV3ImportState.Importing(DigestA),
                TransferV3ImportState.DatabaseVerified(DigestA),
                commandTimeoutSeconds: 2,
                CancellationToken.None);

        Assert.Equal(0, mismatchRows);
        Assert.Equal(2, duplicateRows);
        Assert.Equal(importingA, await ReadStateAsync(schema));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);

        await transaction.RollbackAsync();

        Assert.Equal(importingA, await ReadStateAsync(schema));
    }

    [PostgreSqlTheory]
    [InlineData("missing")]
    [InlineData("non-text-key")]
    [InlineData("non-text-value")]
    [InlineData("byte-different-key")]
    [InlineData("byte-different-value")]
    public async Task TransactionBoundCasRejectsMissingTypeDriftAndByteDifferentRows(
        string scenario)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_cas_bad");
        var expectedKey = TransferV3ReservedConfigPolicy.ImportStateKey;
        string? expectedValue = TransferV3ImportStateCodec.FreshCanonicalJson;
        switch (scenario)
        {
            case "missing":
                await CreateStateTableAsync(schema);
                expectedValue = null;
                break;
            case "non-text-key":
                await schema.ExecuteAsync(
                    """
                    CREATE TABLE "ConfigItems" (
                        "ConfigName" character varying(128) COLLATE "C" PRIMARY KEY,
                        "ConfigValue" text NOT NULL
                    )
                    """);
                await SetStateAsync(schema, expectedValue);
                break;
            case "non-text-value":
                await schema.ExecuteAsync(
                    """
                    CREATE TABLE "ConfigItems" (
                        "ConfigName" text COLLATE "C" PRIMARY KEY,
                        "ConfigValue" character varying(256) NOT NULL
                    )
                    """);
                await SetStateAsync(schema, expectedValue);
                break;
            case "byte-different-key":
                await CreateNondeterministicStateTableAsync(schema, nondeterministicKey: true);
                expectedKey = "DATABASE.IMPORT-STATE";
                await SetStateAsync(schema, expectedValue, expectedKey);
                break;
            case "byte-different-value":
                await CreateNondeterministicStateTableAsync(schema, nondeterministicKey: false);
                expectedValue = "{\"formatVersion\":3,\"state\":\"FRESH\"}";
                await SetStateAsync(schema, expectedValue);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var changedRows = await TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
            connection,
            transaction,
            TransferV3ImportState.Fresh(),
            TransferV3ImportState.Importing(DigestA),
            commandTimeoutSeconds: 2,
            CancellationToken.None);

        Assert.Equal(0, changedRows);
        Assert.Equal(expectedValue, await ReadStateAsync(schema, expectedKey));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TransactionBoundCasRefusesIncompatibleTypeDriftWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_type_bad");
        await schema.ExecuteAsync(
            """
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE "C" PRIMARY KEY,
                "ConfigValue" integer NOT NULL
            );
            INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
            VALUES ('database.import-state', 7)
            """);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                connection,
                transaction,
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                commandTimeoutSeconds: 2,
                CancellationToken.None));

        Assert.Contains(
            error.SqlState,
            new[] { PostgresErrorCodes.UndefinedFunction, PostgresErrorCodes.DatatypeMismatch });
        Assert.DoesNotContain(DigestA, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
        Assert.Equal(
            7,
            await schema.ScalarAsync<int>(
                "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE \"ConfigName\" = 'database.import-state'"));
    }

    [PostgreSqlTheory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("non-text-key")]
    [InlineData("non-text-value")]
    [InlineData("byte-different-key")]
    [InlineData("byte-different-value")]
    [InlineData("noncanonical")]
    [InlineData("oversized")]
    [InlineData("state-invariant")]
    public async Task TransactionBoundReadRejectsMalformedCardinalityStorageAndBytes(
        string scenario)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_read_bad");
        switch (scenario)
        {
            case "missing":
                await CreateStateTableAsync(schema);
                break;
            case "duplicate":
                await schema.ExecuteAsync(
                    """
                    CREATE TABLE "ConfigItems" (
                        "ConfigName" text COLLATE "C" NOT NULL,
                        "ConfigValue" text NOT NULL
                    )
                    """);
                await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
                await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
                break;
            case "non-text-key":
                await schema.ExecuteAsync(
                    """
                    CREATE TABLE "ConfigItems" (
                        "ConfigName" character varying(128) COLLATE "C" PRIMARY KEY,
                        "ConfigValue" text NOT NULL
                    )
                    """);
                await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
                break;
            case "non-text-value":
                await schema.ExecuteAsync(
                    """
                    CREATE TABLE "ConfigItems" (
                        "ConfigName" text COLLATE "C" PRIMARY KEY,
                        "ConfigValue" character varying(256) NOT NULL
                    )
                    """);
                await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
                break;
            case "byte-different-key":
                await CreateNondeterministicStateTableAsync(schema, nondeterministicKey: true);
                await SetStateAsync(
                    schema,
                    TransferV3ImportStateCodec.FreshCanonicalJson,
                    "DATABASE.IMPORT-STATE");
                break;
            case "byte-different-value":
                await CreateNondeterministicStateTableAsync(schema, nondeterministicKey: false);
                await SetStateAsync(schema, "{\"formatVersion\":3,\"state\":\"FRESH\"}");
                break;
            case "noncanonical":
                await CreateStateTableAsync(schema);
                await SetStateAsync(schema, "{\"state\":\"fresh\",\"formatVersion\":3}");
                break;
            case "oversized":
                await CreateStateTableAsync(schema);
                await SetStateAsync(
                    schema,
                    new string('x', TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes + 1));
                break;
            case "state-invariant":
                await CreateStateTableAsync(schema);
                await SetStateAsync(
                    schema,
                    $"{{\"formatVersion\":3,\"state\":\"fresh\",\"manifestSha256\":\"{DigestA}\"}}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<FormatException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                connection,
                transaction,
                commandTimeoutSeconds: 2,
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.PostgreSqlReadFailureMessage, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        await AssertTransactionIsUsableAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TransactionBoundOperationsRejectClosedConnectionWithoutOpeningIt()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_closed");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.CloseAsync();

        var casError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                connection,
                transaction,
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                commandTimeoutSeconds: 2,
                CancellationToken.None));
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                connection,
                transaction,
                commandTimeoutSeconds: 2,
                CancellationToken.None));

        AssertPostgreSqlTransactionFailure(casError);
        AssertPostgreSqlTransactionFailure(readError);
        Assert.Equal(ConnectionState.Closed, connection.State);
        await connection.OpenAsync();
        await AssertConnectionIsUsableAsync(connection);
    }

    [PostgreSqlFact]
    public async Task TransactionBoundOperationsRejectForeignTransactionAndLeaveBothConnectionsUsable()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_foreign");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var ownerConnection = await schema.OpenConnectionAsync();
        await using var ownerTransaction = await ownerConnection.BeginTransactionAsync();
        await using var foreignConnection = await schema.OpenConnectionAsync();

        var casError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                foreignConnection,
                ownerTransaction,
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                commandTimeoutSeconds: 2,
                CancellationToken.None));
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                foreignConnection,
                ownerTransaction,
                commandTimeoutSeconds: 2,
                CancellationToken.None));

        AssertPostgreSqlTransactionFailure(casError);
        AssertPostgreSqlTransactionFailure(readError);
        Assert.Equal(ConnectionState.Open, ownerConnection.State);
        Assert.Equal(ConnectionState.Open, foreignConnection.State);
        Assert.Same(ownerConnection, ownerTransaction.Connection);
        await AssertTransactionIsUsableAsync(ownerConnection, ownerTransaction);
        await AssertConnectionIsUsableAsync(foreignConnection);
        await ownerTransaction.RollbackAsync();
    }

    [PostgreSqlFact]
    public async Task TransactionBoundOperationsRejectCommittedUndisposedTransaction()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_committed");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.CommitAsync();

        var casError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                connection,
                transaction,
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                commandTimeoutSeconds: 2,
                CancellationToken.None));
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                connection,
                transaction,
                commandTimeoutSeconds: 2,
                CancellationToken.None));

        AssertPostgreSqlTransactionFailure(casError);
        AssertPostgreSqlTransactionFailure(readError);
        Assert.Equal(ConnectionState.Open, connection.State);
        await AssertConnectionIsUsableAsync(connection);
    }

    [PostgreSqlFact]
    public async Task TransactionBoundReadHonorsRowLockTimeoutAndLeavesResourcesCallerOwned()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_tx_read_lock");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var blocker = await schema.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText =
                "SELECT 1 FROM \"ConfigItems\" WHERE \"ConfigName\" = @key FOR UPDATE";
            lockCommand.Parameters.AddWithValue(
                "@key",
                TransferV3ReservedConfigPolicy.ImportStateKey);
            await lockCommand.ExecuteScalarAsync();
        }

        await using var readerConnection = await schema.OpenConnectionAsync();
        await using var readerTransaction = await readerConnection.BeginTransactionAsync();
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                readerConnection,
                readerTransaction,
                commandTimeoutSeconds: 1,
                CancellationToken.None));

        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        AssertPostgreSqlTimeout(error);
        Assert.IsNotType<FormatException>(error);
        Assert.Equal(ConnectionState.Open, readerConnection.State);
        Assert.Same(readerConnection, readerTransaction.Connection);
        await readerTransaction.RollbackAsync();
        await blockerTransaction.RollbackAsync();
        Assert.Equal(
            TransferV3ImportStateCodec.FreshCanonicalJson,
            await ReadStateAsync(schema));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlRawCallerTransactionIsRejectedAndLeftUsable()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("transfer_state_caller_tx");
        await CreateStateTableAsync(schema);
        await SetStateAsync(schema, TransferV3ImportStateCodec.FreshCanonicalJson);
        await using var context = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var store = new TransferV3ImportStateStore(context);
        var callerConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        await callerConnection.OpenAsync();
        await using var callerTransaction = await callerConnection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        Assert.Equal(TransferV3ImportStateStore.UnsafeCallerStateMessage, error.Message);
        Assert.Equal(ConnectionState.Open, callerConnection.State);
        await using var command = callerConnection.CreateCommand();
        command.Transaction = callerTransaction;
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync());
        await callerTransaction.RollbackAsync();
    }

    [Fact]
    public async Task UnreachablePostgreSqlEndpointUsesCappedPositiveConnectionTimeout()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=nzbdav;Username=nzbdav;" +
                "Password=not-used;Timeout=30;Command Timeout=30;Pooling=false")
            .Options;
        await using var context = new PostgreSqlDavDatabaseContext(options);
        var store = new TransferV3ImportStateStore(context);
        var ownedConnectionStringField = typeof(TransferV3ImportStateStore).GetField(
            "_ownedConnectionString",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownedConnectionStringField);
        var ownedConnectionString = Assert.IsType<string>(ownedConnectionStringField.GetValue(store));
        var ownedBuilder = new NpgsqlConnectionStringBuilder(ownedConnectionString);
        Assert.False(ownedBuilder.Pooling);
        Assert.False(ownedBuilder.Enlist);
        Assert.Equal(
            TransferV3ImportStateStore.PostgreSqlConnectionTimeoutSeconds,
            ownedBuilder.Timeout);
        Assert.Equal(
            TransferV3ImportStateStore.PostgreSqlCommandTimeoutSeconds,
            ownedBuilder.CommandTimeout);
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            store.TryTransitionAsync(
                TransferV3ImportState.Fresh(),
                TransferV3ImportState.Importing(DigestA),
                CancellationToken.None));

        stopwatch.Stop();
        Assert.True(TransferV3ImportStateStore.PostgreSqlConnectionTimeoutSeconds > 0);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(DigestA, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlCasSqlIsParameterizedAndByteExact()
    {
        Assert.Contains("\"ConfigName\" = @keyText", TransferV3ImportStateStore.PostgreSqlCasSql);
        Assert.Contains(
            "convert_to(\"ConfigName\", 'UTF8') = @key",
            TransferV3ImportStateStore.PostgreSqlCasSql);
        Assert.Contains(
            "convert_to(\"ConfigValue\", 'UTF8') = @expected",
            TransferV3ImportStateStore.PostgreSqlCasSql);
        Assert.Contains("convert_from(@next, 'UTF8')", TransferV3ImportStateStore.PostgreSqlCasSql);
        Assert.DoesNotContain(
            TransferV3ReservedConfigPolicy.ImportStateKey,
            TransferV3ImportStateStore.PostgreSqlCasSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(DigestA, TransferV3ImportStateStore.PostgreSqlCasSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", TransferV3ImportStateStore.PostgreSqlCasSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", TransferV3ImportStateStore.PostgreSqlCasSql, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CreateStateTableAsync(PostgreSqlTestSchema schema)
    {
        await schema.ExecuteAsync(
            """
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE "C" PRIMARY KEY,
                "ConfigValue" text NOT NULL
            )
            """);
    }

    private static async Task CreateNondeterministicStateTableAsync(
        PostgreSqlTestSchema schema,
        bool nondeterministicKey)
    {
        await schema.ExecuteAsync(
            $"""
            CREATE COLLATION state_nondeterministic (
                provider = icu,
                locale = 'und-u-ks-level2',
                deterministic = false
            );
            CREATE TABLE "ConfigItems" (
                "ConfigName" text COLLATE {(nondeterministicKey ? "state_nondeterministic" : "\"C\"")} PRIMARY KEY,
                "ConfigValue" text COLLATE {(nondeterministicKey ? "\"C\"" : "state_nondeterministic")} NOT NULL
            );
            """);
    }

    private static async Task SetStateAsync(
        PostgreSqlTestSchema schema,
        string value,
        string key = TransferV3ReservedConfigPolicy.ImportStateKey)
    {
        await using var connection = await schema.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"ConfigItems\" (\"ConfigName\", \"ConfigValue\") VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadStateAsync(
        PostgreSqlTestSchema schema,
        string key = TransferV3ReservedConfigPolicy.ImportStateKey)
    {
        await using var connection = await schema.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"ConfigValue\" FROM \"ConfigItems\" WHERE convert_to(\"ConfigName\", 'UTF8') = convert_to(@key, 'UTF8')";
        command.Parameters.AddWithValue("@key", key);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task AssertTransactionIsUsableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    private static async Task AssertConnectionIsUsableAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    private static void AssertPostgreSqlTransactionFailure(InvalidOperationException error)
    {
        Assert.Equal(TransferV3ImportStateStore.PostgreSqlTransactionFailureMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    private static void AssertPostgreSqlTimeout(Exception error)
    {
        var npgsqlError = Assert.IsAssignableFrom<NpgsqlException>(error);
        Assert.True(
            npgsqlError.InnerException is TimeoutException or OperationCanceledException
            || npgsqlError is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled },
            $"Unexpected PostgreSQL timeout shape: {npgsqlError.GetType().FullName} / " +
            $"{npgsqlError.InnerException?.GetType().FullName ?? "no inner exception"}.");
    }

    private static string Utf8String(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}

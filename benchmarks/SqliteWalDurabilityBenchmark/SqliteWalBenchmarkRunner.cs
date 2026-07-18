using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database;

namespace NzbWebDAV.Benchmarks.SqliteWal;

public static class SqliteWalBenchmarkRunner
{
    public static async Task<SqliteWalBenchmarkReport> RunAsync(
        SqliteWalBenchmarkConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        var runtime = await SqliteRuntimeGate.ReadLoadedRuntimeAsync(cancellationToken).ConfigureAwait(false);
        SqliteRuntimeGate.Validate(runtime);

        var startedAtUtc = DateTimeOffset.UtcNow;
        var temporaryParent = Path.GetFullPath(configuration.TemporaryRoot ?? Path.GetTempPath());
        Directory.CreateDirectory(temporaryParent);
        var runDirectory = Path.Combine(
            temporaryParent,
            $"nzbdav-sqlite-wal-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var executionOrder = new List<string>(configuration.Rounds);
        var roundResults = new List<RoundResult>(configuration.Rounds * 2);

        try
        {
            for (var round = 0; round < configuration.Rounds; round++)
            {
                var modes = round % 2 == 0
                    ? new[] { SqliteSynchronousMode.Normal, SqliteSynchronousMode.Full }
                    : new[] { SqliteSynchronousMode.Full, SqliteSynchronousMode.Normal };
                executionOrder.Add(
                    $"round-{round + 1}:{string.Join(',', modes.Select(mode => mode.ToString().ToUpperInvariant()))}");

                foreach (var mode in modes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var databasePath = Path.Combine(
                        runDirectory,
                        $"round-{round + 1}-{mode.ToString().ToLowerInvariant()}.db");
                    roundResults.Add(RunRound(databasePath, mode, configuration, cancellationToken));
                }
            }

            var summaries = Enum.GetValues<SqliteSynchronousMode>()
                .Select(mode => Summarize(mode, roundResults, configuration))
                .ToArray();
            var host = new SqliteWalBenchmarkHost(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount,
                temporaryParent,
                ReadVolumeFormat(temporaryParent));

            return new SqliteWalBenchmarkReport(
                startedAtUtc,
                DateTimeOffset.UtcNow,
                runtime,
                host,
                configuration with { TemporaryRoot = temporaryParent },
                executionOrder,
                summaries);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(runDirectory))
                Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static RoundResult RunRound(
        string databasePath,
        SqliteSynchronousMode mode,
        SqliteWalBenchmarkConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();

        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 30000;");
        var journalMode = Convert.ToString(ExecuteScalar(connection, "PRAGMA journal_mode = WAL;"))
            ?.ToLowerInvariant() ?? string.Empty;
        if (!string.Equals(journalMode, "wal", StringComparison.Ordinal))
            throw new InvalidOperationException($"SQLite did not enter WAL mode; reported '{journalMode}'.");

        ExecuteNonQuery(connection, $"PRAGMA synchronous = {mode.ToString().ToUpperInvariant()};");
        var synchronousPragma = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA synchronous;"));
        if (synchronousPragma != (int)mode)
        {
            throw new InvalidOperationException(
                $"SQLite did not apply synchronous={mode}; reported {synchronousPragma}.");
        }

        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, "PRAGMA wal_autocheckpoint = 1000;");
        CreateSchema(connection);

        using var insertCommand = CreateInsertCommand(connection);
        using var updateCommand = CreateUpdateCommand(connection);
        var payload = Enumerable.Range(0, 128).Select(index => (byte)index).ToArray();

        for (var transactionIndex = 0;
             transactionIndex < configuration.WarmupTransactions;
             transactionIndex++)
        {
            ExecuteWorkloadTransaction(
                connection,
                insertCommand,
                updateCommand,
                payload,
                transactionIndex,
                configuration.BatchSize);
        }

        ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");

        var samples = new double[configuration.MeasuredTransactions];
        var elapsed = Stopwatch.StartNew();
        for (var measuredIndex = 0;
             measuredIndex < configuration.MeasuredTransactions;
             measuredIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transactionIndex = configuration.WarmupTransactions + measuredIndex;
            var started = Stopwatch.GetTimestamp();
            ExecuteWorkloadTransaction(
                connection,
                insertCommand,
                updateCommand,
                payload,
                transactionIndex,
                configuration.BatchSize);
            samples[measuredIndex] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        elapsed.Stop();

        return new RoundResult(
            mode,
            journalMode,
            synchronousPragma,
            samples,
            elapsed.Elapsed.TotalMilliseconds,
            File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0,
            File.Exists(databasePath + "-wal") ? new FileInfo(databasePath + "-wal").Length : 0);
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE benchmark_queue_events (
                event_id INTEGER PRIMARY KEY,
                queue_item_id INTEGER NOT NULL,
                state TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                created_at_unix_ms INTEGER NOT NULL,
                payload BLOB NOT NULL
            );
            CREATE INDEX ix_benchmark_queue_events_queue_item
                ON benchmark_queue_events(queue_item_id);
            CREATE TABLE benchmark_worker_state (
                lane TEXT PRIMARY KEY,
                last_event_id INTEGER NOT NULL,
                lease_epoch INTEGER NOT NULL
            ) WITHOUT ROWID;
            INSERT INTO benchmark_worker_state(lane, last_event_id, lease_epoch)
                VALUES ('queue', 0, 0);
            """);
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO benchmark_queue_events(
                event_id, queue_item_id, state, attempt, created_at_unix_ms, payload)
            VALUES ($eventId, $queueItemId, $state, $attempt, $createdAtUnixMs, $payload);
            """;
        command.Parameters.Add("$eventId", SqliteType.Integer);
        command.Parameters.Add("$queueItemId", SqliteType.Integer);
        command.Parameters.Add("$state", SqliteType.Text);
        command.Parameters.Add("$attempt", SqliteType.Integer);
        command.Parameters.Add("$createdAtUnixMs", SqliteType.Integer);
        command.Parameters.Add("$payload", SqliteType.Blob);
        command.Prepare();
        return command;
    }

    private static SqliteCommand CreateUpdateCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE benchmark_worker_state
            SET last_event_id = $lastEventId,
                lease_epoch = lease_epoch + 1
            WHERE lane = 'queue';
            """;
        command.Parameters.Add("$lastEventId", SqliteType.Integer);
        command.Prepare();
        return command;
    }

    private static void ExecuteWorkloadTransaction(
        SqliteConnection connection,
        SqliteCommand insertCommand,
        SqliteCommand updateCommand,
        byte[] payload,
        int transactionIndex,
        int batchSize)
    {
        using var transaction = connection.BeginTransaction();
        insertCommand.Transaction = transaction;
        updateCommand.Transaction = transaction;

        var firstEventId = ((long)transactionIndex * batchSize) + 1;
        for (var rowIndex = 0; rowIndex < batchSize; rowIndex++)
        {
            var eventId = firstEventId + rowIndex;
            insertCommand.Parameters["$eventId"].Value = eventId;
            insertCommand.Parameters["$queueItemId"].Value = transactionIndex;
            insertCommand.Parameters["$state"].Value = rowIndex % 2 == 0 ? "ready" : "claimed";
            insertCommand.Parameters["$attempt"].Value = rowIndex;
            insertCommand.Parameters["$createdAtUnixMs"].Value = eventId;
            insertCommand.Parameters["$payload"].Value = payload;
            insertCommand.ExecuteNonQuery();
        }

        updateCommand.Parameters["$lastEventId"].Value = firstEventId + batchSize - 1;
        updateCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SqliteWalModeResult Summarize(
        SqliteSynchronousMode mode,
        IReadOnlyCollection<RoundResult> roundResults,
        SqliteWalBenchmarkConfiguration configuration)
    {
        var selected = roundResults.Where(result => result.Mode == mode).ToArray();
        var samples = selected.SelectMany(result => result.SamplesMilliseconds).ToArray();
        var elapsedMilliseconds = selected.Sum(result => result.ElapsedMilliseconds);
        var measuredTransactions = samples.Length;
        var operationsPerTransaction = configuration.BatchSize + 1;
        var seconds = elapsedMilliseconds / 1_000;

        return new SqliteWalModeResult(
            mode,
            selected.Select(result => result.JournalMode).Distinct(StringComparer.Ordinal).Single(),
            selected.Select(result => result.SynchronousPragma).Distinct().Single(),
            measuredTransactions,
            operationsPerTransaction,
            elapsedMilliseconds,
            measuredTransactions / seconds,
            (measuredTransactions * operationsPerTransaction) / seconds,
            PercentileCalculator.Calculate(samples, 0.50),
            PercentileCalculator.Calculate(samples, 0.95),
            PercentileCalculator.Calculate(samples, 0.99),
            selected.Max(result => result.DatabaseBytes),
            selected.Max(result => result.WalBytesBeforeClose));
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadVolumeFormat(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(candidate => fullPath.StartsWith(
                    candidate.RootDirectory.FullName,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive?.DriveFormat ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private sealed record RoundResult(
        SqliteSynchronousMode Mode,
        string JournalMode,
        int SynchronousPragma,
        IReadOnlyCollection<double> SamplesMilliseconds,
        double ElapsedMilliseconds,
        long DatabaseBytes,
        long WalBytesBeforeClose);
}

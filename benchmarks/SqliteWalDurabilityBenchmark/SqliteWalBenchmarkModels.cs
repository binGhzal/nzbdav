using NzbWebDAV.Database;

namespace NzbWebDAV.Benchmarks.SqliteWal;

public enum SqliteSynchronousMode
{
    Normal = 1,
    Full = 2
}

public sealed record SqliteWalBenchmarkConfiguration(
    int MeasuredTransactions = 2_000,
    int WarmupTransactions = 200,
    int BatchSize = 8,
    int Rounds = 4,
    string? TemporaryRoot = null)
{
    public void Validate()
    {
        if (MeasuredTransactions <= 0)
            throw new ArgumentOutOfRangeException(nameof(MeasuredTransactions), "Measured transactions must be positive.");
        if (WarmupTransactions < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupTransactions), "Warmup transactions cannot be negative.");
        if (BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "Batch size must be positive.");
        if (Rounds <= 0)
            throw new ArgumentOutOfRangeException(nameof(Rounds), "Rounds must be positive.");
    }
}

public sealed record SqliteWalBenchmarkHost(
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    int LogicalProcessorCount,
    string TemporaryRoot,
    string VolumeFormat);

public sealed record SqliteWalModeResult(
    SqliteSynchronousMode Mode,
    string JournalMode,
    int SynchronousPragma,
    int MeasuredTransactions,
    int OperationsPerTransaction,
    double ElapsedMilliseconds,
    double TransactionsPerSecond,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long MaximumDatabaseBytes,
    long MaximumWalBytesBeforeClose);

public sealed record SqliteWalBenchmarkReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    SqliteRuntimeInfo Runtime,
    SqliteWalBenchmarkHost Host,
    SqliteWalBenchmarkConfiguration Configuration,
    IReadOnlyList<string> ExecutionOrder,
    IReadOnlyList<SqliteWalModeResult> Modes);

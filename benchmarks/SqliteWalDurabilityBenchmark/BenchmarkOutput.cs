using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Benchmarks.SqliteWal;

public static class BenchmarkOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string FormatJson(SqliteWalBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string FormatText(SqliteWalBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var output = new StringBuilder();
        output.AppendLine("NZBDav SQLite WAL synchronous NORMAL vs FULL benchmark");
        output.AppendLine(FormattableString.Invariant($"SQLite: {report.Runtime.Version}"));
        output.AppendLine($"Source ID: {report.Runtime.SourceId}");
        output.AppendLine($"Host: {report.Host.OperatingSystem}; {report.Host.ProcessArchitecture}; {report.Host.Framework}");
        output.AppendLine(FormattableString.Invariant(
            $"Temp storage: {report.Host.TemporaryRoot} (volume format: {report.Host.VolumeFormat})"));
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "Workload: {0} rounds; {1} measured + {2} warmup transactions per mode/round; "
            + "{3} inserts + 1 coordination update per transaction{4}",
            report.Configuration.Rounds,
            report.Configuration.MeasuredTransactions,
            report.Configuration.WarmupTransactions,
            report.Configuration.BatchSize,
            Environment.NewLine);
        output.AppendLine($"Order: {string.Join(" | ", report.ExecutionOrder)}");
        output.AppendLine();

        foreach (var mode in report.Modes)
        {
            output.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}: journal={1}, synchronous={2}, tx/s={3:F1}, ops/s={4:F1}, "
                + "p50={5:F3} ms, p95={6:F3} ms, p99={7:F3} ms, elapsed={8:F3} s, "
                + "max-db={9}, max-wal={10}{11}",
                mode.Mode.ToString().ToUpperInvariant(),
                mode.JournalMode,
                mode.SynchronousPragma,
                mode.TransactionsPerSecond,
                mode.OperationsPerSecond,
                mode.P50Milliseconds,
                mode.P95Milliseconds,
                mode.P99Milliseconds,
                mode.ElapsedMilliseconds / 1_000,
                FormatBytes(mode.MaximumDatabaseBytes),
                FormatBytes(mode.MaximumWalBytesBeforeClose),
                Environment.NewLine);
        }

        var normal = report.Modes.Single(mode => mode.Mode == SqliteSynchronousMode.Normal);
        var full = report.Modes.Single(mode => mode.Mode == SqliteSynchronousMode.Full);
        var throughputChangePercent = ((full.TransactionsPerSecond / normal.TransactionsPerSecond) - 1) * 100;
        var p95Ratio = normal.P95Milliseconds == 0
            ? double.PositiveInfinity
            : full.P95Milliseconds / normal.P95Milliseconds;
        output.AppendLine();
        output.AppendFormat(
            CultureInfo.InvariantCulture,
            "FULL relative to NORMAL: throughput {0:+0.0;-0.0;0.0}%; p95 latency {1:F2}x.{2}",
            throughputChangePercent,
            p95Ratio,
            Environment.NewLine);
        output.AppendLine(
            "Limitation: this warm local microbenchmark does not simulate power loss or prove that "
            + "the deployed filesystem, drive, controller, or hypervisor honors fsync. Re-run on "
            + "production-equivalent storage before selecting the durability mode.");

        return output.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        const double mebibyte = 1024 * 1024;
        return (bytes / mebibyte).ToString("F2", CultureInfo.InvariantCulture) + " MiB";
    }
}

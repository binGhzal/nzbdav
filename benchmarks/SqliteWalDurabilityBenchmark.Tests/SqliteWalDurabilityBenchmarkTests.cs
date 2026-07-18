using NzbWebDAV.Benchmarks.SqliteWal;
using Xunit;

namespace NzbWebDAV.SqliteWalDurabilityBenchmark.Tests;

public sealed class SqliteWalDurabilityBenchmarkTests
{
    [Fact]
    public void PercentileUsesLinearInterpolationBetweenOrderedSamples()
    {
        double[] samples = [4, 1, 3, 2];

        Assert.Equal(1, PercentileCalculator.Calculate(samples, 0));
        Assert.Equal(2.5, PercentileCalculator.Calculate(samples, 0.5), precision: 10);
        Assert.Equal(3.85, PercentileCalculator.Calculate(samples, 0.95), precision: 10);
        Assert.Equal(4, PercentileCalculator.Calculate(samples, 1));
    }

    [Fact]
    public async Task RunnerUsesPinnedRuntimeAndVerifiesWalAndRequestedSyncModes()
    {
        var parentDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nzbdav-wal-benchmark-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parentDirectory);

        try
        {
            var configuration = new SqliteWalBenchmarkConfiguration(
                MeasuredTransactions: 4,
                WarmupTransactions: 2,
                BatchSize: 2,
                Rounds: 1,
                TemporaryRoot: parentDirectory);

            var report = await SqliteWalBenchmarkRunner.RunAsync(configuration);

            Assert.Equal("3.53.3", report.Runtime.Version);
            Assert.Equal(2, report.Modes.Count);
            Assert.Equal(["round-1:NORMAL,FULL"], report.ExecutionOrder);

            var normal = Assert.Single(report.Modes, mode => mode.Mode == SqliteSynchronousMode.Normal);
            var full = Assert.Single(report.Modes, mode => mode.Mode == SqliteSynchronousMode.Full);
            AssertMode(normal, expectedSynchronousPragma: 1);
            AssertMode(full, expectedSynchronousPragma: 2);

            var output = BenchmarkOutput.FormatText(report);
            Assert.Contains("SQLite: 3.53.3", output);
            Assert.Contains("NORMAL", output);
            Assert.Contains("FULL", output);
            Assert.Contains("journal=wal", output);
            Assert.Matches("max-db=[0-9]+\\.[0-9]{2} MiB", output);
            Assert.Contains("does not simulate power loss", output);
            Assert.Empty(Directory.EnumerateFileSystemEntries(parentDirectory));
        }
        finally
        {
            Directory.Delete(parentDirectory, recursive: true);
        }
    }

    [Fact]
    public void CommandLineParsesExplicitWorkloadAndJsonOutput()
    {
        var parsed = BenchmarkCommandLine.Parse(
        [
            "--transactions", "123",
            "--warmup-transactions", "12",
            "--batch-size", "4",
            "--rounds", "2",
            "--temp-root", "/tmp/nzbdav-benchmark",
            "--json"
        ]);

        Assert.Equal(123, parsed.Configuration.MeasuredTransactions);
        Assert.Equal(12, parsed.Configuration.WarmupTransactions);
        Assert.Equal(4, parsed.Configuration.BatchSize);
        Assert.Equal(2, parsed.Configuration.Rounds);
        Assert.Equal("/tmp/nzbdav-benchmark", parsed.Configuration.TemporaryRoot);
        Assert.True(parsed.Json);
        Assert.False(parsed.ShowHelp);
    }

    [Fact]
    public void DefaultRoundsBalanceNormalFirstAndFullFirstExecution()
    {
        var parsed = BenchmarkCommandLine.Parse([]);

        Assert.Equal(0, parsed.Configuration.Rounds % 2);
    }

    [Fact]
    public void CommandLineRejectsMissingAndInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkCommandLine.Parse(["--transactions"]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BenchmarkCommandLine.Parse(["--batch-size", "0"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCommandLine.Parse(["--unexpected"]));
    }

    private static void AssertMode(SqliteWalModeResult mode, int expectedSynchronousPragma)
    {
        Assert.Equal("wal", mode.JournalMode);
        Assert.Equal(expectedSynchronousPragma, mode.SynchronousPragma);
        Assert.Equal(4, mode.MeasuredTransactions);
        Assert.Equal(3, mode.OperationsPerTransaction);
        Assert.True(mode.TransactionsPerSecond > 0);
        Assert.True(mode.P50Milliseconds >= 0);
        Assert.True(mode.P95Milliseconds >= mode.P50Milliseconds);
        Assert.True(mode.P99Milliseconds >= mode.P95Milliseconds);
    }
}

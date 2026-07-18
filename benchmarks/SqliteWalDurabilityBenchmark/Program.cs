namespace NzbWebDAV.Benchmarks.SqliteWal;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var commandLine = BenchmarkCommandLine.Parse(args);
            if (commandLine.ShowHelp)
            {
                Console.WriteLine(Usage);
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var report = await SqliteWalBenchmarkRunner
                .RunAsync(commandLine.Configuration, cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine(commandLine.Json
                ? BenchmarkOutput.FormatJson(report)
                : BenchmarkOutput.FormatText(report));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark failed: {exception.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private const string Usage = """
        Usage:
          dotnet run --project benchmarks/SqliteWalDurabilityBenchmark -c Release -- [options]

        Options:
          --transactions N          Measured transactions per mode/round (default: 2000)
          --warmup-transactions N   Warmup transactions per mode/round (default: 200)
          --batch-size N            Inserts per transaction; one update is also performed (default: 8)
          --rounds N                Alternating NORMAL/FULL rounds (default: 4)
          --temp-root PATH          Parent for auto-deleted temporary databases
          --json                    Emit a machine-readable JSON report
          --help, -h                Show this help
        """;
}

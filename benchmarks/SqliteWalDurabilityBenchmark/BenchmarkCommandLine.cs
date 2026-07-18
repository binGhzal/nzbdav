using System.Globalization;

namespace NzbWebDAV.Benchmarks.SqliteWal;

public sealed record BenchmarkCommandLineArguments(
    SqliteWalBenchmarkConfiguration Configuration,
    bool Json,
    bool ShowHelp);

public static class BenchmarkCommandLine
{
    public static BenchmarkCommandLineArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var configuration = new SqliteWalBenchmarkConfiguration();
        var json = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--transactions":
                    configuration = configuration with
                    {
                        MeasuredTransactions = ReadInteger(args, ref index, "--transactions")
                    };
                    break;
                case "--warmup-transactions":
                    configuration = configuration with
                    {
                        WarmupTransactions = ReadInteger(args, ref index, "--warmup-transactions")
                    };
                    break;
                case "--batch-size":
                    configuration = configuration with
                    {
                        BatchSize = ReadInteger(args, ref index, "--batch-size")
                    };
                    break;
                case "--rounds":
                    configuration = configuration with
                    {
                        Rounds = ReadInteger(args, ref index, "--rounds")
                    };
                    break;
                case "--temp-root":
                    configuration = configuration with
                    {
                        TemporaryRoot = ReadString(args, ref index, "--temp-root")
                    };
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option '{args[index]}'.", nameof(args));
            }
        }

        configuration.Validate();
        return new BenchmarkCommandLineArguments(configuration, json, showHelp);
    }

    private static int ReadInteger(string[] args, ref int index, string option)
    {
        var raw = ReadString(args, ref index, option);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Option {option} requires an integer; received '{raw}'.", nameof(args));
        return value;
    }

    private static string ReadString(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Option {option} requires a value.", nameof(args));
        return args[index];
    }
}

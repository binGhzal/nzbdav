using System.Diagnostics;
using backend.Tests.Database;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;
using NzbWebDAV.Hosting;
using backend.Tests.Security;

namespace backend.Tests.Hosting;

public sealed class NzbdavRoleStartupTests
{
    [Fact]
    public void LegacyUpgradeGuardUsesTheFixedStartupFailureBoundary()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("backend/Program.cs"));
        var guard = source[source.IndexOf("private static void BlockUpgradesToV06X", StringComparison.Ordinal)..];
        guard = guard[..guard.IndexOf("private static void ConfigureThreadPool", StringComparison.Ordinal)];

        Assert.Contains("StartupFailureContract.LegacyUpgradeRefusalMessage", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("/config", guard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRoleProcessOutputNeverRendersConfiguredValueOrException()
    {
        var result = await StartApplicationAsync(PublicFailureCanary.Composite);

        Assert.NotEqual(0, result.ExitCode);
        PublicFailureCanary.AssertSafe(result.Output, maximumLength: 1024);
        Assert.Contains("startup_invalid_role", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("control", "Control")]
    [InlineData("gateway", "Gateway")]
    [InlineData("worker-download", "WorkerDownload")]
    [InlineData("worker-verify", "WorkerVerify")]
    [InlineData("worker-repair", "WorkerRepair")]
    [InlineData("ui", "Ui")]
    public async Task SeparatedRoleStopsBeforeDatabaseProviderValidation(
        string configuredRole,
        string expectedRole)
    {
        var result = await StartApplicationAsync(configuredRole);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            $"NZBDAV_ROLE '{expectedRole}' is defined but not executable",
            result.Output);
        Assert.DoesNotContain("Unsupported database provider", result.Output);
    }

    [Fact]
    public async Task AllPassesRoleGuardAndReachesDatabaseProviderValidation()
    {
        var result = await StartApplicationAsync("all");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("is defined but not executable", result.Output);
        Assert.Contains("Unsupported database provider", result.Output);
    }

    [Fact]
    public async Task PostgreSqlProviderFailsClosedBeforeReadingConnectionConfiguration()
    {
        var result = await StartApplicationAsync("all", "postgres");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "PostgreSQL runtime is disabled until the provider-native migration and transfer gates pass",
            result.Output);
        Assert.DoesNotContain("NZBDAV_DATABASE_CONNECTION_STRING", result.Output);
    }

    [Fact]
    public async Task PostgreSqlMigrationCommandFailsClosedBeforeRuntimeFactoryOrConnectionSecret()
    {
        var result = await StartApplicationAsync("all", "postgres", "--db-migration");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "PostgreSQL runtime is disabled until the provider-native migration and transfer gates pass",
            result.Output);
        Assert.DoesNotContain("NZBDAV_DATABASE_CONNECTION_STRING", result.Output);
        Assert.DoesNotContain(DatabaseMigrator.PostgreSqlRefusalMessage, result.Output);
    }

    public static TheoryData<string[]> PostgreSqlCommands => new()
    {
        { [] },
        { ["--db-migration"] },
        { ["--db-export-json", "/path-that-must-not-be-created/export.json"] },
        { ["--db-import-json", "/path-that-must-not-be-opened/import.json"] },
        { ["--db-import-json", "/path-that-must-not-be-opened/import.json", "--replace"] },
    };

    public static TheoryData<string[]> EarlyInvalidCommands => new()
    {
        { ["--unknown"] },
        { ["--db-migration", "-target"] },
        { ["--db-export-v3", "/snapshot"] },
        { ["--db-import-v3", "/snapshot"] },
        { ["--db-import-v3", "/snapshot", "extra"] },
    };

    [Theory]
    [MemberData(nameof(PostgreSqlCommands))]
    public async Task ProcessEnvironmentPostgreSqlPathsRefuseWithoutOpeningDotEnvFifo(string[] arguments)
    {
        using var fixture = new StartupFixture();
        await fixture.CreateFifoAsync(fixture.DotEnvPath);

        var result = await StartApplicationCoreAsync(
            "all",
            "postgres",
            arguments,
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(MaintenanceCommandLine.PostgreSqlUnavailableMessage, result.Output);
        Assert.DoesNotContain("NZBDAV_DATABASE_CONNECTION_STRING", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EarlyInvalidCommands))]
    public async Task InvalidAndFutureV3ArgumentsRefuseWithoutOpeningDotEnvFifo(string[] arguments)
    {
        using var fixture = new StartupFixture();
        await fixture.CreateFifoAsync(fixture.DotEnvPath);

        var result = await StartApplicationCoreAsync(
            "all",
            "sqlite",
            arguments,
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, result.ExitCode);
        if (arguments[0] is "--db-export-v3" or "--db-import-v3" && arguments.Length == 2)
            Assert.Contains(MaintenanceCommandLine.TransferV3UnavailableMessage, result.Output);
        else
            Assert.Contains(MaintenanceCommandLine.InvalidArgumentsMessage, result.Output);
    }

    [Theory]
    [InlineData("sqlite", "--db-export-v3")]
    [InlineData("sqlite", "--db-import-v3")]
    [InlineData("postgres", "--db-export-v3")]
    [InlineData("postgres", "--db-import-v3")]
    public async Task ExactV3FormsRefuseBeforeProviderDotEnvOrTargetPathIo(
        string provider,
        string command)
    {
        using var fixture = new StartupFixture();
        await fixture.CreateFifoAsync(fixture.DotEnvPath);
        var inputFifo = Path.Combine(fixture.Root, "v3-input");
        await fixture.CreateFifoAsync(inputFifo);
        var outputParent = Path.Combine(fixture.Root, "v3-output-parent");
        var target = command == "--db-import-v3"
            ? inputFifo
            : Path.Combine(outputParent, "snapshot");

        var result = await StartApplicationCoreAsync(
            "all",
            provider,
            [command, target],
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(5));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(MaintenanceCommandLine.TransferV3UnavailableMessage, result.Output);
        Assert.DoesNotContain(MaintenanceCommandLine.PostgreSqlUnavailableMessage, result.Output);
        Assert.False(Directory.Exists(outputParent));
    }

    [Fact]
    public async Task DotEnvOnlyPostgreSqlProviderIsRefusedAfterGenericDotEnvIngestion()
    {
        using var fixture = new StartupFixture();
        await File.WriteAllTextAsync(
            fixture.DotEnvPath,
            "NZBDAV_DATABASE_PROVIDER=postgres\nNZBDAV_DATABASE_CONNECTION_STRING=must-not-be-used\n");

        var result = await StartApplicationCoreAsync(
            "all",
            databaseProvider: null,
            [],
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(MaintenanceCommandLine.PostgreSqlUnavailableMessage, result.Output);
        Assert.DoesNotContain("must-not-be-used", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSqlTransferCommandsDoNotOpenInputOrCreateOutputParent()
    {
        using var fixture = new StartupFixture();
        var inputFifo = Path.Combine(fixture.Root, "transfer-input");
        await fixture.CreateFifoAsync(inputFifo);
        var outputPath = Path.Combine(fixture.Root, "missing-parent", "export.json");

        var importResult = await StartApplicationCoreAsync(
            "all",
            "postgres",
            ["--db-import-json", inputFifo, "--replace"],
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(5));
        var exportResult = await StartApplicationCoreAsync(
            "all",
            "postgres",
            ["--db-export-json", outputPath],
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(5));

        Assert.Contains(MaintenanceCommandLine.PostgreSqlUnavailableMessage, importResult.Output);
        Assert.Contains(MaintenanceCommandLine.PostgreSqlUnavailableMessage, exportResult.Output);
        Assert.False(Directory.Exists(Path.GetDirectoryName(outputPath)));
    }

    [Fact]
    public async Task AnySqliteImportMarkerRefusesEveryPublicNonImportV3PathBeforeSideEffects()
    {
        using var fixture = new StartupFixture();
        Directory.CreateDirectory(fixture.ConfigPath);
        await CreateMarkerDatabaseAsync(
            Path.Combine(fixture.ConfigPath, "db.sqlite"),
            "{\"formatVersion\":3,\"state\":\"fresh\"}");
        var outputPath = Path.Combine(fixture.Root, "output-parent", "export.json");
        var inputPath = Path.Combine(fixture.Root, "input-that-does-not-exist.json");
        var commands = new[]
        {
            Array.Empty<string>(),
            new[] { "--db-migration" },
            new[] { "--db-export-json", outputPath },
            new[] { "--db-import-json", inputPath },
            new[] { "--db-import-json", inputPath, "--replace" },
        };

        foreach (var arguments in commands)
        {
            var result = await StartApplicationCoreAsync(
                "all",
                "sqlite",
                arguments,
                fixture.ConfigPath,
                fixture.DotEnvPath,
                TimeSpan.FromSeconds(15));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(TransferV3StartupGuard.RefusalMessage, result.Output);
            Assert.DoesNotContain("Now listening on", result.Output, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(outputPath)));
    }

    [Fact]
    public async Task AbsentSqliteMarkerRetainsSuccessfulMigrationStartup()
    {
        using var fixture = new StartupFixture();
        Directory.CreateDirectory(fixture.ConfigPath);

        var result = await StartApplicationCoreAsync(
            "all",
            "sqlite",
            ["--db-migration"],
            fixture.ConfigPath,
            fixture.DotEnvPath,
            TimeSpan.FromSeconds(45));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.ConfigPath, "db.sqlite")));
        Assert.DoesNotContain(TransferV3StartupGuard.RefusalMessage, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramOrdersPureParsingAndProviderAndSqliteGatesBeforeTargetSideEffects()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("backend/Program.cs"));
        var mainEnd = source.IndexOf("private static void", StringComparison.Ordinal);
        var main = source[..mainEnd];

        var parse = main.IndexOf("MaintenanceCommandLine.Parse(args)", StringComparison.Ordinal);
        var v3ExportRefusal = main.IndexOf(
            "MaintenanceCommandKind.ExportV3Unavailable",
            StringComparison.Ordinal);
        var v3ImportRefusal = main.IndexOf(
            "MaintenanceCommandKind.ImportV3Unavailable",
            StringComparison.Ordinal);
        var v3Throw = main.IndexOf(
            "MaintenanceCommandLine.TransferV3UnavailableMessage",
            StringComparison.Ordinal);
        var firstPostgresGate = main.IndexOf("ThrowIfPostgreSqlUnavailable()", StringComparison.Ordinal);
        var dotenv = main.IndexOf("EnvironmentUtil.LoadDotEnvFile()", StringComparison.Ordinal);
        var secondPostgresGate = main.IndexOf(
            "ThrowIfPostgreSqlUnavailable()",
            firstPostgresGate + 1,
            StringComparison.Ordinal);
        var runtimeGate = main.IndexOf("ReadLoadedRuntimeAsync(CancellationToken.None)", StringComparison.Ordinal);
        var startupGuard = main.IndexOf("TransferV3StartupGuard", StringComparison.Ordinal);
        var threadPool = main.IndexOf("ConfigureThreadPool()", StringComparison.Ordinal);
        var logger = main.IndexOf("new LoggerConfiguration()", StringComparison.Ordinal);
        var upgrade = main.IndexOf("BlockUpgradesToV06X()", StringComparison.Ordinal);

        Assert.True(parse >= 0 && parse < v3ExportRefusal);
        Assert.True(v3ExportRefusal < v3ImportRefusal);
        Assert.True(v3ImportRefusal < v3Throw);
        Assert.True(v3Throw < firstPostgresGate);
        Assert.True(firstPostgresGate < dotenv);
        Assert.True(dotenv < secondPostgresGate);
        Assert.True(secondPostgresGate < runtimeGate);
        Assert.True(runtimeGate < startupGuard);
        Assert.True(startupGuard < threadPool);
        Assert.True(startupGuard < logger);
        Assert.True(startupGuard < upgrade);
    }

    private static async Task<ProcessResult> StartApplicationAsync(
        string role,
        string databaseProvider = "invalid",
        params string[] arguments)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-role-config-{Guid.NewGuid():N}");
        var envPath = Path.Combine(Path.GetTempPath(), $"nzbdav-role-{Guid.NewGuid():N}.env");
        return await StartApplicationCoreAsync(
            role,
            databaseProvider,
            arguments,
            configPath,
            envPath,
            TimeSpan.FromSeconds(15));
    }

    private static async Task<ProcessResult> StartApplicationCoreAsync(
        string role,
        string? databaseProvider,
        string[] arguments,
        string configPath,
        string envPath,
        TimeSpan timeoutBound)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ConfigManager).Assembly.Location);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["NZBDAV_ENV_FILE"] = envPath;
        startInfo.Environment["NZBDAV_ROLE"] = role;
        startInfo.Environment["CONFIG_PATH"] = configPath;
        startInfo.Environment.Remove("NZBDAV_DATABASE_CONNECTION_STRING");
        if (databaseProvider is null)
            startInfo.Environment.Remove("NZBDAV_DATABASE_PROVIDER");
        else
            startInfo.Environment["NZBDAV_DATABASE_PROVIDER"] = databaseProvider;

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Unable to start the NZBDAV process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutBound);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"NZBDAV did not exit for role '{role}' and provider '{databaseProvider}' within {timeoutBound}.");
        }

        return new ProcessResult(process.ExitCode, (await stdoutTask) + (await stderrTask));
    }

    private static async Task CreateMarkerDatabaseAsync(string path, string value)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE ConfigItems(ConfigName TEXT NOT NULL PRIMARY KEY, ConfigValue TEXT);"
            + "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", TransferV3ReservedConfigPolicy.ImportStateKey);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StartupFixture : IDisposable
    {
        public StartupFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"nzbdav-startup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string ConfigPath => Path.Combine(Root, "config");
        public string DotEnvPath => Path.Combine(Root, "process.env");

        public async Task CreateFifoAsync(string path)
        {
            var startInfo = new ProcessStartInfo("mkfifo")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start mkfifo.");
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

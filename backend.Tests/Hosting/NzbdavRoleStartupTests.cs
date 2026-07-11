using System.Diagnostics;
using NzbWebDAV.Config;

namespace backend.Tests.Hosting;

public sealed class NzbdavRoleStartupTests
{
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

    private static async Task<ProcessResult> StartApplicationAsync(string role)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(ConfigManager).Assembly.Location);
        startInfo.Environment["NZBDAV_ENV_FILE"] = Path.Combine(
            Path.GetTempPath(),
            $"nzbdav-role-{Guid.NewGuid():N}.env");
        startInfo.Environment["NZBDAV_ROLE"] = role;
        startInfo.Environment["NZBDAV_DATABASE_PROVIDER"] = "invalid";

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Unable to start the NZBDAV process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"NZBDAV did not exit for role '{role}'.");
        }

        return new ProcessResult(process.ExitCode, (await stdoutTask) + (await stderrTask));
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

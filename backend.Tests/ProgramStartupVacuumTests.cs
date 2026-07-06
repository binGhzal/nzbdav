using System.Reflection;
using NzbWebDAV.Config;

namespace backend.Tests;

public sealed class ProgramStartupVacuumTests
{
    [Theory]
    [InlineData(2_000, 1_000, false, true)]
    [InlineData(1_000, 1_000, false, false)]
    [InlineData(2_000, 1_000, true, false)]
    public void ShouldSkipStartupVacuumSkipsLargeDatabasesUnlessForced(
        long sqliteBytes,
        long maxStartupVacuumBytes,
        bool forceVacuum,
        bool expected)
    {
        var programType = typeof(ConfigManager).Assembly.GetType("NzbWebDAV.Program");
        Assert.NotNull(programType);
        var method = programType.GetMethod(
            "ShouldSkipStartupVacuum",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, [sqliteBytes, maxStartupVacuumBytes, forceVacuum])!;

        Assert.Equal(expected, result);
    }
}

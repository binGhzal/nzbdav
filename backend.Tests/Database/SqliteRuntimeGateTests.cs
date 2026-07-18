using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class SqliteRuntimeGateTests
{
    private static readonly string[] RequiredCompileOptions =
    [
        "THREADSAFE=1",
        "DEFAULT_FOREIGN_KEYS",
        "DEFAULT_SYNCHRONOUS=2",
        "DEFAULT_WAL_SYNCHRONOUS=2",
        "TEMP_STORE=1"
    ];

    [Fact]
    public void ValidateAcceptsPinnedRuntimeAndRequiredCompileOptions()
    {
        var info = new SqliteRuntimeInfo(
            SqliteRuntimeGate.RequiredVersion,
            SqliteRuntimeGate.RequiredSourceId,
            RequiredCompileOptions);

        SqliteRuntimeGate.Validate(info);
    }

    [Theory]
    [InlineData("3.50.4", "unsupported SQLite version")]
    [InlineData("3.53.3", "unexpected SQLite source id")]
    public void ValidateRejectsUnapprovedRuntimeIdentity(string version, string expectedMessage)
    {
        var sourceId = version == SqliteRuntimeGate.RequiredVersion
            ? "unexpected-source-id"
            : SqliteRuntimeGate.RequiredSourceId;
        var info = new SqliteRuntimeInfo(version, sourceId, RequiredCompileOptions);

        var exception = Assert.Throws<InvalidOperationException>(() => SqliteRuntimeGate.Validate(info));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsMissingOrUnsafeCompileOptions()
    {
        var info = new SqliteRuntimeInfo(
            SqliteRuntimeGate.RequiredVersion,
            SqliteRuntimeGate.RequiredSourceId,
            ["THREADSAFE=1", "DEFAULT_FOREIGN_KEYS", "DEFAULT_SYNCHRONOUS=2", "OMIT_WAL"]);

        var exception = Assert.Throws<InvalidOperationException>(() => SqliteRuntimeGate.Validate(info));

        Assert.Contains("DEFAULT_WAL_SYNCHRONOUS=2", exception.Message);
        Assert.Contains("TEMP_STORE=1", exception.Message);
        Assert.Contains("OMIT_WAL", exception.Message);
    }

    [Fact]
    public async Task LoadedNativeRuntimeMatchesApprovedIdentity()
    {
        var info = await SqliteRuntimeGate.ReadLoadedRuntimeAsync();

        SqliteRuntimeGate.Validate(info);
        Assert.Equal(SqliteRuntimeGate.RequiredVersion, info.Version);
        Assert.Equal(SqliteRuntimeGate.RequiredSourceId, info.SourceId);
    }
}

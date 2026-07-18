using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database;

public sealed record SqliteRuntimeInfo(
    string Version,
    string SourceId,
    IReadOnlyCollection<string> CompileOptions);

public static class SqliteRuntimeGate
{
    public const string RequiredVersion = "3.53.3";
    public const string RequiredSourceId =
        "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62";

    private static readonly string[] RequiredCompileOptions =
    [
        "THREADSAFE=1",
        "DEFAULT_FOREIGN_KEYS",
        "DEFAULT_SYNCHRONOUS=2",
        "DEFAULT_WAL_SYNCHRONOUS=2",
        "TEMP_STORE=1"
    ];

    private static readonly string[] ForbiddenCompileOptions =
    [
        "OMIT_WAL",
        "OMIT_FOREIGN_KEY",
        "OMIT_TRIGGER"
    ];

    public static async Task<SqliteRuntimeInfo> ReadLoadedRuntimeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            "Data Source=:memory:;Mode=Memory;Cache=Private;Pooling=False");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string version;
        string sourceId;
        await using (var identityCommand = connection.CreateCommand())
        {
            identityCommand.CommandText = "SELECT sqlite_version(), sqlite_source_id();";
            await using var reader = await identityCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("SQLite did not return a runtime identity.");
            version = reader.GetString(0);
            sourceId = reader.GetString(1);
        }

        var compileOptions = new List<string>();
        await using (var optionsCommand = connection.CreateCommand())
        {
            optionsCommand.CommandText = "PRAGMA compile_options;";
            await using var reader = await optionsCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                compileOptions.Add(reader.GetString(0));
        }

        return new SqliteRuntimeInfo(version, sourceId, compileOptions);
    }

    public static void Validate(SqliteRuntimeInfo info)
    {
        var problems = new List<string>();
        if (!string.Equals(info.Version, RequiredVersion, StringComparison.Ordinal))
            problems.Add($"unsupported SQLite version '{info.Version}' (required: {RequiredVersion})");
        if (!string.Equals(info.SourceId, RequiredSourceId, StringComparison.Ordinal))
            problems.Add($"unexpected SQLite source id '{info.SourceId}'");

        var options = info.CompileOptions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredCompileOptions.Where(option => !options.Contains(option)).ToArray();
        if (missing.Length > 0)
            problems.Add($"missing required compile options: {string.Join(", ", missing)}");

        var forbidden = ForbiddenCompileOptions.Where(options.Contains).ToArray();
        if (forbidden.Length > 0)
            problems.Add($"forbidden compile options present: {string.Join(", ", forbidden)}");

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "NZBDav refuses to start with an unapproved SQLite runtime: "
                + string.Join("; ", problems));
        }
    }
}

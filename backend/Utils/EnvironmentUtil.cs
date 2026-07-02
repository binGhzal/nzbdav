using NzbWebDAV.Extensions;

namespace NzbWebDAV.Utils;

public static class EnvironmentUtil
{
    public static void LoadDotEnvFile()
    {
        var configuredPath = GetVariable("NZBDAV_ENV_FILE");
        var candidates = configuredPath != null ? [configuredPath] : new[] { ".env", "../.env" };
        var envPath = candidates.FirstOrDefault(File.Exists);
        if (envPath == null) return;

        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            if (key.Length == 0 || Environment.GetEnvironmentVariable(key) != null) continue;

            var value = line[(separator + 1)..].Trim();
            value = UnquoteEnvValue(value);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static string? GetVariable(string envVariable)
    {
        return Environment.GetEnvironmentVariable(envVariable).ToNullIfEmpty();
    }

    public static string GetRequiredVariable(string envVariable)
    {
        return GetVariable(envVariable) ??
               throw new Exception($"The environment variable `{envVariable}` must be set.");
    }

    public static long? GetLongVariable(string envVariable)
    {
        return long.TryParse(Environment.GetEnvironmentVariable(envVariable), out var longValue) ? longValue : null;
    }

    public static bool IsVariableTrue(string envVariable)
    {
        var value = Environment.GetEnvironmentVariable(envVariable)?.ToLower();
        return value is "y" or "yes" or "true";
    }

    private static string UnquoteEnvValue(string value)
    {
        if (value.Length < 2) return value;
        var quote = value[0];
        if ((quote != '"' && quote != '\'') || value[^1] != quote) return value;
        var unquoted = value[1..^1];
        return quote == '"' ? unquoted.Replace("\\n", "\n").Replace("\\\"", "\"") : unquoted;
    }
}

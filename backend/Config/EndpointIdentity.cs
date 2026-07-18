namespace NzbWebDAV.Config;

internal readonly record struct EndpointIdentity(
    string Scheme,
    string Host,
    int Port,
    string UserInfo,
    string Path,
    string Query,
    string Fragment)
{
    public static bool AreEquivalent(string? first, string? second)
    {
        return TryCreate(first, out var firstIdentity)
               && TryCreate(second, out var secondIdentity)
               && firstIdentity == secondIdentity;
    }

    private static bool TryCreate(string? value, out EndpointIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        var schemeDelimiter = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeDelimiter <= 0
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var authorityStart = schemeDelimiter + 3;
        var suffixStart = FindSuffixStart(trimmed, authorityStart);
        var authority = trimmed[authorityStart..suffixStart];
        if (authority.Contains('\\')) return false;
        var userInfoSeparator = authority.LastIndexOf('@');
        var userInfo = userInfoSeparator < 0 ? "" : authority[..userInfoSeparator];

        var fragmentStart = trimmed.IndexOf('#', suffixStart);
        var queryStart = trimmed.IndexOf('?', suffixStart);
        if (fragmentStart >= 0 && queryStart > fragmentStart) queryStart = -1;
        var pathEnd = MinNonNegative(trimmed.Length, queryStart, fragmentStart);
        var path = trimmed[suffixStart..pathEnd];
        var queryEnd = fragmentStart >= 0 ? fragmentStart : trimmed.Length;
        var query = queryStart >= 0 ? trimmed[queryStart..queryEnd] : "";
        var fragment = fragmentStart >= 0 ? trimmed[fragmentStart..] : "";

        identity = new EndpointIdentity(
            uri.Scheme.ToLowerInvariant(),
            uri.IdnHost.ToLowerInvariant(),
            uri.Port,
            userInfo,
            path == "/" ? "" : path,
            query,
            fragment);
        return true;
    }

    private static int FindSuffixStart(string value, int authorityStart)
    {
        for (var index = authorityStart; index < value.Length; index++)
        {
            if (value[index] is '/' or '?' or '#') return index;
        }

        return value.Length;
    }

    private static int MinNonNegative(int fallback, params int[] values)
    {
        var result = fallback;
        foreach (var value in values)
        {
            if (value >= 0 && value < result) result = value;
        }

        return result;
    }
}

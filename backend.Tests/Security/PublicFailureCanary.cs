using System.Text;
using Microsoft.AspNetCore.Http;

namespace backend.Tests.Security;

internal static class PublicFailureCanary
{
    private static readonly string[] SensitiveFragments =
    [
        string.Concat("credential", "-marker"),
        string.Concat(Path.DirectorySeparatorChar, "private", Path.DirectorySeparatorChar, "pinrail", Path.DirectorySeparatorChar, "failure-marker"),
        string.Concat("https://", "operator", ":", "credential", "@provider.invalid/private"),
        string.Concat("Server", "=database.invalid;", "Pass", "word", "=credential"),
        string.Concat("provider", "-body-marker"),
        string.Concat("nested", "-exception-marker"),
    ];

    public static string Composite => string.Join(
        "|",
        SensitiveFragments.Append(string.Concat("line-one", "\r\n", "line-two", '\u001b', "[31m", '\u0001', new string('x', 5000))));

    public static Exception NestedException => new InvalidOperationException(
        Composite,
        new IOException(SensitiveFragments[^1]));

    public static void AssertSafe(string? output, int maximumLength = 4096)
    {
        output ??= string.Empty;
        Assert.True(output.Length <= maximumLength);
        foreach (var fragment in SensitiveFragments)
            Assert.False(output.Contains(fragment, StringComparison.Ordinal));
        Assert.False(output.Contains('\r'));
        Assert.False(output.Contains('\u001b'));
        Assert.False(output.Contains('\u0001'));
    }

    public static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}

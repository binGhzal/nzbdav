using Xunit;

namespace backend.Tests.Database;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestConnectionStringVariable))
            && !string.Equals(
                Environment.GetEnvironmentVariable(RequireTestsVariable),
                "1",
                StringComparison.Ordinal))
            Skip = $"Set {TestConnectionStringVariable} to run PostgreSQL migration tests.";
    }

    public const string TestConnectionStringVariable = "NZBDAV_TEST_POSTGRES_CONNECTION_STRING";
    public const string RequireTestsVariable = "NZBDAV_REQUIRE_POSTGRES_TESTS";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PostgreSqlFactAttribute.TestConnectionStringVariable))
            && !string.Equals(
                Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.RequireTestsVariable),
                "1",
                StringComparison.Ordinal))
            Skip = $"Set {PostgreSqlFactAttribute.TestConnectionStringVariable} to run PostgreSQL migration tests.";
    }
}

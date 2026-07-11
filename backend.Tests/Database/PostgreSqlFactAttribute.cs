using Xunit;

namespace backend.Tests.Database;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestConnectionStringVariable)))
            Skip = $"Set {TestConnectionStringVariable} to run PostgreSQL migration tests.";
    }

    public const string TestConnectionStringVariable = "NZBDAV_TEST_POSTGRES_CONNECTION_STRING";
}

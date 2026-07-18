namespace backend.Tests.Database;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class PostgreSqlFactAttributeTests
{
    [Fact]
    public void MissingConnectionIsSkippedWhenPostgreSqlIsOptional()
    {
        using var environment = new PostgreSqlTestEnvironment(connectionString: null, required: null);

        var attribute = new PostgreSqlFactAttribute();
        var theoryAttribute = new PostgreSqlTheoryAttribute();

        Assert.Contains(
            PostgreSqlFactAttribute.TestConnectionStringVariable,
            attribute.Skip,
            StringComparison.Ordinal);
        Assert.Contains(
            PostgreSqlFactAttribute.TestConnectionStringVariable,
            theoryAttribute.Skip,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingConnectionIsNotSkippedWhenPostgreSqlIsRequired()
    {
        using var environment = new PostgreSqlTestEnvironment(connectionString: null, required: "1");

        var attribute = new PostgreSqlFactAttribute();
        var theoryAttribute = new PostgreSqlTheoryAttribute();

        Assert.Null(attribute.Skip);
        Assert.Null(theoryAttribute.Skip);
    }

    private sealed class PostgreSqlTestEnvironment : IDisposable
    {
        private readonly string? _connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.TestConnectionStringVariable);
        private readonly string? _required = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.RequireTestsVariable);

        internal PostgreSqlTestEnvironment(string? connectionString, string? required)
        {
            Environment.SetEnvironmentVariable(
                PostgreSqlFactAttribute.TestConnectionStringVariable,
                connectionString);
            Environment.SetEnvironmentVariable(PostgreSqlFactAttribute.RequireTestsVariable, required);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                PostgreSqlFactAttribute.TestConnectionStringVariable,
                _connectionString);
            Environment.SetEnvironmentVariable(PostgreSqlFactAttribute.RequireTestsVariable, _required);
        }
    }
}

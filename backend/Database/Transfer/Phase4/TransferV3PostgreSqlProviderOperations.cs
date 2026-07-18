using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal interface ITransferV3PostgreSqlProviderOperations
{
    NpgsqlConnection CreateConnection(NpgsqlDataSource dataSource);

    Task OpenAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken);

    Task<TransferV3PostgreSqlTargetIdentity> ValidateServerAsync(
        NpgsqlConnection connection,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);

    Task<string> ValidateEnvironmentAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);

    ValueTask DisposeConnectionAsync(NpgsqlConnection connection);

    ValueTask DisposeDataSourceAsync(NpgsqlDataSource dataSource);
}

internal sealed class TransferV3PostgreSqlProviderOperations
    : ITransferV3PostgreSqlProviderOperations
{
    internal static TransferV3PostgreSqlProviderOperations Instance { get; } = new();

    private TransferV3PostgreSqlProviderOperations()
    {
    }

    public NpgsqlConnection CreateConnection(NpgsqlDataSource dataSource) =>
        dataSource.CreateConnection();

    public Task OpenAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        connection.OpenAsync(cancellationToken);

    public Task<TransferV3PostgreSqlTargetIdentity> ValidateServerAsync(
        NpgsqlConnection connection,
        string expectedTimeZoneId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
            connection,
            expectedTimeZoneId,
            commandTimeoutSeconds,
            cancellationToken);

    public Task<string> ValidateEnvironmentAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        PostgreSqlEnvironmentContract.ValidateAsync(
            connection,
            commandTimeoutSeconds,
            cancellationToken);

    public ValueTask DisposeConnectionAsync(NpgsqlConnection connection) =>
        connection.DisposeAsync();

    public ValueTask DisposeDataSourceAsync(NpgsqlDataSource dataSource) =>
        dataSource.DisposeAsync();
}

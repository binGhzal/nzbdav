using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Clients.Usenet;

public interface IProviderPoolSnapshotSource
{
    IReadOnlyList<ProviderPoolSnapshot> GetProviderSnapshots();
}

public sealed record ProviderPoolSnapshot(
    string Name,
    string Type,
    int Priority,
    string Role,
    int MaxConnections,
    int LiveConnections,
    int IdleConnections,
    int ActiveConnections,
    int AvailableConnections,
    bool StatPipeliningEnabled,
    ProviderCircuitBreakerSnapshot Circuit);

using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int ConfiguredPooledConnections => Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => Math.Max(0, x.MaxConnections))
        .Sum();

    public int TotalPooledConnections => Math.Max(1, ConfiguredPooledConnections);

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }
        public int Priority { get; set; } = 100;

        // Whether STAT existence checks may be pipelined for this provider. Not "required" so
        // that provider configs saved before this feature existed deserialize to false (i.e.
        // linear STAT) and only opt in once the user has tested + enabled pipelining.
        public bool StatPipeliningEnabled { get; set; } = false;
    }
}

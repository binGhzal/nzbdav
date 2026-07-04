using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Config;

public class ArrConfig
{
    public List<ConnectionDetails> RadarrInstances { get; set; } = [];
    public List<ConnectionDetails> SonarrInstances { get; set; } = [];
    public List<ConnectionDetails> LidarrInstances { get; set; } = [];
    public List<QueueRule> QueueRules { get; set; } = [];
    public PrioritizationOptions Prioritization { get; set; } = new();
    public SearchNudgeOptions SearchNudge { get; set; } = new();

    // ReSharper disable once InvokeAsExtensionMethod
    public IEnumerable<ArrClient> GetArrClients() => Enumerable.Concat(
        RadarrInstances.Select(ArrClient (x) => new RadarrClient(x.Host, x.ApiKey)),
        SonarrInstances.Select(ArrClient (x) => new SonarrClient(x.Host, x.ApiKey))
    ).Concat(
        LidarrInstances.Select(ArrClient (x) => new LidarrClient(x.Host, x.ApiKey))
    );

    public int GetInstanceCount() =>
        RadarrInstances.Count + SonarrInstances.Count + LidarrInstances.Count;

    public class ConnectionDetails
    {
        public required string Host { get; set; }
        public required string ApiKey { get; set; }
    }

    public class QueueRule
    {
        public string Message { get; set; } = null!;
        public QueueAction Action { get; set; }
    }

    public enum QueueAction
    {
        DoNothing = 0,
        Remove = 1,
        RemoveAndBlocklist = 2,
        RemoveAndBlocklistAndSearch = 3
    }

    public class PrioritizationOptions
    {
        public bool Enabled { get; set; } = false;
        public string Mode { get; set; } = "report";
        public int RecomputeIntervalSeconds { get; set; } = 300;
        public int MaxAutomaticPriority { get; set; } = (int)Database.Models.QueueItem.PriorityOption.High;
    }

    public class SearchNudgeOptions
    {
        public bool Enabled { get; set; } = false;
        public string Mode { get; set; } = "report";
        public int IntervalSeconds { get; set; } = 1800;
        public int CooldownSeconds { get; set; } = 21600;
        public int MaxCommandsPerHour { get; set; } = 20;
        public int SonarrBatchSize { get; set; } = 10;
        public int RadarrBatchSize { get; set; } = 5;
        public int ConcurrentCommandsPerInstance { get; set; } = 1;
    }
}

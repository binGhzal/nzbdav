using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Coordination;

public sealed class ConfigWorkerLaneCapacityPolicy(ConfigManager configManager) : IWorkerLaneCapacityPolicy
{
    public int GetMaximum(WorkerJob.JobKind kind) => kind switch
    {
        WorkerJob.JobKind.Download => configManager.GetAdaptiveMaxConcurrentQueueDownloads(),
        WorkerJob.JobKind.Verify => configManager.GetAdaptiveMaxConcurrentVerifyJobs(),
        WorkerJob.JobKind.Repair => configManager.GetAdaptiveMaxConcurrentRepairJobs(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown worker lane.")
    };
}

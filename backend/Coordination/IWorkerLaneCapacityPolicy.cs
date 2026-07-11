using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Coordination;

public interface IWorkerLaneCapacityPolicy
{
    int GetMaximum(WorkerJob.JobKind kind);
}

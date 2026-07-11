namespace NzbWebDAV.Coordination;

public sealed record WorkerLeaseOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
}

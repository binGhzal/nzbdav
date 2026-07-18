namespace NzbWebDAV.Database;

public static class HealthWorkerWakeSignal
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Pulse()
    {
        try
        {
            Signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A queued wake already covers newly committed worker jobs.
        }
    }

    public static Task<bool> WaitAsync(TimeSpan fallbackDelay, CancellationToken ct)
    {
        return Signal.WaitAsync(fallbackDelay, ct);
    }
}

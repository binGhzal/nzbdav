namespace NzbWebDAV.Database;

public static class RcloneInvalidationWakeSignal
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
            // A pending wake already represents all newly committed work.
        }
    }

    public static Task<bool> WaitAsync(TimeSpan fallbackDelay, CancellationToken ct)
    {
        return Signal.WaitAsync(fallbackDelay, ct);
    }
}

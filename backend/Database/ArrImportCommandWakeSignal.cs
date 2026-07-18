namespace NzbWebDAV.Database;

public static class ArrImportCommandWakeSignal
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

    public static async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        return await Signal.WaitAsync(timeout, ct).ConfigureAwait(false);
    }
}

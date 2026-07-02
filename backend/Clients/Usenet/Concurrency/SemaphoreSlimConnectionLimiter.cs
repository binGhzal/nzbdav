namespace NzbWebDAV.Clients.Usenet.Concurrency;

public sealed class SemaphoreSlimConnectionLimiter(SemaphoreSlim semaphore) : IConnectionLimiter
{
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return semaphore.WaitAsync(cancellationToken);
    }

    public void Release()
    {
        semaphore.Release();
    }
}


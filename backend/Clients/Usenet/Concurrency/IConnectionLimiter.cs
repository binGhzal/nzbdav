namespace NzbWebDAV.Clients.Usenet.Concurrency;

public interface IConnectionLimiter
{
    Task WaitAsync(CancellationToken cancellationToken);
    void Release();
}


using NzbWebDAV.Clients.Usenet.Concurrency;

namespace NzbWebDAV.Queue;

public sealed class QueueWorkLaneCoordinator : IDisposable
{
    private const int MaxLanePermits = 128;

    private readonly PrioritizedSemaphore _verifyLane = new(MaxLanePermits, MaxLanePermits);
    private int _verifyActive;

    public int VerifyActive => Volatile.Read(ref _verifyActive);

    public async ValueTask<IDisposable> EnterVerifyAsync(int maxConcurrentVerify, CancellationToken ct)
    {
        var max = Math.Clamp(maxConcurrentVerify, 1, MaxLanePermits);
        _verifyLane.UpdateMaxAllowed(max);
        await _verifyLane.WaitAsync(SemaphorePriority.Low, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _verifyActive);
        return new LaneLease(this);
    }

    public void Dispose()
    {
        _verifyLane.Dispose();
    }

    private sealed class LaneLease(QueueWorkLaneCoordinator owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Interlocked.Decrement(ref owner._verifyActive);
            owner._verifyLane.Release();
        }
    }
}

using UsenetSharp.Concurrency;

namespace NzbWebDAV.Clients.Usenet.Concurrency;

/// <summary>
/// This semaphore maintains two separate queues for waiters:
///   1. A high-priority queue
///   2. A low-priority queue
///
/// When there are both high- and low- priority waiters in their respective queues,
/// dice are rolled to determine which to release, using the given odds from the
/// constructor.
///
/// These configurable odds prevent the high-priority queue from fully starving the
/// low-priority queue.
/// </summary>
public class PrioritizedSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _highPriorityWaiters = [];
    private readonly LinkedList<TaskCompletionSource<bool>> _normalPriorityWaiters = [];
    private readonly LinkedList<TaskCompletionSource<bool>> _lowPriorityWaiters = [];
    private SemaphorePriorityOdds _priorityOdds;
    private int _maxAllowed;
    private int _enteredCount;
    private bool _disposed = false;
    private readonly Lock _lock = new();
    private int _accumulatedOdds;
    private int _normalLowAccumulatedOdds;

    public PrioritizedSemaphore(int initialAllowed, int maxAllowed, SemaphorePriorityOdds? priorityOdds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialAllowed);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAllowed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialAllowed, maxAllowed);
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
        _enteredCount = maxAllowed - initialAllowed;
        _maxAllowed = maxAllowed;
    }

    public Task WaitAsync(SemaphorePriority priority, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_enteredCount < _maxAllowed)
            {
                _enteredCount++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority switch
            {
                SemaphorePriority.High => _highPriorityWaiters,
                SemaphorePriority.Normal => _normalPriorityWaiters,
                _ => _lowPriorityWaiters
            };
            var node = queue.AddLast(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    var removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            queue.Remove(node);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // intentionally left blank
                        }
                    }

                    if (removed)
                        tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }

    public bool TryWait()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_enteredCount >= _maxAllowed)
                return false;

            _enteredCount++;
            return true;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_enteredCount > _maxAllowed)
            {
                // if more threads have entered than are allowed,
                // then don't release any waiter.
                //
                // This can happen when the _maxAllowed gets
                // lowered through the UpdateMaxAllowed method.
                toRelease = null;
            }
            else
            {
                toRelease = SelectWaiterToRelease();
            }

            if (toRelease == null)
            {
                // if no waiters were ultimately released,
                // then decrease the entered count.
                _enteredCount--;
                if (_enteredCount < 0)
                {
                    throw new InvalidOperationException("The semaphore cannot be further released.");
                }

                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    private TaskCompletionSource<bool>? SelectWaiterToRelease()
    {
        if (_highPriorityWaiters.Count == 0)
        {
            // If there are no foreground stream waiters, alternate between verify/repair
            // and bulk download waiters. This keeps completed downloads moving through
            // verification without starving the main queue.
            return ReleaseNormalOrLowWaiter();
        }

        if (_normalPriorityWaiters.Count == 0 && _lowPriorityWaiters.Count == 0)
            return Release(_highPriorityWaiters);

        // if there are both high-priority waiters and non-foreground waiters,
        // then roll the dice to determine which to release, based on the given odds.
        _accumulatedOdds += _priorityOdds.LowPriorityOdds;
        if (_accumulatedOdds >= 100)
        {
            _accumulatedOdds -= 100;
            return ReleaseNormalOrLowWaiter() ?? Release(_highPriorityWaiters);
        }

        return Release(_highPriorityWaiters) ?? ReleaseNormalOrLowWaiter();
    }

    private static TaskCompletionSource<bool>? Release(LinkedList<TaskCompletionSource<bool>> queue)
    {
        while (queue.Count > 0)
        {
            var node = queue.First!;
            queue.RemoveFirst();

            // Skip canceled tasks
            if (!node.Value.Task.IsCanceled)
            {
                return node.Value;
            }
        }

        return null;
    }

    private TaskCompletionSource<bool>? ReleaseNormalOrLowWaiter()
    {
        if (_normalPriorityWaiters.Count == 0)
            return Release(_lowPriorityWaiters);
        if (_lowPriorityWaiters.Count == 0)
            return Release(_normalPriorityWaiters);

        _normalLowAccumulatedOdds += 50;
        if (_normalLowAccumulatedOdds >= 100)
        {
            _normalLowAccumulatedOdds -= 100;
            return Release(_lowPriorityWaiters) ?? Release(_normalPriorityWaiters);
        }

        return Release(_normalPriorityWaiters) ?? Release(_lowPriorityWaiters);
    }

    public void UpdateMaxAllowed(int newMaxAllowed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newMaxAllowed);

        List<TaskCompletionSource<bool>> waitersToRelease = [];
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            _maxAllowed = newMaxAllowed;
            while (_enteredCount < _maxAllowed)
            {
                var waiter = SelectWaiterToRelease();
                if (waiter == null) break;

                _enteredCount++;
                waitersToRelease.Add(waiter);
            }
        }

        foreach (var waiter in waitersToRelease)
            waiter.TrySetResult(true);
    }

    public void UpdatePriorityOdds(SemaphorePriorityOdds newPriorityOdds)
    {
        lock (_lock)
        {
            _priorityOdds = newPriorityOdds;
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = _highPriorityWaiters
                .Concat(_normalPriorityWaiters)
                .Concat(_lowPriorityWaiters)
                .ToList();
            _highPriorityWaiters.Clear();
            _normalPriorityWaiters.Clear();
            _lowPriorityWaiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(AsyncSemaphore)));
    }
}

namespace NzbWebDAV.Queue;

public sealed class QueueProcessingLoop
{
    public static readonly TimeSpan UnexpectedErrorDelay = TimeSpan.FromSeconds(5);

    private readonly Func<bool> _isPaused;
    private readonly Func<CancellationToken, Task<bool>> _tryStartNext;
    private readonly Func<CancellationToken, Task> _waitForWork;
    private readonly Func<Exception, Task> _onUnexpectedError;
    private readonly Func<int, CancellationToken, Task> _delayAfterUnexpectedError;

    public QueueProcessingLoop
    (
        Func<bool> isPaused,
        Func<CancellationToken, Task<bool>> tryStartNext,
        Func<CancellationToken, Task> waitForWork,
        Func<Exception, Task> onUnexpectedError,
        Func<int, CancellationToken, Task>? delayAfterUnexpectedError = null
    )
    {
        _isPaused = isPaused;
        _tryStartNext = tryStartNext;
        _waitForWork = waitForWork;
        _onUnexpectedError = onUnexpectedError;
        _delayAfterUnexpectedError = delayAfterUnexpectedError
                                     ?? ((_, ct) => Task.Delay(UnexpectedErrorDelay, ct));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_isPaused())
                {
                    consecutiveErrors = 0;
                    await _waitForWork(ct).ConfigureAwait(false);
                    continue;
                }

                var startedItem = await _tryStartNext(ct).ConfigureAwait(false);
                if (startedItem)
                {
                    consecutiveErrors = 0;
                    continue;
                }

                consecutiveErrors = 0;
                await _waitForWork(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                consecutiveErrors++;
                await _onUnexpectedError(e).ConfigureAwait(false);
                try
                {
                    await _delayAfterUnexpectedError(consecutiveErrors, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}

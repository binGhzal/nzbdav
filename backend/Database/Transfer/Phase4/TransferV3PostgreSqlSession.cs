using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3PostgreSqlSession
{
    private readonly object _gate = new();
    private readonly TransferV3PostgreSqlTargetDescriptor _descriptor;
    private readonly ITransferV3PostgreSqlProviderOperations _operations;
    private NpgsqlConnection? _connection;
    private SessionState _state = SessionState.Owned;
    private bool _isQuarantined;
    private TransferV3PostgreSqlDeadline? _retryDeadline;
    private TransferV3Phase4CleanupResult _terminalCloseResult;

    internal TransferV3PostgreSqlSession(
        TransferV3PostgreSqlTargetDescriptor descriptor,
        ITransferV3PostgreSqlProviderOperations operations,
        NpgsqlConnection connection,
        TransferV3PostgreSqlTargetIdentity identity,
        int ordinaryCommandTimeoutSeconds)
    {
        _descriptor = descriptor;
        _operations = operations;
        _connection = connection;
        Identity = identity;
        OrdinaryCommandTimeoutSeconds = ordinaryCommandTimeoutSeconds;
    }

    internal TransferV3PostgreSqlTargetIdentity Identity { get; }

    internal string TimeZoneId => _descriptor.TimeZoneId;

    internal int OrdinaryCommandTimeoutSeconds { get; }

    internal bool IsQuarantined
    {
        get
        {
            lock (_gate)
                return _isQuarantined;
        }
    }

    internal NpgsqlConnection BorrowConnection()
    {
        lock (_gate)
        {
            if (_state != SessionState.Owned
                || _isQuarantined
                || _connection is null)
            {
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            }

            return _connection;
        }
    }

    internal void Quarantine()
    {
        lock (_gate)
        {
            _isQuarantined = true;
            if (_state == SessionState.Owned)
                _state = SessionState.Quarantined;
        }
    }

    internal async ValueTask<TransferV3Phase4CleanupResult> CloseWithinAsync(
        TransferV3PostgreSqlDeadline deadline)
    {
        var isRetry = false;
        lock (_gate)
        {
            switch (_state)
            {
                case SessionState.Closed:
                case SessionState.CloseExpired:
                case SessionState.CloseAbandoned:
                    return _terminalCloseResult;
                case SessionState.CloseFaulted:
                    if (deadline is null
                        || !ReferenceEquals(_retryDeadline, deadline))
                    {
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    }
                    _state = SessionState.RetryChecking;
                    isRetry = true;
                    break;
                case SessionState.Owned:
                case SessionState.Quarantined:
                    if (deadline is null)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _isQuarantined = true;
                    _state = SessionState.ClosePreparing;
                    break;
                case SessionState.RetryChecking:
                case SessionState.ClosePreparing:
                case SessionState.CloseRunning:
                default:
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            }
        }

        if (isRetry)
        {
            bool isExpired;
            try
            {
                isExpired = deadline.IsExpired;
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    if (_state != SessionState.RetryChecking)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _state = SessionState.CloseFaulted;
                }

                return TransferV3PostgreSqlBoundedClose.ConnectionCloseFailedResult;
            }

            lock (_gate)
            {
                if (_state != SessionState.RetryChecking)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                if (isExpired)
                {
                    _state = SessionState.CloseFaulted;
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                }

                _state = SessionState.ClosePreparing;
            }
        }

        var execution = await TransferV3PostgreSqlBoundedClose.ExecuteAsync(
                StartCloseProvider,
                PublishSuccessfulClose,
                deadline)
            .ConfigureAwait(false);

        lock (_gate)
        {
            switch (execution.Outcome)
            {
                case TransferV3PostgreSqlCloseOutcome.Success:
                    if (_state != SessionState.Closed)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    return _terminalCloseResult;
                case TransferV3PostgreSqlCloseOutcome.Fault:
                    if (_state is not (SessionState.ClosePreparing
                        or SessionState.CloseRunning))
                    {
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    }
                    _retryDeadline = deadline;
                    _state = SessionState.CloseFaulted;
                    break;
                case TransferV3PostgreSqlCloseOutcome.Expired:
                    if (_state != SessionState.ClosePreparing)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _retryDeadline = null;
                    _terminalCloseResult = execution.Result;
                    _state = SessionState.CloseExpired;
                    break;
                case TransferV3PostgreSqlCloseOutcome.Abandoned:
                    if (_state != SessionState.CloseRunning)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _retryDeadline = null;
                    _terminalCloseResult = execution.Result;
                    _state = SessionState.CloseAbandoned;
                    break;
                default:
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            }

            return execution.Result;
        }
    }

    private ValueTask StartCloseProvider()
    {
        lock (_gate)
        {
            if (_state != SessionState.ClosePreparing || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _state = SessionState.CloseRunning;
            return _operations.DisposeConnectionAsync(_connection);
        }
    }

    private void PublishSuccessfulClose()
    {
        lock (_gate)
        {
            if (_state != SessionState.CloseRunning || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _descriptor.ReleaseLifecycleLease();
            _connection = null;
            _retryDeadline = null;
            _terminalCloseResult = TransferV3PostgreSqlBoundedClose.SuccessResult;
            _state = SessionState.Closed;
        }
    }

    private enum SessionState
    {
        Owned,
        Quarantined,
        RetryChecking,
        ClosePreparing,
        CloseRunning,
        Closed,
        CloseFaulted,
        CloseExpired,
        CloseAbandoned,
    }
}

internal enum TransferV3PostgreSqlCloseOutcome
{
    Success,
    Fault,
    Expired,
    Abandoned,
}

internal readonly record struct TransferV3PostgreSqlCloseExecution(
    TransferV3PostgreSqlCloseOutcome Outcome,
    TransferV3Phase4CleanupResult Result);

internal static class TransferV3PostgreSqlBoundedClose
{
    internal static readonly TransferV3Phase4CleanupResult SuccessResult = new(
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None);

    private static readonly TransferV3Phase4CleanupResult FaultResult = new(
        TransferV3Phase4SecondaryCode.ConnectionCloseFailed,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None);

    private static readonly TransferV3Phase4CleanupResult ExpiredResult = new(
        TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None);

    private static readonly TransferV3Phase4CleanupResult AbandonedResult = new(
        TransferV3Phase4SecondaryCode.DeadlineAbandonedProviderTask,
        TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded,
        TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode.None);

    internal static TransferV3Phase4CleanupResult ConnectionCloseFailedResult =>
        FaultResult;

    internal static async ValueTask<TransferV3PostgreSqlCloseExecution> ExecuteAsync(
        Func<ValueTask> startProvider,
        Action publishSuccessfulClose,
        TransferV3PostgreSqlDeadline deadline)
    {
        ITransferV3PostgreSqlOperationFence fence;
        try
        {
            fence = deadline.CreateOperationFence();
        }
        catch (Exception)
        {
            return Fault();
        }

        if (fence.IsExpired)
            return Finish(fence, Expired());
        if (!fence.CancellationToken.CanBeCanceled)
            return Finish(fence, Fault());

        ValueTask providerOperation;
        try
        {
            providerOperation = startProvider();
        }
        catch (Exception)
        {
            return Finish(fence, Fault());
        }

        bool isProviderOperationCompleted;
        try
        {
            isProviderOperationCompleted = providerOperation.IsCompleted;
        }
        catch (Exception)
        {
            return Finish(fence, Fault());
        }

        if (isProviderOperationCompleted)
        {
            try
            {
                await providerOperation.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Finish(fence, Fault());
            }

            return FinishProvenSuccess(
                fence,
                publishSuccessfulClose,
                default);
        }

        Task providerTask;
        try
        {
            providerTask = providerOperation.AsTask();
        }
        catch (Exception)
        {
            return Finish(fence, Fault());
        }

        var expired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration;
        try
        {
            registration = fence.CancellationToken.UnsafeRegister(
                static state =>
                    ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                expired);
        }
        catch (Exception)
        {
            return Finish(fence, Fault());
        }

        await Task.WhenAny(providerTask, expired.Task).ConfigureAwait(false);

        if (providerTask.IsCompleted)
        {
            try
            {
                await providerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                DisposeIgnoringFailure(registration);
                return Finish(fence, Fault());
            }

            return FinishProvenSuccess(
                fence,
                publishSuccessfulClose,
                registration);
        }

        ObserveLateFault(providerTask);
        DisposeIgnoringFailure(registration);
        DisposeIgnoringFailure(fence);
        return Abandoned();
    }

    private static TransferV3PostgreSqlCloseExecution Finish(
        ITransferV3PostgreSqlOperationFence fence,
        TransferV3PostgreSqlCloseExecution candidate)
    {
        try
        {
            fence.Dispose();
            return candidate;
        }
        catch (Exception)
        {
            return Fault();
        }
    }

    private static TransferV3PostgreSqlCloseExecution FinishProvenSuccess(
        ITransferV3PostgreSqlOperationFence fence,
        Action publishSuccessfulClose,
        CancellationTokenRegistration registration)
    {
        try
        {
            publishSuccessfulClose();
        }
        catch (Exception)
        {
            DisposeIgnoringFailure(registration);
            DisposeIgnoringFailure(fence);
            throw;
        }

        DisposeIgnoringFailure(registration);
        DisposeIgnoringFailure(fence);
        return Success();
    }

    private static void DisposeIgnoringFailure(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static void ObserveLateFault(Task providerTask)
    {
        _ = providerTask.ContinueWith(
            static completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
            | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TransferV3PostgreSqlCloseExecution Success() =>
        new(TransferV3PostgreSqlCloseOutcome.Success, SuccessResult);

    private static TransferV3PostgreSqlCloseExecution Fault() =>
        new(TransferV3PostgreSqlCloseOutcome.Fault, FaultResult);

    private static TransferV3PostgreSqlCloseExecution Expired() =>
        new(TransferV3PostgreSqlCloseOutcome.Expired, ExpiredResult);

    private static TransferV3PostgreSqlCloseExecution Abandoned() =>
        new(TransferV3PostgreSqlCloseOutcome.Abandoned, AbandonedResult);
}

internal static class TransferV3PostgreSqlLifecycleFailure
{
    internal static TransferV3Phase4Exception Unexpected() =>
        TransferV3Phase4Exception.Create(
            new InvalidOperationException(),
            TransferV3Phase4Boundary.Unexpected);
}

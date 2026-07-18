using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3PostgreSqlOpenAttempt
{
    private readonly object _gate = new();
    private readonly TransferV3PostgreSqlTargetDescriptor _descriptor;
    private readonly ITransferV3PostgreSqlProviderOperations _operations;
    private NpgsqlConnection? _connection;
    private AttemptState _state = AttemptState.Created;
    private TransferV3PostgreSqlDeadline? _retryDeadline;
    private TransferV3Phase4CleanupResult _terminalCloseResult;

    internal TransferV3PostgreSqlOpenAttempt(
        TransferV3PostgreSqlTargetDescriptor descriptor,
        ITransferV3PostgreSqlProviderOperations operations)
    {
        _descriptor = descriptor;
        _operations = operations;
    }

    internal void AttachUnpublishedConnection(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    internal async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        Task? providerTask = null;
        Exception? providerFailure = null;
        lock (_gate)
        {
            if (_state != AttemptState.Created)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _state = AttemptState.Opening;
            try
            {
                if (_connection is null
                    || _connection.State != System.Data.ConnectionState.Closed)
                {
                    throw new InvalidOperationException();
                }

                providerTask = _operations.OpenAsync(
                    _connection,
                    cancellationToken);
                if (providerTask is null)
                    throw new InvalidOperationException();
            }
            catch (Exception raw)
            {
                providerFailure = raw;
            }
        }

        if (providerFailure is null)
        {
            try
            {
                await providerTask!.ConfigureAwait(false);
            }
            catch (Exception raw)
            {
                providerFailure = raw;
            }
        }

        lock (_gate)
        {
            if (_state == AttemptState.Abandoned)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            if (_state != AttemptState.Opening)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _state = providerFailure is null
                ? AttemptState.Opened
                : AttemptState.OpenFailed;
        }

        if (providerFailure is not null)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                providerFailure,
                TransferV3Phase4Boundary.PostgreSqlOpen,
                cancellationToken);
        }
    }

    internal ValueTask<TransferV3PostgreSqlSession> ValidateFirstAsync(
        string sourceTimeZoneId,
        CancellationToken cancellationToken) =>
        ValidateCoreAsync(
            sourceTimeZoneId,
            expected: null,
            requireIdentityMatch: false,
            deadline: null,
            cancellationToken);

    internal ValueTask<TransferV3PostgreSqlSession> ValidateMatchingAsync(
        string sourceTimeZoneId,
        TransferV3PostgreSqlTargetIdentity expected,
        CancellationToken cancellationToken) =>
        ValidateCoreAsync(
            sourceTimeZoneId,
            expected,
            requireIdentityMatch: true,
            deadline: null,
            cancellationToken);

    internal ValueTask<TransferV3PostgreSqlSession> ValidateMatchingWithinAsync(
        string sourceTimeZoneId,
        TransferV3PostgreSqlTargetIdentity expected,
        TransferV3PostgreSqlDeadline deadline)
    {
        if (deadline is null)
        {
            return ValueTask.FromException<TransferV3PostgreSqlSession>(
                TransferV3PostgreSqlLifecycleFailure.Unexpected());
        }

        return ValidateCoreAsync(
            sourceTimeZoneId,
            expected,
            requireIdentityMatch: true,
            deadline,
            default);
    }

    internal async ValueTask<TransferV3Phase4CleanupResult> CloseWithinAsync(
        TransferV3PostgreSqlDeadline deadline)
    {
        var isRetry = false;
        lock (_gate)
        {
            switch (_state)
            {
                case AttemptState.Closed:
                case AttemptState.CloseExpired:
                case AttemptState.CloseAbandoned:
                    return _terminalCloseResult;
                case AttemptState.CloseFaulted:
                    if (deadline is null
                        || !ReferenceEquals(_retryDeadline, deadline))
                    {
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    }
                    _state = AttemptState.RetryChecking;
                    isRetry = true;
                    break;
                case AttemptState.Created:
                case AttemptState.Opened:
                case AttemptState.OpenFailed:
                case AttemptState.ValidationFailed:
                    if (deadline is null)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _state = AttemptState.ClosePreparing;
                    break;
                case AttemptState.Opening:
                case AttemptState.Validating:
                case AttemptState.RetryChecking:
                case AttemptState.ClosePreparing:
                case AttemptState.CloseRunning:
                case AttemptState.Transferred:
                case AttemptState.Abandoned:
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
                    if (_state == AttemptState.Abandoned)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    if (_state != AttemptState.RetryChecking)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _state = AttemptState.CloseFaulted;
                }

                return TransferV3PostgreSqlBoundedClose.ConnectionCloseFailedResult;
            }

            lock (_gate)
            {
                if (_state == AttemptState.Abandoned)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                if (_state != AttemptState.RetryChecking)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                if (isExpired)
                {
                    _state = AttemptState.CloseFaulted;
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                }

                _state = AttemptState.ClosePreparing;
            }
        }

        var execution = await TransferV3PostgreSqlBoundedClose.ExecuteAsync(
                StartCloseProvider,
                PublishSuccessfulClose,
                deadline)
            .ConfigureAwait(false);

        lock (_gate)
        {
            if (_state == AttemptState.Abandoned)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            switch (execution.Outcome)
            {
                case TransferV3PostgreSqlCloseOutcome.Success:
                    if (_state != AttemptState.Closed)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    return _terminalCloseResult;
                case TransferV3PostgreSqlCloseOutcome.Fault:
                    if (_state is not (AttemptState.ClosePreparing
                        or AttemptState.CloseRunning))
                    {
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    }
                    _retryDeadline = deadline;
                    _state = AttemptState.CloseFaulted;
                    break;
                case TransferV3PostgreSqlCloseOutcome.Expired:
                    if (_state != AttemptState.ClosePreparing)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _retryDeadline = null;
                    _terminalCloseResult = execution.Result;
                    _state = AttemptState.CloseExpired;
                    break;
                case TransferV3PostgreSqlCloseOutcome.Abandoned:
                    if (_state != AttemptState.CloseRunning)
                        throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                    _retryDeadline = null;
                    _terminalCloseResult = execution.Result;
                    _state = AttemptState.CloseAbandoned;
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
            if (_state != AttemptState.ClosePreparing || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _state = AttemptState.CloseRunning;
            return _operations.DisposeConnectionAsync(_connection);
        }
    }

    private void PublishSuccessfulClose()
    {
        lock (_gate)
        {
            if (_state == AttemptState.Abandoned)
                return;
            if (_state != AttemptState.CloseRunning || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

            _descriptor.ReleaseLifecycleLease();
            _connection = null;
            _retryDeadline = null;
            _terminalCloseResult = TransferV3PostgreSqlBoundedClose.SuccessResult;
            _state = AttemptState.Closed;
        }
    }

    internal void AbandonForHelperExit()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case AttemptState.Abandoned:
                case AttemptState.CloseExpired:
                case AttemptState.CloseAbandoned:
                    return;
                case AttemptState.Created:
                case AttemptState.Opening:
                case AttemptState.Opened:
                case AttemptState.OpenFailed:
                case AttemptState.Validating:
                case AttemptState.ValidationFailed:
                case AttemptState.RetryChecking:
                case AttemptState.ClosePreparing:
                case AttemptState.CloseRunning:
                case AttemptState.CloseFaulted:
                    _retryDeadline = null;
                    _state = AttemptState.Abandoned;
                    return;
                case AttemptState.Transferred:
                case AttemptState.Closed:
                default:
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            }
        }
    }

    private async ValueTask<TransferV3PostgreSqlSession> ValidateCoreAsync(
        string sourceTimeZoneId,
        TransferV3PostgreSqlTargetIdentity? expected,
        bool requireIdentityMatch,
        TransferV3PostgreSqlDeadline? deadline,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != AttemptState.Opened)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            _state = AttemptState.Validating;
        }

        try
        {
            ValidateTimeZoneEquality(sourceTimeZoneId);

            TransferV3PostgreSqlTargetIdentity identity;
            string environmentSchema;
            if (deadline is null)
            {
                identity = await StartServerValidation(
                        TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureStillValidating();
                if (identity is null)
                    throw new InvalidOperationException();
                environmentSchema = await StartEnvironmentValidation(
                        TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureStillValidating();
            }
            else
            {
                identity = await ValidateServerWithinAsync(deadline)
                    .ConfigureAwait(false);
                if (identity is null)
                    throw new InvalidOperationException();
                environmentSchema = await ValidateEnvironmentWithinAsync(deadline)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(
                    environmentSchema,
                    identity.SchemaName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    environmentSchema,
                    _descriptor.TargetSchema,
                    StringComparison.Ordinal)
                || (requireIdentityMatch && !Equals(identity, expected)))
            {
                throw new InvalidOperationException();
            }

            lock (_gate)
            {
                if (_state == AttemptState.Abandoned)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                if (_state != AttemptState.Validating || _connection is null)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();

                var session = new TransferV3PostgreSqlSession(
                    _descriptor,
                    _operations,
                    _connection,
                    identity,
                    TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds);
                _connection = null;
                _state = AttemptState.Transferred;
                return session;
            }
        }
        catch (Exception raw)
        {
            lock (_gate)
            {
                if (_state == AttemptState.Abandoned)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                if (_state != AttemptState.Validating)
                    throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
                _state = AttemptState.ValidationFailed;
            }

            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }
    }

    private Task<TransferV3PostgreSqlTargetIdentity> StartServerValidation(
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != AttemptState.Validating || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            return _operations.ValidateServerAsync(
                       _connection,
                       _descriptor.TimeZoneId,
                       commandTimeoutSeconds,
                       cancellationToken)
                   ?? throw new InvalidOperationException();
        }
    }

    private Task<string> StartEnvironmentValidation(
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != AttemptState.Validating || _connection is null)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
            return _operations.ValidateEnvironmentAsync(
                       _connection,
                       commandTimeoutSeconds,
                       cancellationToken)
                   ?? throw new InvalidOperationException();
        }
    }

    private async Task<TransferV3PostgreSqlTargetIdentity> ValidateServerWithinAsync(
        TransferV3PostgreSqlDeadline deadline)
    {
        var fence = deadline.CreateCommandFence(
            TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds);
        var hasPrimaryFailure = false;
        try
        {
            if (fence.IsExpired)
                throw new InvalidOperationException();
            var result = await StartServerValidation(
                    fence.CommandTimeoutSeconds,
                    fence.CancellationToken)
                .ConfigureAwait(false);
            EnsureStillValidating();
            return result;
        }
        catch (Exception raw)
        {
            hasPrimaryFailure = true;
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                default);
        }
        finally
        {
            FinishValidationFence(fence, deadline, hasPrimaryFailure);
        }
    }

    private async Task<string> ValidateEnvironmentWithinAsync(
        TransferV3PostgreSqlDeadline deadline)
    {
        var fence = deadline.CreateCommandFence(
            TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds);
        var hasPrimaryFailure = false;
        try
        {
            if (fence.IsExpired)
                throw new InvalidOperationException();
            var result = await StartEnvironmentValidation(
                    fence.CommandTimeoutSeconds,
                    fence.CancellationToken)
                .ConfigureAwait(false);
            EnsureStillValidating();
            return result;
        }
        catch (Exception raw)
        {
            hasPrimaryFailure = true;
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                default);
        }
        finally
        {
            FinishValidationFence(fence, deadline, hasPrimaryFailure);
        }
    }

    private void FinishValidationFence(
        TransferV3PostgreSqlCommandFence fence,
        TransferV3PostgreSqlDeadline deadline,
        bool hasPrimaryFailure)
    {
        if (!hasPrimaryFailure)
        {
            fence.Dispose();
            EnsureStillValidating();
            if (deadline.IsExpired)
                throw new InvalidOperationException();
            return;
        }

        try
        {
            fence.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void EnsureStillValidating()
    {
        lock (_gate)
        {
            if (_state != AttemptState.Validating)
                throw TransferV3PostgreSqlLifecycleFailure.Unexpected();
        }
    }

    private void ValidateTimeZoneEquality(string sourceTimeZoneId)
    {
        var localTimeZoneId = TimeZoneInfo.Local.Id;
        var environmentTimeZoneId = Environment.GetEnvironmentVariable(
            PostgreSqlConnectionPolicy.LegacyTimezoneVariable);
        if (string.IsNullOrEmpty(sourceTimeZoneId)
            || !string.Equals(
                sourceTimeZoneId,
                environmentTimeZoneId,
                StringComparison.Ordinal)
            || !string.Equals(
                sourceTimeZoneId,
                localTimeZoneId,
                StringComparison.Ordinal)
            || !string.Equals(
                sourceTimeZoneId,
                _descriptor.TimeZoneId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }
    }

    private enum AttemptState
    {
        Created,
        Opening,
        Opened,
        OpenFailed,
        Validating,
        ValidationFailed,
        Transferred,
        RetryChecking,
        ClosePreparing,
        CloseRunning,
        Closed,
        CloseFaulted,
        CloseExpired,
        CloseAbandoned,
        Abandoned,
    }
}

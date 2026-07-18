using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Database.Transfer;

internal readonly record struct TransferV3SourceProvenance(
    TransferV3FileIdentity DatabaseIdentity,
    TransferV3FileIdentity BlobRootIdentity,
    string SourceTimeZoneId);

internal enum TransferV3SqliteExportSessionState
{
    Ready,
    Running,
    Completed,
    Faulted,
}

internal sealed record TransferV3SqliteExportSessionHooks(Action<string>? BeforeCleanupStep = null);

internal sealed class TransferV3SqliteExportContext
{
    private readonly TransferV3SqliteExportSession _owner;
    private int _active = 1;

    internal TransferV3SqliteExportContext(TransferV3SqliteExportSession owner)
    {
        _owner = owner;
    }

    internal SqliteConnection Connection
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveConnection();
        }
    }

    internal SqliteTransaction Transaction
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveTransaction();
        }
    }

    internal TransferV3SourceContract Contract
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveContract();
        }
    }

    internal TransferV3ValidatedSource Validation
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveValidation();
        }
    }

    internal TransferV3SourceProvenance Provenance
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveProvenance();
        }
    }

    internal TransferV3BlobSourceGuard BlobSource
    {
        get
        {
            EnsureActive();
            return _owner.GetActiveBlobSource();
        }
    }

    internal void Invalidate() => Interlocked.Exchange(ref _active, 0);

    private void EnsureActive()
    {
        if (Volatile.Read(ref _active) == 0)
            throw new InvalidOperationException(
                "The Transfer-v3 export context is valid only during its serialized callback.");
    }
}

internal sealed class TransferV3SqliteExportSession : IAsyncDisposable
{
    internal const string CleanupFailuresDataKey = "transfer-v3-cleanup-failures";
    private readonly object _sync = new();
    private readonly TransferV3SourceContract _contract;
    private readonly TransferV3ValidatedSource _validation;
    private readonly TransferV3SourceProvenance _provenance;
    private readonly IProgress<TransferV3ValidationProgress>? _progress;
    private readonly TransferV3SqliteExportSessionHooks? _hooks;
    private readonly TaskCompletionSource _runCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransferV3SqliteSourceGuard? _sourceGuard;
    private TransferV3BlobSourceGuard? _blobGuard;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private TransferV3SqliteExportSessionState _state = TransferV3SqliteExportSessionState.Ready;
    private bool _disposed;

    internal TransferV3SqliteExportSession(
        TransferV3SourceContract contract,
        TransferV3ValidatedSource validation,
        TransferV3SourceProvenance provenance,
        TransferV3SqliteSourceGuard sourceGuard,
        TransferV3BlobSourceGuard blobGuard,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<TransferV3ValidationProgress>? progress,
        TransferV3SqliteExportSessionHooks? hooks)
    {
        _contract = contract;
        _validation = validation;
        _provenance = provenance;
        _sourceGuard = sourceGuard;
        _blobGuard = blobGuard;
        _connection = connection;
        _transaction = transaction;
        _progress = progress;
        _hooks = hooks;
    }

    internal TransferV3SqliteExportSessionState State
    {
        get
        {
            lock (_sync) return _state;
        }
    }

    internal TransferV3ValidatedSource Validation => _validation;
    internal TransferV3SourceProvenance Provenance => _provenance;

    internal Task RunExportAsync(
        Func<TransferV3SqliteExportContext, CancellationToken, Task> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunExportCoreAsync(async (context, token) =>
        {
            await callback(context, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    internal Task<TResult> RunExportAsync<TResult>(
        Func<TransferV3SqliteExportContext, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunExportCoreAsync(callback, cancellationToken);
    }

    private async Task<TResult> RunExportCoreAsync<TResult>(
        Func<TransferV3SqliteExportContext, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed || _state != TransferV3SqliteExportSessionState.Ready)
                throw new InvalidOperationException("The Transfer-v3 export session is single-use and is not ready.");
            _state = TransferV3SqliteExportSessionState.Running;
        }

        Exception? primary = null;
        var cleanup = new List<Exception>();
        var cleanupHooksInvoked = false;
        TResult? result = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new TransferV3SqliteExportContext(this);
            try
            {
                result = await callback(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context.Invalidate();
            }
            _progress?.Report(new TransferV3ValidationProgress(
                "export-callback-completed", null, 0));
            cancellationToken.ThrowIfCancellationRequested();

            var source = RequireSourceGuard();
            var blobs = RequireBlobGuard();
            var connection = RequireConnection();
            var transaction = RequireTransaction();
            blobs.VerifyUnchanged();
            source.VerifyUnchanged();
            await TransferV3BlobInventoryScanner.VerifyRetainedAsync(
                    blobs, connection, transaction, _progress, cancellationToken)
                .ConfigureAwait(false);
            source.VerifyUnchanged();
            blobs.VerifyUnchanged();
            _progress?.Report(new TransferV3ValidationProgress(
                "export-precommit-verified", null, 0));

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _progress?.Report(new TransferV3ValidationProgress(
                "export-transaction-committed", null, 0));

            cleanup.AddRange(InvokeCleanupHooks());
            cleanupHooksInvoked = true;
            blobs.VerifyUnchanged();
            source.VerifyUnchanged();
            await TransferV3BlobInventoryScanner.VerifyRetainedAsync(
                    blobs, connection, transaction: null, progress: null, cancellationToken)
                .ConfigureAwait(false);
            source.VerifyUnchanged();
            blobs.VerifyUnchanged();
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        cleanup.AddRange(await CleanupCoreAsync(invokeHooks: !cleanupHooksInvoked).ConfigureAwait(false));
        lock (_sync)
        {
            _disposed = true;
            _state = primary is null && cleanup.Count == 0
                ? TransferV3SqliteExportSessionState.Completed
                : TransferV3SqliteExportSessionState.Faulted;
        }
        _runCompletion.TrySetResult();

        if (primary is not null)
        {
            if (cleanup.Count != 0)
                primary.Data[CleanupFailuresDataKey] = cleanup.AsReadOnly();
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0)
            throw new AggregateException("Transfer-v3 ordered session cleanup failed.", cleanup);
        return result!;
    }

    public async ValueTask DisposeAsync()
    {
        Task? running = null;
        lock (_sync)
        {
            if (_disposed) return;
            if (_state == TransferV3SqliteExportSessionState.Running)
            {
                running = _runCompletion.Task;
            }
            else
            {
                _disposed = true;
                if (_state == TransferV3SqliteExportSessionState.Ready)
                    _state = TransferV3SqliteExportSessionState.Faulted;
            }
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
            return;
        }

        var cleanup = await CleanupCoreAsync().ConfigureAwait(false);
        if (cleanup.Count != 0)
            throw new AggregateException("Transfer-v3 ordered session cleanup failed.", cleanup);
    }

    internal SqliteConnection GetActiveConnection()
    {
        EnsureContextActive();
        return RequireConnection();
    }

    internal SqliteTransaction GetActiveTransaction()
    {
        EnsureContextActive();
        return RequireTransaction();
    }

    internal TransferV3SourceContract GetActiveContract()
    {
        EnsureContextActive();
        return _contract;
    }

    internal TransferV3ValidatedSource GetActiveValidation()
    {
        EnsureContextActive();
        return _validation;
    }

    internal TransferV3SourceProvenance GetActiveProvenance()
    {
        EnsureContextActive();
        return _provenance;
    }

    internal TransferV3BlobSourceGuard GetActiveBlobSource()
    {
        EnsureContextActive();
        return RequireBlobGuard();
    }

    private void EnsureContextActive()
    {
        lock (_sync)
        {
            if (_disposed || _state != TransferV3SqliteExportSessionState.Running)
                throw new InvalidOperationException("The Transfer-v3 export context is no longer active.");
        }
    }

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("The Transfer-v3 validation connection is closed.");

    private SqliteTransaction RequireTransaction() =>
        _transaction ?? throw new InvalidOperationException("The Transfer-v3 validation transaction is closed.");

    private TransferV3SqliteSourceGuard RequireSourceGuard() =>
        _sourceGuard ?? throw new InvalidOperationException("The Transfer-v3 source guard is closed.");

    private TransferV3BlobSourceGuard RequireBlobGuard() =>
        _blobGuard ?? throw new InvalidOperationException("The Transfer-v3 blob guard is closed.");

    private List<Exception> InvokeCleanupHooks()
    {
        var failures = new List<Exception>();
        InvokeCleanupHook("transaction", _transaction is not null, failures);
        InvokeCleanupHook("connection", _connection is not null, failures);
        InvokeCleanupHook("blob-guard", _blobGuard is not null, failures);
        InvokeCleanupHook("source-guard", _sourceGuard is not null, failures);
        return failures;
    }

    private void InvokeCleanupHook(string step, bool resourceIsPresent, List<Exception> failures)
    {
        if (!resourceIsPresent) return;
        try
        {
            _hooks?.BeforeCleanupStep?.Invoke(step);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task<List<Exception>> CleanupCoreAsync(bool invokeHooks = true)
    {
        var failures = new List<Exception>();
        var transaction = Interlocked.Exchange(ref _transaction, null);
        await CleanupStepAsync(
            "transaction",
            transaction is null ? null : () => transaction.DisposeAsync(),
            invokeHooks,
            failures).ConfigureAwait(false);
        var connection = Interlocked.Exchange(ref _connection, null);
        await CleanupStepAsync(
            "connection",
            connection is null ? null : () => connection.DisposeAsync(),
            invokeHooks,
            failures).ConfigureAwait(false);
        var blobs = Interlocked.Exchange(ref _blobGuard, null);
        await CleanupStepAsync(
            "blob-guard",
            blobs is null ? null : () =>
            {
                blobs.Dispose();
                return ValueTask.CompletedTask;
            },
            invokeHooks,
            failures).ConfigureAwait(false);
        var source = Interlocked.Exchange(ref _sourceGuard, null);
        await CleanupStepAsync(
            "source-guard",
            source is null ? null : () =>
            {
                source.Dispose();
                return ValueTask.CompletedTask;
            },
            invokeHooks,
            failures).ConfigureAwait(false);
        return failures;
    }

    private async ValueTask CleanupStepAsync(
        string step,
        Func<ValueTask>? cleanup,
        bool invokeHook,
        List<Exception> failures)
    {
        if (cleanup is null) return;
        if (invokeHook)
        {
            try
            {
                _hooks?.BeforeCleanupStep?.Invoke(step);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}

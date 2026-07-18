using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal readonly record struct TransferV3DurableCloseSnapshot(
    bool Completed,
    Exception? Failure);

// The close operation and its observable result share one lock. A concurrent
// manifest check therefore sees either "not started" or the final durable-close
// result; it can never observe FileStream's base disposed state in the middle of
// a still-running flush/close operation.
internal sealed class TransferV3DurableCloseState
{
    private readonly object _gate = new();
    private bool _completed;
    private Exception? _failure;

    internal TransferV3DurableCloseSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new TransferV3DurableCloseSnapshot(_completed, _failure);
        }
    }

    internal void ExecuteClose(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);
        Exception? failure = null;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            try
            {
                close();
            }
            catch (Exception exception)
            {
                _failure = exception;
                failure = exception;
            }
            finally
            {
                _completed = true;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

internal sealed class TransferV3DurableFileStream : FileStream
{
    private readonly TransferV3DurableCloseState _closeState = new();

    internal TransferV3DurableFileStream(SafeFileHandle handle)
        : base(handle, FileAccess.Write, bufferSize: 64 * 1024, isAsync: false)
    {
    }

    internal TransferV3DurableCloseSnapshot GetDurableCloseSnapshot() =>
        _closeState.GetSnapshot();

    protected override void Dispose(bool disposing)
    {
        _closeState.ExecuteClose(() =>
        {
            Exception? flushFailure = null;
            Exception? closeFailure = null;
            if (disposing)
            {
                try
                {
                    Flush(flushToDisk: true);
                }
                catch (Exception exception)
                {
                    flushFailure = exception;
                }
            }

            try
            {
                base.Dispose(disposing);
            }
            catch (Exception exception)
            {
                closeFailure = exception;
            }

            var failure = CombineFailures(flushFailure, closeFailure);
            if (failure is not null)
            {
                throw failure;
            }
        });
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static Exception? CombineFailures(Exception? primary, Exception? cleanup)
    {
        if (primary is null)
        {
            return cleanup;
        }

        return cleanup is null
            ? primary
            : new AggregateException(
                "Durable file flush and descriptor close both failed.",
                primary,
                cleanup);
    }
}

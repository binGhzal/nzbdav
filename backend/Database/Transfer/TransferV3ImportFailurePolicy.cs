using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3ImportFailurePolicy
{
    internal const string SecondaryFailureDataKey = "TransferV3FailedStateCasFailure";
    internal const string ZeroRowsDiagnostic =
        "The failed-state compare-and-swap did not change exactly one row.";
    internal const string CallbackFailureDiagnostic =
        "The failed-state compare-and-swap failed and its commit outcome is unknown.";

    internal static async Task RethrowPrimaryAsync(
        Exception primary,
        Func<Task<bool>> tryMarkFailedAsync)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(tryMarkFailedAsync);
        var primaryDispatch = ExceptionDispatchInfo.Capture(primary);

        try
        {
            if (!await tryMarkFailedAsync().ConfigureAwait(false))
                primary.Data[SecondaryFailureDataKey] = ZeroRowsDiagnostic;
        }
        catch (Exception)
        {
            primary.Data[SecondaryFailureDataKey] = CallbackFailureDiagnostic;
        }

        primaryDispatch.Throw();
        throw new UnreachableException();
    }
}

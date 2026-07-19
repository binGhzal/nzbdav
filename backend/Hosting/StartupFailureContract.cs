using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace NzbWebDAV.Hosting;

internal static class StartupFailureContract
{
    internal const string LegacyUpgradeRefusalMessage =
        "Legacy in-place database upgrades are not supported by V1.";

    private static readonly HashSet<string> SafeMessages = new(StringComparer.Ordinal)
    {
        MaintenanceCommandLine.InvalidArgumentsMessage,
        MaintenanceCommandLine.TransferV3UnavailableMessage,
        MaintenanceCommandLine.PostgreSqlUnavailableMessage,
        TransferV3StartupGuard.RefusalMessage,
        TransferV3StartupGuard.ValidationFailureMessage,
        DavDatabaseContextRuntimeFactory.UnsupportedProviderMessage,
        LegacyUpgradeRefusalMessage,
        "NZBDAV_ROLE 'Control' is defined but not executable until its service implementation is installed.",
        "NZBDAV_ROLE 'Gateway' is defined but not executable until its service implementation is installed.",
        "NZBDAV_ROLE 'WorkerDownload' is defined but not executable until its service implementation is installed.",
        "NZBDAV_ROLE 'WorkerVerify' is defined but not executable until its service implementation is installed.",
        "NZBDAV_ROLE 'WorkerRepair' is defined but not executable until its service implementation is installed.",
        "NZBDAV_ROLE 'Ui' is defined but not executable until its service implementation is installed.",
    };

    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Message.StartsWith("Unsupported NZBDAV_ROLE '", StringComparison.Ordinal))
            return "startup_failure code=startup_invalid_role";
        if (SafeMessages.Contains(exception.Message))
            return $"startup_failure code=startup_refused message={exception.Message}";
        return "startup_failure code=startup_failed";
    }
}

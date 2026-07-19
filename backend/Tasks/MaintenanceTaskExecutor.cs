using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tasks;

public sealed class MaintenanceTaskExecutor(
    ConfigManager configManager,
    WebsocketManager websocketManager) : IMaintenanceTaskExecutor
{
    public async Task ExecuteAsync(
        MaintenanceRunKind kind,
        MaintenanceProgressReporter report,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case MaintenanceRunKind.RemoveUnlinkedFiles:
                await new RemoveUnlinkedFilesTask(
                        configManager,
                        websocketManager,
                        isDryRun: false,
                        report)
                    .Execute(cancellationToken)
                    .ConfigureAwait(false);
                return;
            case MaintenanceRunKind.RemoveUnlinkedFilesDryRun:
                await new RemoveUnlinkedFilesTask(
                        configManager,
                        websocketManager,
                        isDryRun: true,
                        report)
                    .Execute(cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported maintenance run kind.");
        }
    }
}

using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// </summary>
public class ArrMonitoringService : BackgroundService
{
    private readonly ConfigManager _configManager;
    private readonly ArrQueueResolutionLimiter _resolutionLimiter = new();
    private bool _loggedStartupGrace;

    public ArrMonitoringService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Ensure delay runs on each iteration
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            // if all queue-actions are disabled, then do nothing
            var arrConfig = _configManager.GetArrConfig();
            if (arrConfig.QueueRules.All(x => x.Action == ArrConfig.QueueAction.DoNothing))
                continue;

            if (_resolutionLimiter.IsStartupGraceActive)
            {
                if (!_loggedStartupGrace)
                {
                    Log.Information(
                        "ARR stuck queue cleanup is paused during the startup grace period.");
                    _loggedStartupGrace = true;
                }

                continue;
            }

            // otherwise, handle stuck queue items according to the config
            foreach (var arrClient in arrConfig.GetArrClients())
                await HandleStuckQueueItems(arrConfig, arrClient).ConfigureAwait(false);
        }
    }

    private async Task HandleStuckQueueItems(ArrConfig arrConfig, ArrClient client)
    {
        try
        {
            var queueStatus = await client.GetQueueStatusAsync().ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync().ConfigureAwait(false);
            var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message);
            var stuckRecords = queue.Records.Where(x => actionableStatuses.Any(x.HasStatusMessage));
            var actionsThisRun = 0;
            foreach (var record in stuckRecords)
            {
                if (actionsThisRun >= ArrQueueResolutionLimiter.MaxActionsPerClientRun)
                {
                    Log.Warning(
                        "ARR stuck queue cleanup for {Host} reached the per-pass cap of {MaxActions}; " +
                        "remaining matching records will be retried on a later pass.",
                        client.Host,
                        ArrQueueResolutionLimiter.MaxActionsPerClientRun);
                    break;
                }

                if (await HandleStuckQueueItem(record, arrConfig, client).ConfigureAwait(false))
                    actionsThisRun++;
            }
        }
        catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
        {
            Log.Debug($"Could not reach Arr instance `{client.Host}` for queue monitoring: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"Error occured while monitoring queue for `{client.Host}`: {e.Message}");
        }
    }

    private async Task<bool> HandleStuckQueueItem(ArrQueueRecord item, ArrConfig arrConfig, ArrClient client)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var action = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return false;
        if (!_resolutionLimiter.TryAcquire(client.Host, item.Id.ToString(), action)) return false;

        await client.DeleteQueueRecord(item.Id, action).ConfigureAwait(false);
        Log.Warning("Resolved stuck queue item `{Title}` from `{Host}`, with action `{Action}`",
            item.Title, client.Host, action);
        return true;
    }
}

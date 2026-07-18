using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Runs the RemoveUnlinkedFilesTask daily at the configured time when scheduling is enabled.
/// </summary>
public class RemoveOrphanedFilesSchedulerService : BackgroundService
{
    private readonly ConfigManager _configManager;
    private readonly MaintenanceRunService _maintenanceRunService;
    private CancellationTokenSource _rescheduleCts = new();

    public RemoveOrphanedFilesSchedulerService(
        ConfigManager configManager,
        MaintenanceRunService maintenanceRunService)
    {
        _configManager = configManager;
        _maintenanceRunService = maintenanceRunService;

        _configManager.OnConfigChanged += (_, args) =>
        {
            if (!args.ChangedConfig.ContainsKey("maintenance.remove-orphaned-schedule-enabled") &&
                !args.ChangedConfig.ContainsKey("maintenance.remove-orphaned-schedule-time"))
                return;

            var old = Interlocked.Exchange(ref _rescheduleCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_configManager.IsRemoveOrphanedFilesScheduleEnabled())
                {
                    using var disabledLinked = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken, _rescheduleCts.Token);
                    await Task.Delay(Timeout.Infinite, disabledLinked.Token).ConfigureAwait(false);
                    continue;
                }

                var scheduleTime = _configManager.RemoveOrphanedFilesSchedule();
                var now = DateTime.Now;
                var todayRun = now.Date + scheduleTime;
                var nextRun = todayRun > now ? todayRun : todayRun.AddDays(1);
                var delay = nextRun - now;

                Log.Information("RemoveOrphanedFilesScheduler: next run scheduled at {NextRun}", nextRun);

                using var delayLinked = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken, _rescheduleCts.Token);
                await Task.Delay(delay, delayLinked.Token).ConfigureAwait(false);

                Log.Information("RemoveOrphanedFilesScheduler: running scheduled Remove Orphaned Files task");
                var result = await _maintenanceRunService.TryStartRunAsync(
                        MaintenanceRunKind.RemoveUnlinkedFiles,
                        requestedBy: "scheduled",
                        stoppingToken)
                    .ConfigureAwait(false);
                if (!result.Started)
                {
                    Log.Warning(
                        "RemoveOrphanedFilesScheduler: skipped because maintenance run {MaintenanceRunId} is active.",
                        result.Run.Id);
                }
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Config changed — loop and recompute the next run time
            }
            catch (Exception e)
            {
                Log.Error(e, "RemoveOrphanedFilesScheduler: error running scheduled task: {Message}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

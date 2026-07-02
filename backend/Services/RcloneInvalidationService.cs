using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class RcloneInvalidationService : BackgroundService
{
    private const int BatchSize = 250;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisabledDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!RcloneClient.IsRemoteControlEnabled || RcloneClient.Host == null)
                {
                    await Task.Delay(DisabledDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using var dbContext = new DavDatabaseContext();
                var now = DateTimeOffset.UtcNow;
                var items = await dbContext.RcloneInvalidationItems
                    .Where(x => x.NextAttemptAt <= now)
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (items.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var paths = items
                    .Select(x => x.Path)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var result = await RcloneClient.ForgetVfsPaths(paths).ConfigureAwait(false);
                if (result.Success)
                {
                    dbContext.RcloneInvalidationItems.RemoveRange(items);
                    await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var error = string.IsNullOrWhiteSpace(result.Error)
                    ? "rclone vfs/forget failed"
                    : result.Error;
                Reschedule(items, error, DateTimeOffset.UtcNow);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error processing rclone invalidation queue: {e.Message}");
                await Task.Delay(ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static void Reschedule(List<RcloneInvalidationItem> items, string error, DateTimeOffset now)
    {
        var truncatedError = error.Length <= 1024 ? error : error[..1024];
        foreach (var item in items)
        {
            item.Attempts++;
            item.LastAttemptAt = now;
            item.LastError = truncatedError;
            item.NextAttemptAt = now + GetRetryDelay(item.Attempts);
        }
    }

    private static TimeSpan GetRetryDelay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 6);
        var delay = TimeSpan.FromSeconds(5 * Math.Pow(2, exponent));
        return delay <= MaxRetryDelay ? delay : MaxRetryDelay;
    }
}

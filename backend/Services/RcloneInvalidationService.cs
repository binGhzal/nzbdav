using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Rclone.Models;
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
                    var forgottenItems = GetSuccessfullyForgottenItems(items, result);
                    var forgottenIds = forgottenItems.Select(x => x.Id).ToHashSet();
                    var unverifiedItems = items
                        .Where(x => !forgottenIds.Contains(x.Id))
                        .ToList();

                    dbContext.RcloneInvalidationItems.RemoveRange(forgottenItems);
                    if (unverifiedItems.Count > 0)
                    {
                        var unverifiedError = GetUnverifiedForgetError(paths, result);
                        Reschedule(unverifiedItems, unverifiedError, DateTimeOffset.UtcNow);
                    }

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

    public static IReadOnlyList<RcloneInvalidationItem> GetSuccessfullyForgottenItems
    (
        List<RcloneInvalidationItem> items,
        VfsForgetResponse response
    )
    {
        if (!response.Success || response.Forgotten is not { Count: > 0 }) return [];

        var forgottenPaths = response.Forgotten
            .Select(NormalizeRclonePath)
            .ToHashSet(StringComparer.Ordinal);
        return items
            .Where(x => forgottenPaths.Contains(NormalizeRclonePath(x.Path)))
            .ToList();
    }

    private static string GetUnverifiedForgetError(IReadOnlyCollection<string> requestedPaths, VfsForgetResponse response)
    {
        if (response.Forgotten is not { Count: > 0 })
            return "rclone vfs/forget succeeded without confirming forgotten paths";

        var forgottenPaths = response.Forgotten
            .Select(NormalizeRclonePath)
            .ToHashSet(StringComparer.Ordinal);
        var missingCount = requestedPaths.Count(x => !forgottenPaths.Contains(NormalizeRclonePath(x)));
        return $"rclone vfs/forget did not confirm {missingCount} requested path(s)";
    }

    private static string NormalizeRclonePath(string path)
    {
        return path.Trim('/');
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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class HistoryCleanupService : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();

                var cleanupItems = await dbContext.HistoryCleanupItems
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItems.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var deleteMountedFileIds = cleanupItems
                    .Where(x => x.DeleteMountedFiles)
                    .Select(x => x.Id)
                    .ToList();
                var contentIndexChanged = deleteMountedFileIds.Count > 0;
                if (deleteMountedFileIds.Count > 0)
                {
                    // Collect items to delete for vfs/forget
                    var deletedItems = await dbContext.Items
                        .Where(x => x.HistoryItemId != null && deleteMountedFileIds.Contains(x.HistoryItemId.Value))
                        .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
                        .ToListAsync(stoppingToken);

                    // Delete the corresponding dav-items
                    await dbContext.Items
                        .Where(x => x.HistoryItemId != null && deleteMountedFileIds.Contains(x.HistoryItemId.Value))
                        .ExecuteDeleteAsync(stoppingToken);

                    // Queue rclone vfs/forget for deleted items
                    dbContext.EnqueueRcloneVfsForget(deletedItems);
                }

                var unlinkHistoryIds = cleanupItems
                    .Where(x => !x.DeleteMountedFiles)
                    .Select(x => x.Id)
                    .ToList();
                if (unlinkHistoryIds.Count > 0)
                {
                    // Mark the corresponding dav-items as no longer in History
                    await dbContext.Items
                        .Where(x => x.HistoryItemId != null && unlinkHistoryIds.Contains(x.HistoryItemId.Value))
                        .ExecuteUpdateAsync(
                            x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                            stoppingToken
                        );
                    contentIndexChanged = true;
                }

                // Persist DAV changes and rclone invalidations before writing the recovery snapshot.
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                if (contentIndexChanged)
                {
                    ContentIndexSnapshotWriterService.RequestSnapshot();
                    if (!await ContentIndexSnapshotWriterService.FlushNowAsync(stoppingToken).ConfigureAwait(false))
                        continue;
                }

                // Only drain cleanup rows after the recovery snapshot is durable.
                dbContext.HistoryCleanupItems.RemoveRange(cleanupItems);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error processing history cleanup queue: {e.Message}");

                // Wait 10 seconds before continuing on exception
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

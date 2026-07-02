using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class DavCleanupService : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();

                var cleanupItems = await dbContext.DavCleanupItems
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItems.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var cleanupIds = cleanupItems.Select(x => x.Id).ToList();

                // Collect children to delete for vfs/forget
                var deletedItems = await dbContext.Items
                    .Where(x => x.ParentId != null && cleanupIds.Contains(x.ParentId.Value))
                    .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
                    .ToListAsync(stoppingToken);

                // Delete any children
                await dbContext.Items
                    .Where(x => x.ParentId != null && cleanupIds.Contains(x.ParentId.Value))
                    .ExecuteDeleteAsync(stoppingToken);

                // Queue rclone vfs/forget for deleted children
                dbContext.EnqueueRcloneVfsForget(deletedItems);
                if (deletedItems.Count > 0)
                    ContentIndexSnapshotWriterService.RequestSnapshot();

                // Remove the queue items from database
                dbContext.DavCleanupItems.RemoveRange(cleanupItems);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error processing dav cleanup queue: {e.Message}");

                // Wait 10 seconds before continuing on exception
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

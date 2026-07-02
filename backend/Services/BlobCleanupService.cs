using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that processes the blob cleanup queue.
/// Continuously monitors BlobCleanupQueueItems table and deletes corresponding blobs.
/// </summary>
public class BlobCleanupService : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();

                var cleanupItems = await dbContext.BlobCleanupItems
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItems.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var cleanupItem in cleanupItems)
                    BlobStore.Delete(cleanupItem.Id);

                // Remove the queue items from database
                dbContext.BlobCleanupItems.RemoveRange(cleanupItems);
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
                Log.Error(e, $"Error processing blob cleanup queue: {e.Message}");

                // Wait 10 seconds before continuing on exception
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

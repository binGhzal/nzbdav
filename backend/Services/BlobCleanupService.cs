using System.Data;
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

                var blobIds = cleanupItems.Select(x => x.Id).ToList();

                await using var tx = await dbContext.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken)
                    .ConfigureAwait(false);

                var referencedIds = await GetReferencedBlobIdsAsync(dbContext, blobIds, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var cleanupItem in cleanupItems)
                {
                    if (referencedIds.Contains(cleanupItem.Id)) continue;
                    BlobStore.Delete(cleanupItem.Id);
                }

                // Remove the queue items from database
                dbContext.BlobCleanupItems.RemoveRange(cleanupItems);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                await tx.CommitAsync(stoppingToken).ConfigureAwait(false);

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
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

    private static async Task<HashSet<Guid>> GetReferencedBlobIdsAsync
    (
        DavDatabaseContext dbContext,
        IReadOnlyCollection<Guid> blobIds,
        CancellationToken ct
    )
    {
        if (blobIds.Count == 0) return [];

        var queueRefs = await dbContext.QueueItems
            .Where(x => blobIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fileBlobRefs = await dbContext.Items
            .Where(x => x.FileBlobId != null && blobIds.Contains(x.FileBlobId.Value))
            .Select(x => x.FileBlobId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var davNzbRefs = await dbContext.Items
            .Where(x => x.NzbBlobId != null && blobIds.Contains(x.NzbBlobId.Value))
            .Select(x => x.NzbBlobId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var historyNzbRefs = await dbContext.HistoryItems
            .Where(x => x.NzbBlobId != null && blobIds.Contains(x.NzbBlobId.Value))
            .Select(x => x.NzbBlobId!.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return queueRefs
            .Concat(fileBlobRefs)
            .Concat(davNzbRefs)
            .Concat(historyNzbRefs)
            .ToHashSet();
    }
}

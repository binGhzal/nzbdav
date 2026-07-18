using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that processes the NZB blob cleanup queue.
/// An NZB blob is only deleted once it is no longer referenced by any
/// QueueItem, HistoryItem, or DavItem.
/// </summary>
public class NzbBlobCleanupService(NzbBlobIngestCoordinator nzbBlobIngestCoordinator) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = DavDatabaseContextRuntimeFactory.Create();

                var cleanupItems = await dbContext.NzbBlobCleanupItems
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                // If no items in queue, wait 10 seconds before checking again
                if (cleanupItems.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var cleanupLeases = new List<(NzbBlobCleanupItem Item, IDisposable Lease)>(cleanupItems.Count);
                foreach (var cleanupItem in cleanupItems)
                {
                    var lease = nzbBlobIngestCoordinator.TryAcquire(cleanupItem.Id);
                    if (lease is not null)
                        cleanupLeases.Add((cleanupItem, lease));
                }

                if (cleanupLeases.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    cleanupItems = cleanupLeases.Select(x => x.Item).ToList();
                    var blobIds = cleanupItems.Select(x => x.Id).ToList();

                    // Use a serializable (BEGIN IMMEDIATE) transaction so that the three
                    // reference checks and the removal of the cleanup item are atomic.
                    // Without this, a concurrent HistoryItem/DavItem deletion could:
                    //   1. occur between our reference checks (making one check stale), and
                    //   2. have its trigger INSERT OR IGNORE suppressed because our cleanup
                    //      item is still in the table, permanently orphaning the blob.
                    // With BEGIN IMMEDIATE, concurrent writers are blocked until we commit.
                    // After commit, the cleanup item is gone, so any trigger that fires
                    // will successfully insert a new item for the next service pass.
                    await using var tx = await dbContext.Database
                        .BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken)
                        .ConfigureAwait(false);

                    var queueRefs = await dbContext.QueueItems
                        .Where(x => blobIds.Contains(x.Id))
                        .Select(x => x.Id)
                        .ToListAsync(stoppingToken)
                        .ConfigureAwait(false);

                    var historyRefs = await dbContext.HistoryItems
                        .Where(x => x.NzbBlobId != null && blobIds.Contains(x.NzbBlobId.Value))
                        .Select(x => x.NzbBlobId!.Value)
                        .ToListAsync(stoppingToken)
                        .ConfigureAwait(false);

                    var davRefs = await dbContext.Items
                        .Where(x => x.NzbBlobId != null && blobIds.Contains(x.NzbBlobId.Value))
                        .Select(x => x.NzbBlobId!.Value)
                        .ToListAsync(stoppingToken)
                        .ConfigureAwait(false);

                    var referencedIds = queueRefs
                        .Concat(historyRefs)
                        .Concat(davRefs)
                        .ToHashSet();
                    var unreferencedBlobIds = blobIds
                        .Where(x => !referencedIds.Contains(x))
                        .ToList();

                    foreach (var blobId in unreferencedBlobIds)
                    {
                        // Delete the blob before SaveChangesAsync so that if SaveChangesAsync
                        // fails, the cleanup item remains in the DB and the service retries.
                        // On retry, BlobStore.Delete succeeds even if the file is already gone.
                        BlobStore.Delete(blobId);
                    }

                    if (unreferencedBlobIds.Count > 0)
                    {
                        var nzbNames = await dbContext.NzbNames
                            .Where(x => unreferencedBlobIds.Contains(x.Id))
                            .ToListAsync(stoppingToken)
                            .ConfigureAwait(false);
                        dbContext.NzbNames.RemoveRange(nzbNames);
                    }

                    // Remove the cleanup queue items and commit.
                    dbContext.NzbBlobCleanupItems.RemoveRange(cleanupItems);
                    await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                    await tx.CommitAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var cleanupLease in cleanupLeases)
                        cleanupLease.Lease.Dispose();
                }

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error processing NZB blob cleanup queue: {e.Message}");

                // Wait 10 seconds before continuing on exception
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

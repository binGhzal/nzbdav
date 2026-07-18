using System.Data;
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
                await using var dbContext = DavDatabaseContextRuntimeFactory.Create();

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

                await DeleteChildrenAndEnqueueInvalidationsAsync(
                        dbContext,
                        cleanupItems,
                        stoppingToken)
                    .ConfigureAwait(false);

                // A retry can observe no children after a previous transaction committed
                // but the process stopped before the snapshot became durable.
                ContentIndexSnapshotWriterService.RequestSnapshot();
                if (!await ContentIndexSnapshotWriterService.FlushNowAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Drain cleanup rows only after both the deletion transaction and
                // recovery snapshot are durable.
                dbContext.DavCleanupItems.RemoveRange(cleanupItems);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

                // Continue immediately to next iteration to process more items
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
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

    internal static async Task DeleteChildrenAndEnqueueInvalidationsAsync(
        DavDatabaseContext dbContext,
        IReadOnlyCollection<DavCleanupItem> cleanupItems,
        CancellationToken ct)
    {
        var cleanupIds = cleanupItems.Select(x => x.Id).ToList();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var deletedItems = await dbContext.Items
            .Where(x => x.ParentId != null && cleanupIds.Contains(x.ParentId.Value))
            .Select(x => new DavItem { Id = x.Id, Type = x.Type, Path = x.Path })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await dbContext.Items
            .Where(x => x.ParentId != null && cleanupIds.Contains(x.ParentId.Value))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        dbContext.EnqueueRcloneVfsForget(deletedItems);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}

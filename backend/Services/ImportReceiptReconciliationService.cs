using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ImportReceiptReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaimGracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReviewThreshold = TimeSpan.FromMinutes(30);
    private const int BatchSize = 100;
    private readonly Func<IEnumerable<OrganizedLinksUtil.DavItemLink>> _enumerateLinks;

    public ImportReceiptReconciliationService(ConfigManager configManager)
        : this(() => OrganizedLinksUtil.GetLibraryDavItemLinks(configManager))
    {
    }

    public ImportReceiptReconciliationService(
        Func<IEnumerable<OrganizedLinksUtil.DavItemLink>> enumerateLinks)
    {
        _enumerateLinks = enumerateLinks;
    }

    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var candidates = await dbContext.ImportReceipts
            .Where(x => x.State == ImportReceiptState.UnlinkClaimed)
            .Where(x => x.UpdatedAt <= now - ClaimGracePeriod)
            .OrderBy(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Take(BatchSize)
            .Select(x => new { x.DavItemId, x.HistoryItemId, x.UpdatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0) return;

        var linkedItemIds = _enumerateLinks()
            .Select(x => x.DavItemId)
            .ToHashSet();
        var receiptService = new ImportReceiptService(dbContext);
        foreach (var candidate in candidates)
        {
            if (linkedItemIds.Contains(candidate.DavItemId))
            {
                await receiptService
                    .MarkImportedAsync(candidate.DavItemId, candidate.HistoryItemId, now, ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (candidate.UpdatedAt <= now - ReviewThreshold)
            {
                await receiptService
                    .MarkNeedsReviewAsync(
                        candidate.DavItemId,
                        candidate.HistoryItemId,
                        now,
                        "No organized-library link was found during reconciliation.",
                        ct)
                    .ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Completed-symlink import receipt reconciliation failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

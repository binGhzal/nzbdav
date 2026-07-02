using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService
{
    private static readonly TimeSpan AutoRepairRetryDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissingSegmentCacheTtl = TimeSpan.FromHours(6);
    private const int MaxMissingSegmentCacheEntries = 100_000;

    private readonly ConfigManager _configManager;
    private readonly INntpClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly object _activeHealthChecksLock = new();
    private readonly HashSet<Guid> _activeHealthChecks = [];

    private static readonly Dictionary<string, DateTimeOffset> _missingSegmentIds = [];

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;
            lock (_missingSegmentIds) _missingSegmentIds.Clear();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var startedWorker = false;
                var segmentConcurrency = _configManager.GetAdaptiveHealthCheckConcurrency();
                var itemConcurrency = GetHealthCheckItemConcurrency(segmentConcurrency);
                var workerSegmentConcurrency = Math.Max(1, segmentConcurrency / itemConcurrency);

                while (GetActiveHealthCheckCount() < itemConcurrency)
                {
                    var davItemId = await GetNextHealthCheckItemId(stoppingToken).ConfigureAwait(false);
                    if (davItemId == null) break;
                    if (!TryMarkHealthCheckActive(davItemId.Value)) continue;

                    startedWorker = true;
                    _ = RunHealthCheckWorkerAsync(davItemId.Value, workerSegmentConcurrency, stoppingToken);
                }

                var delay = startedWorker
                    ? TimeSpan.FromMilliseconds(250)
                    : GetActiveHealthCheckCount() > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static int GetHealthCheckItemConcurrency(int segmentConcurrency)
    {
        return Math.Clamp(segmentConcurrency / 2, 1, 4);
    }

    private int GetActiveHealthCheckCount()
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.Count;
        }
    }

    private Guid[] GetActiveHealthCheckIds()
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.ToArray();
        }
    }

    private bool TryMarkHealthCheckActive(Guid davItemId)
    {
        lock (_activeHealthChecksLock)
        {
            return _activeHealthChecks.Add(davItemId);
        }
    }

    private void ClearHealthCheckActive(Guid davItemId)
    {
        lock (_activeHealthChecksLock)
        {
            _activeHealthChecks.Remove(davItemId);
        }
    }

    private async Task<Guid?> GetNextHealthCheckItemId(CancellationToken ct)
    {
        await using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var currentDateTime = DateTimeOffset.UtcNow;
        var activeHealthCheckIds = GetActiveHealthCheckIds();
        IQueryable<DavItem> query = GetHealthCheckQueueItems(dbClient)
            .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime);

        if (activeHealthCheckIds.Length > 0)
            query = query.Where(x => !activeHealthCheckIds.Contains(x.Id));

        return await query
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task RunHealthCheckWorkerAsync(Guid davItemId, int segmentConcurrency, CancellationToken ct)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var dbClient = new DavDatabaseClient(dbContext);
            var davItem = await dbContext.Items
                .FirstOrDefaultAsync(x => x.Id == davItemId, ct)
                .ConfigureAwait(false);
            if (davItem == null) return;

            await PerformHealthCheck(davItem, dbClient, segmentConcurrency, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || SigtermUtil.IsSigtermTriggered())
        {
        }
        catch (Exception e)
        {
            Log.Error(e, "Unexpected error performing background health check for {DavItemId}: {Message}", davItemId, e.Message);
        }
        finally
        {
            ClearHealthCheckActive(davItemId);
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck == null ? 1 : 0)
            .ThenBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.HistoryItemId == null);
    }

    private async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        try
        {
            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);


            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce();
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            var progress = progressHook.ToPercentage(segments.Count);
            await _usenetClient.CheckAllSegmentsAsync(segments, concurrency, progress, ct).ConfigureAwait(false);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // update the database
            ClearCachedMissingSegmentIds(segments);
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;
            davItem.NextHealthCheck = davItem.ReleaseDate + 2 * (davItem.LastHealthCheck - davItem.ReleaseDate);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = "File is healthy."
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
                AddCachedMissingSegmentIds(NzbSegmentIdSet.Decode(e.SegmentId));

            // when usenet article is missing, perform repairs
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = segments.FirstOrDefault().ToNullIfEmpty();
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await _usenetClient.HeadWithFallbackAsync(firstSegmentId, ct).ConfigureAwait(false);
        var articleHeaders = articleHeadersResponse.ArticleHeaders!;
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            var arrErrors = new List<string>();
            var matchingArrHosts = new List<string>();
            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                var rootFolders = await GetArrRootFolders(arrClient, arrErrors).ConfigureAwait(false);
                if (!rootFolders.Any(x => IsPathInsideRoot(symlinkOrStrmPath, x.Path))) continue;
                matchingArrHosts.Add(arrClient.Host);

                // if we found a corresponding arr instance,
                // then remove and search.
                try
                {
                    if (await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false))
                    {
                        dbClient.Ctx.Items.Remove(davItem);
                        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                        {
                            Id = Guid.NewGuid(),
                            DavItemId = davItem.Id,
                            Path = davItem.Path,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Result = HealthCheckResult.HealthResult.Unhealthy,
                            RepairStatus = HealthCheckResult.RepairAction.Repaired,
                            Message = string.Join(" ", [
                                "File had missing articles.",
                                $"Corresponding {linkType} found within Library Dir.",
                                $"Triggered new Arr search through `{arrClient.Host}`."
                            ])
                        }));
                        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception e)
                {
                    arrErrors.Add($"`{arrClient.Host}`: {e.Message}");
                }
            }

            var arrErrorText = arrErrors.Count == 0
                ? ""
                : $" Arr errors: {string.Join(" ", arrErrors)}";
            var repairFailureMessage = matchingArrHosts.Count > 0
                ? $"Found matching Arr root folder in {string.Join(", ", matchingArrHosts.Select(x => $"`{x}`"))}, but no instance matched the link to a tracked media item."
                : "Could not find a configured Arr root folder for this link.";
            await MarkAutoRepairNeedsAction(
                davItem,
                dbClient,
                string.Join(" ", [
                    "File had missing articles.",
                    $"Corresponding {linkType} found within Library Dir.",
                    repairFailureMessage,
                    "Left the webdav-file and link in place and will retry automatic repair.",
                    arrErrorText
                ]),
                ct
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<List<ArrRootFolder>> GetArrRootFolders
    (
        ArrClient arrClient,
        List<string> errors
    )
    {
        try
        {
            return await arrClient.GetRootFolders().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            errors.Add($"`{arrClient.Host}`: {e.Message}");
            return [];
        }
    }

    private async Task MarkAutoRepairNeedsAction
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        string message,
        CancellationToken ct
    )
    {
        var utcNow = DateTimeOffset.UtcNow;
        davItem.LastHealthCheck = utcNow;
        davItem.NextHealthCheck = utcNow + AutoRepairRetryDelay;
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = utcNow,
            Result = HealthCheckResult.HealthResult.Unhealthy,
            RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
            Message = message
        }));
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public static bool IsPathInsideRoot(string path, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath)) return false;

        var normalizedPath = NormalizePathForRootMatch(path);
        var normalizedRoot = NormalizePathForRootMatch(rootPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (normalizedRoot == "/") return normalizedPath.StartsWith("/", comparison);

        return normalizedPath.Equals(normalizedRoot, comparison)
               || normalizedPath.StartsWith($"{normalizedRoot}/", comparison);
    }

    private static string NormalizePathForRootMatch(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            PruneExpiredMissingSegmentIds(DateTimeOffset.UtcNow);
            foreach (var segmentId in segmentIds)
                if (NzbSegmentIdSet.Decode(segmentId).All(candidateSegmentId => _missingSegmentIds.ContainsKey(candidateSegmentId)))
                    throw new UsenetArticleNotFoundException(segmentId);
        }
    }

    private static void ClearCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds.SelectMany(NzbSegmentIdSet.Decode))
                _missingSegmentIds.Remove(segmentId);
        }
    }

    private static void AddCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            var utcNow = DateTimeOffset.UtcNow;
            PruneExpiredMissingSegmentIds(utcNow);
            foreach (var segmentId in segmentIds)
                _missingSegmentIds[segmentId] = utcNow;
            PruneOldestMissingSegmentIds();
        }
    }

    private static void PruneExpiredMissingSegmentIds(DateTimeOffset utcNow)
    {
        var expiredSegmentIds = _missingSegmentIds
            .Where(x => utcNow - x.Value > MissingSegmentCacheTtl)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in expiredSegmentIds)
            _missingSegmentIds.Remove(segmentId);
    }

    private static void PruneOldestMissingSegmentIds()
    {
        var excessCount = _missingSegmentIds.Count - MaxMissingSegmentCacheEntries;
        if (excessCount <= 0) return;

        var oldestSegmentIds = _missingSegmentIds
            .OrderBy(x => x.Value)
            .Take(excessCount)
            .Select(x => x.Key)
            .ToList();
        foreach (var segmentId in oldestSegmentIds)
            _missingSegmentIds.Remove(segmentId);
    }
}

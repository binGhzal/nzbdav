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
    private readonly Func<CancellationToken, Task>? _iterationOverride;

    internal enum WholeCacheFenceProcessOutcome
    {
        NotRequired,
        NoWork,
        Completed,
        Failed
    }

    public RcloneInvalidationService()
    {
    }

    internal RcloneInvalidationService(Func<CancellationToken, Task> iterationOverride)
    {
        _iterationOverride = iterationOverride
            ?? throw new ArgumentNullException(nameof(iterationOverride));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_iterationOverride is not null)
                {
                    await _iterationOverride(stoppingToken).ConfigureAwait(false);
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(IdleDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!RcloneClient.RequiresVfsVisibilityFence)
                {
                    await MakeWaitingArrCommandsDueWhenFenceNotRequiredAsync(stoppingToken)
                        .ConfigureAwait(false);
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(DisabledDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var wholeCacheOutcome = await ProcessWholeCacheVisibilityFenceAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (wholeCacheOutcome is WholeCacheFenceProcessOutcome.Completed)
                    continue;
                if (wholeCacheOutcome is WholeCacheFenceProcessOutcome.Failed)
                {
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(ErrorDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!RcloneClient.IsRemoteControlEnabled || RcloneClient.Host == null)
                {
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(DisabledDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
                var now = DateTimeOffset.UtcNow;
                var items = await dbContext.RcloneInvalidationItems
                    .AsNoTracking()
                    .Where(x => x.Path != RcloneInvalidationItem.WholeCacheVisibilityFencePath
                                && x.NextAttemptAt <= now)
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (items.Count == 0)
                {
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(IdleDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await ProcessBatchAsync(dbContext, items, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (BackgroundServiceCancellationUtil.IsExpectedCancellation(e, stoppingToken))
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(
                    "Rclone invalidation worker iteration failed: category={FailureCategory}, exception_type={ExceptionType}.",
                    "rclone_invalidation_worker_failure",
                    RcloneClient.GetSafeExceptionType(e));
                try
                {
                    await RcloneInvalidationWakeSignal
                        .WaitAsync(ErrorDelay, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException cancellationException)
                    when (BackgroundServiceCancellationUtil.IsExpectedCancellation(
                        cancellationException,
                        stoppingToken))
                {
                    return;
                }
            }
        }
    }

    internal static async Task ProcessBatchAsync(
        DavDatabaseContext dbContext,
        List<RcloneInvalidationItem> items,
        CancellationToken ct)
    {
        await using var topologyLease = await RcloneClient
            .AcquireVisibilityFenceTopologyLeaseAsync(ct)
            .ConfigureAwait(false);
        if (!topologyLease.Required) return;

        var paths = items
            .Where(x => x.Path != RcloneInvalidationItem.WholeCacheVisibilityFencePath)
            .Select(x => x.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0) return;
        var result = await RcloneClient.ForgetVfsPaths(paths, ct).ConfigureAwait(false);
        if (result.Success)
        {
            var forgottenItems = GetSuccessfullyForgottenItems(items, result);
            var forgottenIds = forgottenItems.Select(x => x.Id).ToHashSet();
            var unverifiedItems = items
                .Where(x => !forgottenIds.Contains(x.Id))
                .ToList();

            await DeleteConfirmedItemsAsync(dbContext, forgottenItems, ct).ConfigureAwait(false);
            if (unverifiedItems.Count > 0)
            {
                var unverifiedError = GetUnverifiedForgetError(paths, result);
                await RescheduleItemsAsync(
                        dbContext,
                        unverifiedItems,
                        unverifiedError,
                        DateTimeOffset.UtcNow,
                        ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        var error = ClassifyDurableFailure(result);
        await RescheduleItemsAsync(
                dbContext,
                items,
                error,
                DateTimeOffset.UtcNow,
                ct)
            .ConfigureAwait(false);
    }

    internal static async Task<WholeCacheFenceProcessOutcome> ProcessWholeCacheVisibilityFenceAsync(
        CancellationToken ct = default)
    {
        await using var topologyLease = await RcloneClient
            .AcquireVisibilityFenceTopologyLeaseAsync(ct)
            .ConfigureAwait(false);
        if (!topologyLease.Required)
            return WholeCacheFenceProcessOutcome.NotRequired;

        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        dbContext.SuppressRcloneInvalidations = true;
        if (topologyLease.WholeCacheVisibilityFencePending
            && !topologyLease.WholeCacheVisibilityFenceMaterialized)
        {
            dbContext.SuppressRcloneInvalidations = false;
            dbContext.EnqueueWholeCacheVisibilityFence();
            dbContext.SuppressRcloneInvalidations = true;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            if (!RcloneClient.TryMarkWholeCacheVisibilityFenceMaterialized(topologyLease.Generation))
                return WholeCacheFenceProcessOutcome.Failed;
        }

        var sentinel = await dbContext.RcloneInvalidationItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == RcloneInvalidationItem.WholeCacheVisibilityFenceId
                     && x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath,
                ct)
            .ConfigureAwait(false);
        if (sentinel is null)
            return WholeCacheFenceProcessOutcome.NoWork;

        var response = await RcloneClient
            .ForgetWholeVfsCache(topologyLease.Generation, ct)
            .ConfigureAwait(false);
        if (!response.Success)
            return WholeCacheFenceProcessOutcome.Failed;

        var deleted = await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(
                async () => await dbContext.RcloneInvalidationItems
                    .Where(x => x.Id == sentinel.Id && x.Revision == sentinel.Revision)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);
        if (deleted == 0)
            return WholeCacheFenceProcessOutcome.Completed;

        RcloneClient.TryClearWholeCacheVisibilityFence(topologyLease.Generation);
        await MakeAllWaitingArrCommandsDueAsync(ct).ConfigureAwait(false);
        ArrImportCommandWakeSignal.Pulse();
        return WholeCacheFenceProcessOutcome.Completed;
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

    public static async Task<int> DeleteConfirmedItemsAsync(
        DavDatabaseContext dbContext,
        IReadOnlyCollection<RcloneInvalidationItem> confirmedItems,
        CancellationToken ct = default)
    {
        var deleted = await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var count = 0;
            foreach (var revisionGroup in confirmedItems.GroupBy(x => x.Revision))
            {
                var ids = revisionGroup.Select(x => x.Id).ToList();
                count += await dbContext.RcloneInvalidationItems
                    .Where(x => ids.Contains(x.Id) && x.Revision == revisionGroup.Key)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
            }

            if (count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                await dbContext.ArrImportCommands
                    .Where(x => x.Status == ArrImportCommandStatus.WaitingForInvalidation)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.NextAttemptAt, now)
                        .SetProperty(x => x.UpdatedAt, now), ct)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return count;
        }, ct).ConfigureAwait(false);

        if (deleted > 0)
            ArrImportCommandWakeSignal.Pulse();

        return deleted;
    }

    /// <summary>
    /// Releases ARR commands from the rclone prerequisite when another mount owns visibility.
    /// Invalidation rows deliberately remain durable so switching back to rclone cannot bypass
    /// work that was never confirmed by rclone.
    /// </summary>
    public static async Task<int> MakeWaitingArrCommandsDueWhenFenceNotRequiredAsync(
        CancellationToken ct = default)
    {
        if (RcloneClient.RequiresVfsVisibilityFence) return 0;

        return await MakeAllWaitingArrCommandsDueAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> MakeAllWaitingArrCommandsDueAsync(CancellationToken ct)
    {
        var changed = await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            return await dbContext.ArrImportCommands
                .Where(x => x.Status == ArrImportCommandStatus.WaitingForInvalidation
                            && x.NextAttemptAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (changed > 0)
            ArrImportCommandWakeSignal.Pulse();

        return changed;
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

    internal static string ClassifyDurableFailure(RcloneResponse response)
    {
        return RcloneClient.GetSafeFailureCategory(response);
    }

    internal static string? GetStatusSafeError(string? durableError)
    {
        if (string.IsNullOrWhiteSpace(durableError)) return null;
        if (durableError is
            "rclone_rc_authentication_failed" or
            "rclone_rc_http_error" or
            "rclone_rc_malformed_response" or
            "rclone_rc_timeout" or
            "rclone_rc_connection_failure" or
            "rclone_rc_disabled" or
            "rclone_rc_host_not_configured" or
            "rclone_rc_configuration_changed" or
            "rclone_rc_invalid_response" or
            "rclone_rc_invalid_endpoint" or
            "rclone_rc_request_failed")
        {
            return durableError;
        }

        if (TryParseStatusCodeCategory(durableError, "rclone_rc_http_", out _)
            || TryParseStatusCodeCategory(
                durableError,
                "rclone_rc_authentication_failed_http_",
                out _))
        {
            return durableError;
        }

        const string missingPrefix = "rclone vfs/forget did not confirm ";
        const string missingSuffix = " requested path(s)";
        if (durableError.StartsWith(missingPrefix, StringComparison.Ordinal)
            && durableError.EndsWith(missingSuffix, StringComparison.Ordinal)
            && int.TryParse(
                durableError[missingPrefix.Length..^missingSuffix.Length],
                out var missingCount)
            && missingCount >= 0)
        {
            return $"{missingPrefix}{missingCount}{missingSuffix}";
        }

        if (durableError == "rclone vfs/forget succeeded without confirming forgotten paths")
            return durableError;

        return "rclone_invalidation_legacy_failure";
    }

    private static bool TryParseStatusCodeCategory(
        string value,
        string prefix,
        out int statusCode)
    {
        statusCode = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(value[prefix.Length..], out statusCode)
               && statusCode is >= 100 and <= 599;
    }

    private static string NormalizeRclonePath(string path)
    {
        return path.Trim('/');
    }

    private static async Task RescheduleItemsAsync(
        DavDatabaseContext dbContext,
        IReadOnlyCollection<RcloneInvalidationItem> items,
        string error,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var safeError = GetStatusSafeError(error);
        await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                var attempts = item.Attempts == int.MaxValue ? int.MaxValue : item.Attempts + 1;
                var nextAttemptAt = now + GetRetryDelay(attempts);
                await dbContext.RcloneInvalidationItems
                    .Where(x => x.Id == item.Id && x.Revision == item.Revision)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Attempts, attempts)
                        .SetProperty(x => x.LastAttemptAt, now)
                        .SetProperty(x => x.LastError, safeError)
                        .SetProperty(x => x.NextAttemptAt, nextAttemptAt), ct)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    private static TimeSpan GetRetryDelay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 6);
        var delay = TimeSpan.FromSeconds(5 * Math.Pow(2, exponent));
        return delay <= MaxRetryDelay ? delay : MaxRetryDelay;
    }
}

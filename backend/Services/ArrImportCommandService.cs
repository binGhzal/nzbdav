using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed record ArrImportDispatchResult(
    string App,
    string InstanceKey,
    int CommandId,
    DateTimeOffset AcceptedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DispatchMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FallbackReasonCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CommandName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PublicationToAcceptMilliseconds = null);

public enum ArrVisibilityPublicationOutcome
{
    Blocked,
    Published,
    Ready,
    Stale
}

public sealed class ArrImportCommandService : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InvalidationFallbackDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultArrRequestTimeout = TimeSpan.FromSeconds(2);
    private const int VisibilityTransactionMaxAttempts = 5;
    private const string WorkerErrorCode = "worker-error";
    private const string CancellationRequeueErrorCode = "cancel-requeue-error";
    private const string VisibilityPublicationErrorCode = "visibility-error";
    private readonly Func<IEnumerable<ArrClient>> _getArrClients;
    private readonly TimeSpan _arrRequestTimeout;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<bool>> _wakeRecheckInvalidation;
    private readonly HistoryVisibilityNotifier? _historyVisibilityNotifier;
    private readonly Func<CancellationToken, Task> _requeueNoRoute;
    private readonly Func<
        ArrImportCommand,
        IReadOnlyCollection<string>,
        bool,
        CancellationToken,
        Task<ArrVisibilityPublicationOutcome>> _evaluateVisibilityAndPublish;
    private readonly SemaphoreSlim _configurationTransitionLock = new(1, 1);
    private bool _hasObservedArrConfiguration;
    private bool _lastObservedArrConfigurationAvailable;

    public ArrImportCommandService(
        ConfigManager configManager,
        HistoryVisibilityNotifier historyVisibilityNotifier)
        : this(
            () => configManager.GetArrConfig().GetArrClients(),
            historyVisibilityNotifier: historyVisibilityNotifier)
    {
    }

    public ArrImportCommandService(
        Func<IEnumerable<ArrClient>> getArrClients,
        TimeSpan? arrRequestTimeout = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<bool>>? wakeRecheckInvalidation = null,
        HistoryVisibilityNotifier? historyVisibilityNotifier = null,
        Func<CancellationToken, Task>? requeueNoRoute = null,
        Func<
            ArrImportCommand,
            IReadOnlyCollection<string>,
            bool,
            CancellationToken,
            Task<ArrVisibilityPublicationOutcome>>? evaluateVisibilityAndPublish = null)
    {
        _getArrClients = getArrClients;
        _arrRequestTimeout = arrRequestTimeout ?? DefaultArrRequestTimeout;
        if (_arrRequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(arrRequestTimeout), "ARR request timeout must be positive.");
        _wakeRecheckInvalidation = wakeRecheckInvalidation ?? HasPendingInvalidationForWakeRecheckAsync;
        _historyVisibilityNotifier = historyVisibilityNotifier;
        _requeueNoRoute = requeueNoRoute ?? RequeueNoRouteCommandsAsync;
        _evaluateVisibilityAndPublish = evaluateVisibilityAndPublish
                                        ?? ((command, paths, fenceRequired, ct) =>
                                            EvaluateVisibilityAndPublishAsync(
                                                command,
                                                paths,
                                                fenceRequired,
                                                ct));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken).ConfigureAwait(false))
                    continue;

                await ArrImportCommandWakeSignal.WaitAsync(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                BackgroundServiceCancellationUtil.IsExpectedCancellation(exception, stoppingToken))
            {
                return;
            }
            catch (Exception)
            {
                Log.Error(
                    "ARR import command worker failed ({ErrorCode}).",
                    WorkerErrorCode);
                try
                {
                    await ArrImportCommandWakeSignal
                        .WaitAsync(IdleDelay, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    BackgroundServiceCancellationUtil.IsExpectedCancellation(exception, stoppingToken))
                {
                    return;
                }
            }
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var arrClients = _getArrClients().ToArray();
        await RequeueNoRouteOnConfigurationTransitionAsync(arrClients, ct).ConfigureAwait(false);
        var command = await TryClaimAsync(ct).ConfigureAwait(false);
        if (command is null) return false;

        try
        {
            var requiredPaths = DeserializeRequiredPaths(command.RequiredInvalidationPathsJson);
            await using var topologyLease = await RcloneClient
                .AcquireVisibilityFenceTopologyLeaseAsync(ct)
                .ConfigureAwait(false);
            var visibilityOutcome = topologyLease.Required
                                    && topologyLease.WholeCacheVisibilityFencePending
                ? ArrVisibilityPublicationOutcome.Blocked
                : await _evaluateVisibilityAndPublish(
                        command,
                        requiredPaths,
                        topologyLease.Required,
                        ct)
                    .ConfigureAwait(false);

            if (visibilityOutcome == ArrVisibilityPublicationOutcome.Blocked)
            {
                await ReleaseLeaseAsync(
                        command,
                        ArrImportCommandStatus.WaitingForInvalidation,
                        DateTimeOffset.UtcNow + InvalidationFallbackDelay,
                        error: null,
                        resultsJson: command.ResultsJson,
                        completedAt: null,
                        incrementAttempts: false,
                        ct)
                    .ConfigureAwait(false);

                // Recheck after publishing the waiting state. A completion wake can race with
                // the release above; making the row due here closes that lost-wake window.
                // Reuse the held topology lease: acquiring it again here deadlocks and would
                // allow the target identity to change between the decision and waiting update.
                if (!await HasBlockingWakePrerequisiteAsync(
                            requiredPaths,
                            topologyLease.Required,
                            topologyLease.WholeCacheVisibilityFencePending,
                            ct)
                        .ConfigureAwait(false))
                {
                    await MakeWaitingCommandDueNowAsync(command.Id, ct).ConfigureAwait(false);
                    ArrImportCommandWakeSignal.Pulse();
                }
                return true;
            }

            if (visibilityOutcome is ArrVisibilityPublicationOutcome.Published
                or ArrVisibilityPublicationOutcome.Stale)
            {
                if (visibilityOutcome == ArrVisibilityPublicationOutcome.Published)
                {
                    await TryPublishHistoryVisibilityAsync(command.HistoryItemId).ConfigureAwait(false);
                    ArrImportCommandWakeSignal.Pulse();
                }
                return true;
            }

            await TryPublishHistoryVisibilityAsync(command.HistoryItemId).ConfigureAwait(false);
            await DispatchAsync(command, arrClients, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                await ReleaseLeaseAsync(
                        command,
                        ArrImportCommandStatus.Pending,
                        DateTimeOffset.UtcNow,
                        error: null,
                        resultsJson: command.ResultsJson,
                        completedAt: null,
                        incrementAttempts: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ArrImportCommandWakeSignal.Pulse();
            }
            catch (Exception)
            {
                Log.Warning(
                    "Could not requeue cancelled ARR import command {CommandId} ({ErrorCode}).",
                    command.Id,
                    CancellationRequeueErrorCode);
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var attempt = SaturatingIncrement(command.Attempts);
            await ReleaseLeaseAsync(
                    command,
                    ArrImportCommandStatus.Retry,
                    DateTimeOffset.UtcNow + GetRetryDelay(attempt),
                    WorkerErrorCode,
                    command.ResultsJson,
                    completedAt: null,
                    incrementAttempts: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
    }

    private async Task RequeueNoRouteOnConfigurationTransitionAsync(
        IReadOnlyCollection<ArrClient> arrClients,
        CancellationToken ct)
    {
        await _configurationTransitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var configurationAvailable = arrClients.Count > 0;
            if (!configurationAvailable)
            {
                _hasObservedArrConfiguration = true;
                _lastObservedArrConfigurationAvailable = false;
                return;
            }

            if (_hasObservedArrConfiguration && _lastObservedArrConfigurationAvailable) return;

            await _requeueNoRoute(ct).ConfigureAwait(false);
            _hasObservedArrConfiguration = true;
            _lastObservedArrConfigurationAvailable = true;
        }
        finally
        {
            _configurationTransitionLock.Release();
        }
    }

    private static async Task RequeueNoRouteCommandsAsync(CancellationToken ct)
    {
        await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            return await dbContext.ArrImportCommands
                .Where(x => x.Status == ArrImportCommandStatus.NoRoute)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ArrImportCommandStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, (string?)null), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task DispatchAsync(
        ArrImportCommand command,
        IReadOnlyCollection<ArrClient> arrClients,
        CancellationToken ct)
    {
        var targetResolution = await GetTargetsAsync(command, arrClients, ct).ConfigureAwait(false);
        if (targetResolution.Error is not null)
        {
            var routeAttempt = SaturatingIncrement(command.Attempts);
            var terminalNoRoute = targetResolution.IsNoRoute;
            await ReleaseLeaseAsync(
                    command,
                    terminalNoRoute ? ArrImportCommandStatus.NoRoute : ArrImportCommandStatus.Retry,
                    terminalNoRoute ? command.NextAttemptAt : DateTimeOffset.UtcNow + GetRetryDelay(routeAttempt),
                    Truncate(targetResolution.Error, 2048),
                    resultsJson: command.ResultsJson,
                    completedAt: terminalNoRoute ? DateTimeOffset.UtcNow : null,
                    incrementAttempts: true,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        var targets = targetResolution.Targets;
        var accepted = DeserializeResults(command.ResultsJson)
            .ToDictionary(x => x.InstanceKey, StringComparer.Ordinal);
        var pendingTargets = targets
            .Where(target => !accepted.ContainsKey(target.InstanceKey))
            .ToList();
        var dispatches = await Task.WhenAll(
                pendingTargets.Select(target => DispatchOneAsync(command, target, ct)))
            .ConfigureAwait(false);
        var failures = new List<string>();
        foreach (var dispatch in dispatches)
        {
            if (dispatch.Result is not null)
                accepted[dispatch.Result.InstanceKey] = dispatch.Result;
            else if (dispatch.Error is not null)
                failures.Add(dispatch.Error);
        }

        var resultsJson = JsonSerializer.Serialize(
            accepted.Values.OrderBy(x => x.InstanceKey, StringComparer.Ordinal));
        // Preserve accepted siblings if another target observes external cancellation.
        // The outer cancellation path releases the same lease using this snapshot.
        command.ResultsJson = resultsJson;
        if (dispatches.Any(dispatch => dispatch.Cancelled))
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException("ARR import dispatch was cancelled.", ct);
        }

        var attempt = SaturatingIncrement(command.Attempts);
        var allAccepted = targets.All(target => accepted.ContainsKey(target.InstanceKey));
        await ReleaseLeaseAsync(
                command,
                allAccepted ? ArrImportCommandStatus.Dispatched : ArrImportCommandStatus.Retry,
                allAccepted ? command.NextAttemptAt : DateTimeOffset.UtcNow + GetRetryDelay(attempt),
                allAccepted ? null : Truncate(string.Join("; ", failures), 2048),
                resultsJson,
                allAccepted ? DateTimeOffset.UtcNow : null,
                incrementAttempts: true,
                ct)
            .ConfigureAwait(false);
    }

    private async Task TryPublishHistoryVisibilityAsync(Guid historyItemId)
    {
        if (_historyVisibilityNotifier is null) return;
        try
        {
            await _historyVisibilityNotifier
                .PublishIfVisibleAsync(historyItemId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            Log.Warning(
                "Could not publish history visibility for ARR import {HistoryItemId} ({ErrorCode}).",
                historyItemId,
                VisibilityPublicationErrorCode);
        }
    }

    private async Task<TargetResolution> GetTargetsAsync(
        ArrImportCommand command,
        IReadOnlyCollection<ArrClient> arrClients,
        CancellationToken ct)
    {
        var allTargets = arrClients
            .Select(client => new DispatchTarget(
                GetAppName(client),
                ArrIntegration.GetInstanceKey(GetAppName(client), client.Host),
                client,
                GetClientKind(client),
                ArrCompletedDownloadRouteKind.None,
                [],
                Timing: null))
            .GroupBy(x => x.InstanceKey, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var correlations = await dbContext.ArrDownloadCorrelations
            .AsNoTracking()
            .Where(x => x.HistoryItemId == command.HistoryItemId || x.QueueItemId == command.HistoryItemId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var correlatedKeys = correlations
            .Select(correlation => correlation.InstanceKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (correlatedKeys.Count > 0)
        {
            var targetsByKey = allTargets.ToDictionary(x => x.InstanceKey, StringComparer.Ordinal);
            var missingKeys = correlatedKeys
                .Where(key => !targetsByKey.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            if (missingKeys.Length > 0)
                return TargetResolution.Failed("route-missing-correlated-instance");

            return TargetResolution.Resolved(correlatedKeys
                .Select(key => targetsByKey[key] with
                {
                    RouteKind = ArrCompletedDownloadRouteKind.Correlation,
                    Correlations = correlations
                        .Where(correlation => string.Equals(
                            correlation.InstanceKey,
                            key,
                            StringComparison.Ordinal))
                        .Select(correlation => new ArrCompletedDownloadCorrelationFact(
                            correlation.DownloadId,
                            correlation.MovieId,
                            correlation.SeriesId,
                            correlation.EpisodeId))
                        .ToArray()
                })
                .ToList());
        }

        if (allTargets.Count == 0)
            return TargetResolution.NoRoute("route-no-configured-instances");

        var ownershipProbes = await Task.WhenAll(
                allTargets.Select(target => ProbeCategoryOwnershipAsync(command, target, ct)))
            .ConfigureAwait(false);
        var probeErrors = ownershipProbes
            .Where(x => x.Error is not null)
            .Select(x => x.Error!)
            .ToArray();
        if (probeErrors.Length > 0)
            return TargetResolution.Failed(string.Join("; ", probeErrors));

        var owners = ownershipProbes
            .Where(x => x.Categories.Contains(command.Category, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Target with { RouteKind = ArrCompletedDownloadRouteKind.CategoryOwnership })
            .ToList();
        return owners.Count switch
        {
            1 => TargetResolution.Resolved(owners),
            0 => TargetResolution.Failed("route-category-owner-missing"),
            _ => TargetResolution.Failed("route-category-owner-ambiguous")
        };
    }

    private async Task<OwnershipProbe> ProbeCategoryOwnershipAsync(
        ArrImportCommand command,
        DispatchTarget target,
        CancellationToken ct)
    {
        var timing = DispatchTiming.Start();
        target = target with { Timing = timing };
        try
        {
            var authorization = await AuthorizeNetworkAttemptAsync(
                    command,
                    timing,
                    _arrRequestTimeout,
                    ct)
                .ConfigureAwait(false);
            if (!authorization.Authorized)
                return new OwnershipProbe(target, [], Failure(target, authorization.ErrorCode));

            using var requestCts = CreateRequestCancellation(
                ct,
                timing,
                _arrRequestTimeout);
            if (requestCts is null)
                return new OwnershipProbe(target, [], Failure(target, "ownership-timeout"));
            var downloadClients = await target.Client.GetDownloadClientsAsync(requestCts.Token).ConfigureAwait(false);
            var categories = downloadClients
                .Select(x => x.Category)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new OwnershipProbe(target, categories, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OwnershipProbe(target, [], Failure(target, "ownership-timeout"));
        }
        catch (Exception exception)
        {
            return new OwnershipProbe(target, [], Failure(target, Classify(exception, "ownership")));
        }
    }

    private async Task<DispatchAttempt> DispatchOneAsync(
        ArrImportCommand command,
        DispatchTarget target,
        CancellationToken ct)
    {
        try
        {
            var timing = target.Timing ?? DispatchTiming.Start();
            var policyTarget = new ArrCompletedDownloadTarget(
                target.App,
                target.ClientKind,
                target.RouteKind);
            var preparation = ArrCompletedDownloadDispatchPolicy.Prepare(
                policyTarget,
                target.Correlations);
            if (!preparation.ShouldProbeQueue)
            {
                return await RefreshAsync(
                        command,
                        target,
                        timing,
                        _arrRequestTimeout,
                        "refresh-only",
                        preparation.FallbackReasonCode ?? "policy-refused",
                        ct)
                    .ConfigureAwait(false);
            }

            var directDeadline = TimeSpan.FromTicks(_arrRequestTimeout.Ticks / 2);
            var authorization = await AuthorizeNetworkAttemptAsync(command, timing, directDeadline, ct)
                .ConfigureAwait(false);
            if (!authorization.Authorized)
                return DispatchAttempt.Failed(Failure(target, authorization.ErrorCode));

            ArrCompletedDownloadDispatchDecision decision;
            try
            {
                using var queueCts = CreateRequestCancellation(ct, timing, directDeadline);
                if (queueCts is null)
                {
                    return await RefreshAsync(
                            command,
                            target,
                            timing,
                            _arrRequestTimeout,
                            "refresh-fallback",
                            "queue-timeout",
                            ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    decision = target.Client switch
                    {
                        SonarrClient sonarr => ArrCompletedDownloadDispatchPolicy.Evaluate(
                            preparation,
                            await sonarr.GetSonarrQueueAsync(queueCts.Token).ConfigureAwait(false)),
                        RadarrClient radarr => ArrCompletedDownloadDispatchPolicy.Evaluate(
                            preparation,
                            await radarr.GetRadarrQueueAsync(queueCts.Token).ConfigureAwait(false)),
                        _ => ArrCompletedDownloadDispatchDecision.Fallback(
                            ArrCompletedDownloadFallbackReason.QueueTypeMismatch)
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return await RefreshAsync(
                        command,
                        target,
                        timing,
                        _arrRequestTimeout,
                        "refresh-fallback",
                        "queue-timeout",
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await RefreshAsync(
                        command,
                        target,
                        timing,
                        _arrRequestTimeout,
                        "refresh-fallback",
                        Classify(exception, "queue"),
                        ct)
                    .ConfigureAwait(false);
            }

            if (!decision.IsDirectScan)
            {
                return await RefreshAsync(
                        command,
                        target,
                        timing,
                        _arrRequestTimeout,
                        "refresh-fallback",
                        decision.FallbackReasonCode ?? "policy-refused",
                        ct)
                    .ConfigureAwait(false);
            }

            if (timing.Elapsed >= directDeadline)
            {
                return await RefreshAsync(
                        command,
                        target,
                        timing,
                        _arrRequestTimeout,
                        "refresh-fallback",
                        "direct-timeout",
                        ct)
                    .ConfigureAwait(false);
            }

            authorization = await AuthorizeNetworkAttemptAsync(command, timing, directDeadline, ct)
                .ConfigureAwait(false);
            if (!authorization.Authorized)
                return DispatchAttempt.Failed(Failure(target, authorization.ErrorCode));

            var directFailure = "direct-error";
            try
            {
                using var directCts = CreateRequestCancellation(ct, timing, directDeadline);
                if (directCts is null)
                {
                    directFailure = "direct-timeout";
                }
                else
                {
                    var request = decision.DirectScan!;
                    var response = target.Client switch
                    {
                        SonarrClient sonarr => await sonarr.DownloadedEpisodesScanAsync(
                            request.Path,
                            request.DownloadClientId,
                            directCts.Token).ConfigureAwait(false),
                        RadarrClient radarr => await radarr.DownloadedMoviesScanAsync(
                            request.Path,
                            request.DownloadClientId,
                            directCts.Token).ConfigureAwait(false),
                        _ => throw new InvalidOperationException("Unsupported direct-scan target.")
                    };
                    if (response.Id <= 0)
                        throw new InvalidDataException("Invalid ARR command response.");
                    return DispatchAttempt.Accepted(CreateResult(
                        command,
                        target,
                        response.Id,
                        "direct-scan",
                        fallbackReasonCode: null,
                        request.CommandName));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                directFailure = "direct-timeout";
            }
            catch (Exception exception)
            {
                directFailure = Classify(exception, "direct");
            }

            return await RefreshAsync(
                    command,
                    target,
                    timing,
                    _arrRequestTimeout,
                    "refresh-fallback",
                    directFailure,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DispatchAttempt.CancelledAttempt();
        }
        catch (Exception)
        {
            return DispatchAttempt.Failed(Failure(target, "dispatch-error"));
        }
    }

    private async Task<DispatchAttempt> RefreshAsync(
        ArrImportCommand command,
        DispatchTarget target,
        DispatchTiming timing,
        TimeSpan deadline,
        string dispatchMode,
        string fallbackReasonCode,
        CancellationToken ct)
    {
        var authorization = await AuthorizeNetworkAttemptAsync(command, timing, deadline, ct)
            .ConfigureAwait(false);
        if (!authorization.Authorized)
            return DispatchAttempt.Failed(Failure(target, authorization.ErrorCode));

        try
        {
            using var requestCts = CreateRequestCancellation(ct, timing, deadline);
            if (requestCts is null)
                return DispatchAttempt.Failed(Failure(target, CombineCodes(fallbackReasonCode, "refresh-timeout")));

            var response = await target.Client.RefreshMonitoredDownloads(requestCts.Token).ConfigureAwait(false);
            if (response.Id <= 0)
                throw new InvalidDataException("Invalid ARR command response.");
            return DispatchAttempt.Accepted(CreateResult(
                command,
                target,
                response.Id,
                dispatchMode,
                fallbackReasonCode,
                "RefreshMonitoredDownloads"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DispatchAttempt.Failed(Failure(target, CombineCodes(fallbackReasonCode, "refresh-timeout")));
        }
        catch (Exception exception)
        {
            return DispatchAttempt.Failed(Failure(
                target,
                CombineCodes(fallbackReasonCode, Classify(exception, "refresh"))));
        }
    }

    private static async Task<AuthorizationAttempt> AuthorizeNetworkAttemptAsync(
        ArrImportCommand command,
        DispatchTiming timing,
        TimeSpan deadline,
        CancellationToken ct)
    {
        if (command.LeaseToken is null)
            return AuthorizationAttempt.Rejected("lease-not-authorized");

        try
        {
            using var authorizationCts = CreateRequestCancellation(ct, timing, deadline);
            if (authorizationCts is null)
                return AuthorizationAttempt.Rejected("authorization-timeout");

            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            var authorized = await dbContext.ArrImportCommands
                .AsNoTracking()
                .AnyAsync(
                    row => row.Id == command.Id
                           && row.Status == ArrImportCommandStatus.Executing
                           && row.LeaseToken == command.LeaseToken
                           && row.VisibleAt != null
                           && row.LeaseExpiresAt > now,
                    authorizationCts.Token)
                .ConfigureAwait(false);
            return authorized
                ? AuthorizationAttempt.Success()
                : AuthorizationAttempt.Rejected("lease-not-authorized");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthorizationAttempt.Rejected("authorization-timeout");
        }
        catch (Exception)
        {
            return AuthorizationAttempt.Rejected("authorization-error");
        }
    }

    private static CancellationTokenSource? CreateRequestCancellation(
        CancellationToken ct,
        DispatchTiming timing,
        TimeSpan deadline)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = deadline - timing.Elapsed;
        if (remaining <= TimeSpan.Zero) return null;

        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(remaining);
        return requestCts;
    }

    private static ArrImportDispatchResult CreateResult(
        ArrImportCommand command,
        DispatchTarget target,
        int commandId,
        string dispatchMode,
        string? fallbackReasonCode,
        string commandName)
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var elapsedMilliseconds = command.VisibleAt is { } visibleAt
            ? (acceptedAt - visibleAt).TotalMilliseconds
            : 0;
        var boundedMilliseconds = (int)Math.Clamp(
            double.IsFinite(elapsedMilliseconds) ? elapsedMilliseconds : 0,
            0,
            int.MaxValue);
        return new ArrImportDispatchResult(
            target.App,
            target.InstanceKey,
            commandId,
            acceptedAt,
            dispatchMode,
            fallbackReasonCode,
            commandName,
            boundedMilliseconds);
    }

    private static string Failure(DispatchTarget target, string? code) =>
        $"{target.InstanceKey}:{code ?? "dispatch-error"}";

    private static string CombineCodes(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal) ? first : $"{first}+{second}";

    private static string Classify(Exception exception, string operation) => exception switch
    {
        HttpRequestException => $"{operation}-http",
        JsonException or NullReferenceException => $"{operation}-malformed",
        InvalidDataException when operation is "direct" or "refresh" => "invalid-command",
        InvalidDataException => $"{operation}-malformed",
        _ => $"{operation}-error"
    };

    private static async Task<ArrImportCommand?> TryClaimAsync(CancellationToken ct)
    {
        return await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            var claimableStatuses = new[]
            {
                ArrImportCommandStatus.Pending,
                ArrImportCommandStatus.Retry,
                ArrImportCommandStatus.WaitingForInvalidation
            };
            var candidateIds = await dbContext.ArrImportCommands
                .AsNoTracking()
                .Where(x => claimableStatuses.Contains(x.Status) && x.NextAttemptAt <= now
                            || x.Status == ArrImportCommandStatus.Executing && x.LeaseExpiresAt <= now)
                .OrderBy(x => x.NextAttemptAt)
                .ThenBy(x => x.CreatedAt)
                .Select(x => x.Id)
                .Take(10)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var candidateId in candidateIds)
            {
                var leaseToken = Guid.NewGuid();
                var changed = await dbContext.ArrImportCommands
                    .Where(x => x.Id == candidateId)
                    .Where(x => claimableStatuses.Contains(x.Status) && x.NextAttemptAt <= now
                                || x.Status == ArrImportCommandStatus.Executing && x.LeaseExpiresAt <= now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, ArrImportCommandStatus.Executing)
                        .SetProperty(x => x.LeaseToken, leaseToken)
                        .SetProperty(x => x.LeaseExpiresAt, now + LeaseDuration)
                        .SetProperty(x => x.UpdatedAt, now), ct)
                    .ConfigureAwait(false);
                if (changed == 0) continue;

                return await dbContext.ArrImportCommands
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == candidateId && x.LeaseToken == leaseToken, ct)
                    .ConfigureAwait(false);
            }

            return null;
        }, ct).ConfigureAwait(false);
    }

    private async Task<bool> HasBlockingWakePrerequisiteAsync(
        IReadOnlyCollection<string> requiredPaths,
        bool fenceRequired,
        bool wholeCacheVisibilityFencePending,
        CancellationToken ct)
    {
        if (!fenceRequired) return false;
        if (wholeCacheVisibilityFencePending) return true;
        return await _wakeRecheckInvalidation(requiredPaths, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasPendingInvalidationForWakeRecheckAsync(
        IReadOnlyCollection<string> requiredPaths,
        CancellationToken ct)
    {
        var paths = requiredPaths.ToArray();
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        return await dbContext.RcloneInvalidationItems
            .AsNoTracking()
            .AnyAsync(
                x => x.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath
                     || paths.Contains(x.Path),
                ct)
            .ConfigureAwait(false);
    }

    private static async Task MakeWaitingCommandDueNowAsync(Guid commandId, CancellationToken ct)
    {
        await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            return await dbContext.ArrImportCommands
                .Where(x => x.Id == commandId && x.Status == ArrImportCommandStatus.WaitingForInvalidation)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ArrImportCommandStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal static async Task<ArrVisibilityPublicationOutcome> EvaluateVisibilityAndPublishAsync(
        ArrImportCommand command,
        IReadOnlyCollection<string> requiredPaths,
        bool fenceRequired,
        CancellationToken ct,
        Func<DavDatabaseContext>? contextFactory = null)
    {
        contextFactory ??= DavDatabaseContextRuntimeFactory.Create;
        var paths = requiredPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        for (var attempt = 1; ; attempt++)
        {
            await using var dbContext = contextFactory();
            var providerName = dbContext.Database.ProviderName;
            try
            {
                if (string.Equals(
                        providerName,
                        "Microsoft.EntityFrameworkCore.Sqlite",
                        StringComparison.Ordinal))
                {
                    return await ExecuteSqliteVisibilityUnitAsync(
                            dbContext,
                            command,
                            paths,
                            fenceRequired,
                            ct)
                        .ConfigureAwait(false);
                }

                if (string.Equals(
                        providerName,
                        "Npgsql.EntityFrameworkCore.PostgreSQL",
                        StringComparison.Ordinal))
                {
                    return await ExecutePostgreSqlVisibilityUnitAsync(
                            dbContext,
                            command,
                            paths,
                            fenceRequired,
                            ct)
                        .ConfigureAwait(false);
                }

                throw new InvalidOperationException(
                    $"Unsupported database provider for ARR visibility publication: {providerName ?? "unknown"}.");
            }
            catch (Exception exception) when (
                attempt < VisibilityTransactionMaxAttempts
                && IsRetryableVisibilityTransactionFailure(exception, providerName))
            {
                if (string.Equals(
                        providerName,
                        "Microsoft.EntityFrameworkCore.Sqlite",
                        StringComparison.Ordinal))
                {
                    DatabaseTelemetry.Shared.RecordBusyRetry();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt * attempt), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<ArrVisibilityPublicationOutcome> ExecuteSqliteVisibilityUnitAsync(
        DavDatabaseContext dbContext,
        ArrImportCommand command,
        IReadOnlyCollection<string> requiredPaths,
        bool fenceRequired,
        CancellationToken ct)
    {
        await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = dbContext.Database.GetDbConnection() as SqliteConnection
                         ?? throw new InvalidOperationException("SQLite visibility unit requires SqliteConnection.");
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await using var contextTransaction = dbContext.Database.UseTransaction(transaction)
                                             ?? throw new InvalidOperationException(
                                                 "Could not enlist ARR visibility context in SQLite transaction.");
        var outcome = await ExecuteVisibilityUnitCoreAsync(
                dbContext,
                command,
                requiredPaths,
                fenceRequired,
                ct)
            .ConfigureAwait(false);
        await contextTransaction.CommitAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    private static async Task<ArrVisibilityPublicationOutcome> ExecutePostgreSqlVisibilityUnitAsync(
        DavDatabaseContext dbContext,
        ArrImportCommand command,
        IReadOnlyCollection<string> requiredPaths,
        bool fenceRequired,
        CancellationToken ct)
    {
        await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = dbContext.Database.GetDbConnection() as NpgsqlConnection
                         ?? throw new InvalidOperationException(
                             "PostgreSQL visibility unit requires NpgsqlConnection.");
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT current_schema()";
        var schemaName = await schemaCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new InvalidOperationException("PostgreSQL visibility unit could not resolve current schema.");
        var qualifiedInvalidationTable =
            $"\"{schemaName.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"RcloneInvalidationItems\"";
        var lockInvalidationTableSql = $"LOCK TABLE {qualifiedInvalidationTable} IN SHARE MODE";

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlRawAsync(
                "SET LOCAL lock_timeout = '500ms'",
                ct)
            .ConfigureAwait(false);
        // This must remain the first data lock. SHARE conflicts with the ROW EXCLUSIVE
        // lock taken by INSERT/UPDATE publishers, so absence and visibility publication
        // have one serial order at READ COMMITTED.
        await dbContext.Database.ExecuteSqlRawAsync(
                lockInvalidationTableSql,
                ct)
            .ConfigureAwait(false);
        var outcome = await ExecuteVisibilityUnitCoreAsync(
                dbContext,
                command,
                requiredPaths,
                fenceRequired,
                ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return outcome;
    }

    private static async Task<ArrVisibilityPublicationOutcome> ExecuteVisibilityUnitCoreAsync(
        DavDatabaseContext dbContext,
        ArrImportCommand command,
        IReadOnlyCollection<string> requiredPaths,
        bool fenceRequired,
        CancellationToken ct)
    {
        if (fenceRequired)
        {
            var paths = requiredPaths.ToArray();
            var pendingId = await dbContext.RcloneInvalidationItems
                .AsNoTracking()
                .Where(item =>
                    item.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath
                    || paths.Contains(item.Path))
                .Select(item => (Guid?)item.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (pendingId.HasValue)
                return ArrVisibilityPublicationOutcome.Blocked;
        }

        if (command.LeaseToken is null)
            return ArrVisibilityPublicationOutcome.Stale;
        if (command.VisibleAt is not null)
        {
            var claimIsCurrent = await dbContext.ArrImportCommands
                .AsNoTracking()
                .AnyAsync(x => x.Id == command.Id
                               && x.Status == ArrImportCommandStatus.Executing
                               && x.LeaseToken == command.LeaseToken
                               && x.VisibleAt != null, ct)
                .ConfigureAwait(false);
            return claimIsCurrent
                ? ArrVisibilityPublicationOutcome.Ready
                : ArrVisibilityPublicationOutcome.Stale;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = await dbContext.ArrImportCommands
            .Where(x => x.Id == command.Id
                        && x.Status == ArrImportCommandStatus.Executing
                        && x.LeaseToken == command.LeaseToken
                        && x.VisibleAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.VisibleAt, now)
                .SetProperty(x => x.Status, ArrImportCommandStatus.Pending)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.LastError, (string?)null), ct)
            .ConfigureAwait(false);
        if (changed > 0)
            return ArrVisibilityPublicationOutcome.Published;

        Log.Debug("Discarded stale ARR import visibility result for {CommandId}.", command.Id);
        return ArrVisibilityPublicationOutcome.Stale;
    }

    private static bool IsRetryableVisibilityTransactionFailure(
        Exception exception,
        string? providerName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(
                    providerName,
                    "Microsoft.EntityFrameworkCore.Sqlite",
                    StringComparison.Ordinal)
                && current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }

            if (string.Equals(
                    providerName,
                    "Npgsql.EntityFrameworkCore.PostgreSQL",
                    StringComparison.Ordinal)
                && current is PostgresException { SqlState: "55P03" or "40P01" })
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ReleaseLeaseAsync(
        ArrImportCommand command,
        ArrImportCommandStatus status,
        DateTimeOffset nextAttemptAt,
        string? error,
        string resultsJson,
        DateTimeOffset? completedAt,
        bool incrementAttempts,
        CancellationToken ct)
    {
        if (command.LeaseToken is null) return;
        var safeError = PublicDiagnosticContract.ArrImportFailureDetail(error);
        var changed = await DavDatabaseContext.ExecuteWithSqliteBusyRetryAsync(async () =>
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var now = DateTimeOffset.UtcNow;
            return await dbContext.ArrImportCommands
                .Where(x => x.Id == command.Id
                            && x.Status == ArrImportCommandStatus.Executing
                            && x.LeaseToken == command.LeaseToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(
                        x => x.Attempts,
                        x => incrementAttempts
                            ? x.Attempts < int.MaxValue ? x.Attempts + 1 : int.MaxValue
                            : x.Attempts)
                    .SetProperty(x => x.LastAttemptAt, x => incrementAttempts ? now : x.LastAttemptAt)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.CompletedAt, completedAt)
                    .SetProperty(x => x.ResultsJson, resultsJson)
                    .SetProperty(x => x.LastError, safeError), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (changed == 0)
            Log.Debug("Discarded stale ARR import command result for {CommandId}.", command.Id);
    }

    private static IReadOnlyCollection<string> DeserializeRequiredPaths(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("ARR import command contains invalid required-path data.", exception);
        }
    }

    private static IReadOnlyCollection<ArrImportDispatchResult> DeserializeResults(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ArrImportDispatchResult[]>(json) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("ARR import command contains invalid dispatch-result data.", exception);
        }
    }

    private static TimeSpan GetRetryDelay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 6);
        var delay = TimeSpan.FromSeconds(Math.Pow(2, exponent));
        return delay <= MaxRetryDelay ? delay : MaxRetryDelay;
    }

    private static int SaturatingIncrement(int value)
    {
        return value < int.MaxValue ? value + 1 : int.MaxValue;
    }

    private static string GetAppName(ArrClient client) => client switch
    {
        SonarrClient => "sonarr",
        RadarrClient => "radarr",
        LidarrClient => "lidarr",
        _ => "arr",
    };

    private static ArrCompletedDownloadClientKind GetClientKind(ArrClient client) => client switch
    {
        SonarrClient => ArrCompletedDownloadClientKind.Sonarr,
        RadarrClient => ArrCompletedDownloadClientKind.Radarr,
        LidarrClient => ArrCompletedDownloadClientKind.Lidarr,
        _ => ArrCompletedDownloadClientKind.Unknown
    };

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        return value[..maxLength];
    }

    private sealed record DispatchTarget(
        string App,
        string InstanceKey,
        ArrClient Client,
        ArrCompletedDownloadClientKind ClientKind,
        ArrCompletedDownloadRouteKind RouteKind,
        IReadOnlyCollection<ArrCompletedDownloadCorrelationFact> Correlations,
        DispatchTiming? Timing)
    {
        public override string ToString() => $"{InstanceKey}:redacted";
    }

    private readonly record struct DispatchTiming(long StartedAt)
    {
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedAt);

        public static DispatchTiming Start() => new(Stopwatch.GetTimestamp());
    }

    private sealed record DispatchAttempt(
        ArrImportDispatchResult? Result,
        string? Error,
        bool Cancelled)
    {
        public static DispatchAttempt Accepted(ArrImportDispatchResult result) => new(result, null, false);

        public static DispatchAttempt Failed(string error) => new(null, error, false);

        public static DispatchAttempt CancelledAttempt() => new(null, null, true);
    }

    private sealed record AuthorizationAttempt(bool Authorized, string? ErrorCode)
    {
        public static AuthorizationAttempt Success() => new(true, null);

        public static AuthorizationAttempt Rejected(string errorCode) => new(false, errorCode);
    }

    private sealed record OwnershipProbe(DispatchTarget Target, IReadOnlyCollection<string> Categories, string? Error);

    private sealed record TargetResolution(
        IReadOnlyList<DispatchTarget> Targets,
        string? Error,
        bool IsNoRoute)
    {
        public static TargetResolution Resolved(IReadOnlyList<DispatchTarget> targets) => new(targets, null, false);

        public static TargetResolution Failed(string error) => new([], error, false);

        public static TargetResolution NoRoute(string error) => new([], error, true);
    }
}

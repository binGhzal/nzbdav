using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;
using NzbWebDAV.Security;

namespace NzbWebDAV.Services;

public sealed record MaintenanceTaskProgress(
    string Message,
    int? Current = null,
    int? Total = null);

public delegate Task MaintenanceProgressReporter(MaintenanceTaskProgress progress);

public interface IMaintenanceTaskExecutor
{
    Task ExecuteAsync(
        MaintenanceRunKind kind,
        MaintenanceProgressReporter report,
        CancellationToken cancellationToken);
}

public sealed record MaintenanceRunStartResult(bool Started, MaintenanceRun Run);

public sealed record MaintenanceRunStatusSnapshot(MaintenanceRun? ActiveRun, MaintenanceRun? LastRun);

public sealed class MaintenanceRunService(IMaintenanceTaskExecutor taskExecutor) : BackgroundService
{
    private const int ActiveSlot = 1;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _wakeup = new(0, 1);
    private readonly object _activeCancellationLock = new();
    private CancellationTokenSource? _activeRunCancellation;
    private Guid? _activeRunId;

    public async Task<MaintenanceRunStartResult> TryStartRunAsync(
        MaintenanceRunKind kind,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var run = new MaintenanceRun
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Status = MaintenanceRunStatus.Queued,
                ActiveSlot = ActiveSlot,
                RequestedBy = requestedBy.Trim()[..Math.Min(requestedBy.Trim().Length, 32)],
                CreatedAt = now,
                UpdatedAt = now,
                ProgressCurrent = 0,
                Message = "Queued.",
            };

            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            dbContext.MaintenanceRuns.Add(run);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                dbContext.ChangeTracker.Clear();
                var activeRun = await dbContext.MaintenanceRuns
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ActiveSlot == ActiveSlot, cancellationToken)
                    .ConfigureAwait(false);
                if (activeRun is null) throw;
                return new MaintenanceRunStartResult(false, activeRun);
            }

            Awaken();
            return new MaintenanceRunStartResult(true, run);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<MaintenanceRun?> RequestCancellationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? activeCancellation = null;
        MaintenanceRun? run;
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            run = await MaintenanceRunTransitions.RequestCancellationAsync(
                    dbContext,
                    runId,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (run is null) return null;
            if (run.Status == MaintenanceRunStatus.Cancelled)
            {
                Awaken();
                return run;
            }
            if (run.Status == MaintenanceRunStatus.CancellationRequested)
            {
                lock (_activeCancellationLock)
                {
                    if (_activeRunId == runId)
                        activeCancellation = _activeRunCancellation;
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }

        try
        {
            activeCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The executor crossed a terminal state after the durable request was
            // committed but before the local token was signalled.
        }
        return run;
    }

    public async Task<MaintenanceRunStatusSnapshot> GetStatusAsync(
        MaintenanceRunKind? kind,
        CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var activeRun = await dbContext.MaintenanceRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActiveSlot == ActiveSlot, cancellationToken)
            .ConfigureAwait(false);
        var lastRunQuery = dbContext.MaintenanceRuns.AsNoTracking();
        if (kind.HasValue)
            lastRunQuery = lastRunQuery.Where(x => x.Kind == kind.Value);
        var lastRun = await lastRunQuery
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return new MaintenanceRunStatusSnapshot(activeRun, lastRun);
    }

    public async Task<IReadOnlyList<MaintenanceRun>> GetRunsAsync(
        MaintenanceRunKind? kind,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var query = dbContext.MaintenanceRuns.AsNoTracking();
        if (kind.HasValue)
            query = query.Where(x => x.Kind == kind.Value);
        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MaintenanceRun?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        return await dbContext.MaintenanceRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InterruptStaleRunsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var run = await TryMarkNextQueuedRunRunningAsync(stoppingToken).ConfigureAwait(false);
            if (run is null)
            {
                await _wakeup.WaitAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            await ExecuteRunAsync(run, stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task InterruptStaleRunsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var ids = await dbContext.MaintenanceRuns
            .Where(x => x.Status == MaintenanceRunStatus.Running
                        || x.Status == MaintenanceRunStatus.CancellationRequested)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in ids)
            await MaintenanceRunTransitions.InterruptActiveAsync(
                dbContext,
                id,
                DateTimeOffset.UtcNow,
                "Interrupted by application restart; not resumed.",
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<MaintenanceRun?> TryMarkNextQueuedRunRunningAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
            var run = await dbContext.MaintenanceRuns
                .Where(x => x.Status == MaintenanceRunStatus.Queued && x.ActiveSlot == ActiveSlot)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (run is null) return null;

            var now = DateTimeOffset.UtcNow;
            run.Status = MaintenanceRunStatus.Running;
            run.StartedAt = now;
            run.UpdatedAt = now;
            run.Message = "Starting.";
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return run;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task ExecuteRunAsync(MaintenanceRun run, CancellationToken stoppingToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_activeCancellationLock)
        {
            _activeRunId = run.Id;
            _activeRunCancellation = runCancellation;
        }

        try
        {
            await CancelIfAlreadyRequestedAsync(run.Id, runCancellation, stoppingToken).ConfigureAwait(false);
            await taskExecutor.ExecuteAsync(
                    run.Kind,
                    progress => PersistProgressAsync(run.Id, progress, runCancellation.Token),
                    runCancellation.Token)
                .ConfigureAwait(false);

            await MarkTerminalAsync(
                    run.Id,
                    MaintenanceRunStatus.Completed,
                    error: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            var status = stoppingToken.IsCancellationRequested
                ? MaintenanceRunStatus.Interrupted
                : MaintenanceRunStatus.Cancelled;
            await MarkTerminalAsync(run.Id, status, error: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Log.Error("Maintenance run {MaintenanceRunId} failed.", run.Id);
            await MarkTerminalAsync(
                    run.Id,
                    MaintenanceRunStatus.Failed,
                    PublicDiagnosticContract.Message(PublicDiagnosticKind.MaintenanceFailure),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_activeCancellationLock)
            {
                if (_activeRunId == run.Id)
                {
                    _activeRunId = null;
                    _activeRunCancellation = null;
                }
            }
        }
    }

    private static async Task CancelIfAlreadyRequestedAsync(
        Guid runId,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        var cancellationRequested = await dbContext.MaintenanceRuns
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == runId && x.Status == MaintenanceRunStatus.CancellationRequested,
                cancellationToken)
            .ConfigureAwait(false);
        if (cancellationRequested)
            runCancellation.Cancel();
    }

    private static async Task PersistProgressAsync(
        Guid runId,
        MaintenanceTaskProgress progress,
        CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        await MaintenanceRunTransitions.PersistProgressAsync(
            dbContext, runId, progress, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkTerminalAsync(
        Guid runId,
        MaintenanceRunStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        await MaintenanceRunTransitions.FinishAsync(
            dbContext, runId, status, error, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private void Awaken()
    {
        if (_wakeup.CurrentCount == 0)
            _wakeup.Release();
    }

}

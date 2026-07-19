using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;

namespace NzbWebDAV.Services;

public static class MaintenanceRunTransitions
{
    public static async Task<MaintenanceRun?> RequestCancellationAsync(
        DavDatabaseContext dbContext,
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cancelledQueued = await dbContext.MaintenanceRuns
            .Where(x => x.Id == id && x.Status == MaintenanceRunStatus.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, MaintenanceRunStatus.Cancelled)
                .SetProperty(x => x.ActiveSlot, (int?)null)
                .SetProperty(x => x.CancellationRequestedAt, now)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Message, "Cancelled before execution."), cancellationToken)
            .ConfigureAwait(false);

        if (cancelledQueued == 0)
        {
            await dbContext.MaintenanceRuns
                .Where(x => x.Id == id && x.Status == MaintenanceRunStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, MaintenanceRunStatus.CancellationRequested)
                    .SetProperty(x => x.CancellationRequestedAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Message, "Cancellation requested."), cancellationToken)
                .ConfigureAwait(false);
        }

        return await dbContext.MaintenanceRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<MaintenanceRun?> FinishAsync(
        DavDatabaseContext dbContext,
        Guid id,
        MaintenanceRunStatus intendedStatus,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (intendedStatus == MaintenanceRunStatus.Interrupted)
        {
            await InterruptActiveAsync(
                dbContext,
                id,
                now,
                "Interrupted by application shutdown; not resumed.",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var terminalMessage = intendedStatus switch
            {
                MaintenanceRunStatus.Failed => "Failed.",
                MaintenanceRunStatus.Cancelled => "Cancelled.",
                _ => null,
            };
            var truncatedError = PublicDiagnosticContract.FromOptional(
                error,
                PublicDiagnosticKind.MaintenanceFailure);
            var updated = await UpdateTerminalAsync(
                dbContext,
                id,
                MaintenanceRunStatus.Running,
                intendedStatus,
                terminalMessage,
                truncatedError,
                now,
                cancellationToken).ConfigureAwait(false);

            if (updated == 0)
            {
                await UpdateTerminalAsync(
                    dbContext,
                    id,
                    MaintenanceRunStatus.CancellationRequested,
                    MaintenanceRunStatus.Cancelled,
                    "Cancelled.",
                    null,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return await dbContext.MaintenanceRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<int> InterruptActiveAsync(
        DavDatabaseContext dbContext,
        Guid id,
        DateTimeOffset now,
        string message,
        CancellationToken cancellationToken)
    {
        return dbContext.MaintenanceRuns
            .Where(x => x.Id == id
                        && (x.Status == MaintenanceRunStatus.Running
                            || x.Status == MaintenanceRunStatus.CancellationRequested))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, MaintenanceRunStatus.Interrupted)
                .SetProperty(x => x.ActiveSlot, (int?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Message, Truncate(message, 2048)), cancellationToken);
    }

    public static Task<int> PersistProgressAsync(
        DavDatabaseContext dbContext,
        Guid id,
        MaintenanceTaskProgress progress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var query = dbContext.MaintenanceRuns
            .Where(x => x.Id == id && x.Status == MaintenanceRunStatus.Running);
        var message = Truncate(progress.Message, 2048);
        if (progress.Current.HasValue && progress.Total.HasValue)
        {
            return query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Message, message)
                .SetProperty(x => x.ProgressCurrent, progress.Current.Value)
                .SetProperty(x => x.ProgressTotal, progress.Total.Value), cancellationToken);
        }
        if (progress.Current.HasValue)
        {
            return query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Message, message)
                .SetProperty(x => x.ProgressCurrent, progress.Current.Value), cancellationToken);
        }
        if (progress.Total.HasValue)
        {
            return query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Message, message)
                .SetProperty(x => x.ProgressTotal, progress.Total.Value), cancellationToken);
        }
        return query.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.UpdatedAt, now)
            .SetProperty(x => x.Message, message), cancellationToken);
    }

    private static Task<int> UpdateTerminalAsync(
        DavDatabaseContext dbContext,
        Guid id,
        MaintenanceRunStatus expectedStatus,
        MaintenanceRunStatus terminalStatus,
        string? message,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var query = dbContext.MaintenanceRuns.Where(x => x.Id == id && x.Status == expectedStatus);
        return message is null
            ? query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, terminalStatus)
                .SetProperty(x => x.ActiveSlot, (int?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Error, error), cancellationToken)
            : query.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, terminalStatus)
                .SetProperty(x => x.ActiveSlot, (int?)null)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Message, message)
                .SetProperty(x => x.Error, error), cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        return value[..maxLength];
    }
}

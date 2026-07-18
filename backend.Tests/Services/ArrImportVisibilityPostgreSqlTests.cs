using System.Data.Common;
using backend.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrImportVisibilityPostgreSqlTests
{
    [PostgreSqlFact]
    public async Task PostgreSqlVisibilityUnit_SerializesConcurrentInvalidationEnqueueAfterAbsenceRead()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("arr_visibility_gap");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var command = await CreateExecutingCommandAsync(schema);
        var absenceRead = new PausingInvalidationReadInterceptor();

        DavDatabaseContext CreateVisibilityContext() =>
            new PostgreSqlDavDatabaseContext(schema.CreateOptions(absenceRead));

        var visibility = ArrImportCommandService.EvaluateVisibilityAndPublishAsync(
            command,
            ["/content/tv/Example"],
            fenceRequired: true,
            CancellationToken.None,
            CreateVisibilityContext);
        await absenceRead.Observed.WaitAsync(TimeSpan.FromSeconds(2));

        var enqueueAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueue = Task.Run(async () =>
        {
            await using var producer = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
            producer.RcloneInvalidationItems.Add(new RcloneInvalidationItem
            {
                Id = Guid.NewGuid(),
                Path = "/content/tv/Example",
                Revision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            enqueueAttempted.TrySetResult();
            await producer.SaveChangesAsync();
        });
        await enqueueAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.False(enqueue.IsCompleted);

        absenceRead.Release();
        Assert.Equal(
            ArrVisibilityPublicationOutcome.Published,
            await visibility.WaitAsync(TimeSpan.FromSeconds(2)));
        await enqueue.WaitAsync(TimeSpan.FromSeconds(2));

        await using var assertionContext = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var published = await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync();
        Assert.NotNull(published.VisibleAt);
        Assert.Equal(ArrImportCommandStatus.Pending, published.Status);
        Assert.Single(await assertionContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
    }

    [PostgreSqlFact]
    public async Task PostgreSqlVisibilityUnit_RetriesWholeUnitAfterBoundedShareLockTimeout()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("arr_visibility_retry");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        var command = await CreateExecutingCommandAsync(schema);
        await using var producer = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        await using var producerTransaction = await producer.Database.BeginTransactionAsync();
        producer.RcloneInvalidationItems.Add(new RcloneInvalidationItem
        {
            Id = Guid.NewGuid(),
            Path = "/content/tv/Example",
            Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        await producer.SaveChangesAsync();

        var factoryCalls = 0;
        DavDatabaseContext CreateVisibilityContext()
        {
            Interlocked.Increment(ref factoryCalls);
            return new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        }

        var visibility = ArrImportCommandService.EvaluateVisibilityAndPublishAsync(
            command,
            ["/content/tv/Example"],
            fenceRequired: true,
            CancellationToken.None,
            CreateVisibilityContext);
        var retryDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref factoryCalls) < 2 && DateTimeOffset.UtcNow < retryDeadline)
            await Task.Delay(20);
        Assert.True(factoryCalls >= 2, $"Expected lock-timeout retry; observed {factoryCalls} attempt(s).");

        await producerTransaction.CommitAsync();
        Assert.Equal(
            ArrVisibilityPublicationOutcome.Blocked,
            await visibility.WaitAsync(TimeSpan.FromSeconds(2)));
        await using var assertionContext = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        Assert.Null((await assertionContext.ArrImportCommands.AsNoTracking().SingleAsync()).VisibleAt);
    }

    private static async Task<ArrImportCommand> CreateExecutingCommandAsync(PostgreSqlTestSchema schema)
    {
        await using var setup = new PostgreSqlDavDatabaseContext(schema.CreateOptions());
        var now = DateTimeOffset.UtcNow;
        var history = new HistoryItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Unspecified),
            FileName = "Example.nzb",
            JobName = "Example",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
            TotalSegmentBytes = 1024,
            DownloadTimeSeconds = 1
        };
        var command = new ArrImportCommand
        {
            Id = Guid.NewGuid(),
            HistoryItemId = history.Id,
            Category = history.Category,
            RequiredInvalidationPathsJson = "[\"/content/tv/Example\"]",
            Status = ArrImportCommandStatus.Executing,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            VisibleAt = null,
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = now.AddMinutes(1)
        };
        setup.HistoryItems.Add(history);
        setup.ArrImportCommands.Add(command);
        await setup.SaveChangesAsync();
        setup.Entry(command).State = EntityState.Detached;
        return command;
    }

    private sealed class PausingInvalidationReadInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _observed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;

        internal Task Observed => _observed.Task;

        internal void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RcloneInvalidationItems", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _observed.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}

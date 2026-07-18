using backend.Tests.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Reflection;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace backend.Tests.Services;

public sealed class ImportReceiptPostgreSqlConcurrencyTests
{
    [PostgreSqlFact]
    public async Task ConcurrentAvailableClaimsReportExactlyOneChange()
    {
        await WithSchemaAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(now);
            await SeedAsync(options, receipt);
            var barrier = new ReceiptCasCommandBarrier(2);
            var concurrentOptions = AddInterceptor(options, barrier);
            await using var firstContext = new PostgreSqlDavDatabaseContext(concurrentOptions);
            await using var secondContext = new PostgreSqlDavDatabaseContext(concurrentOptions);

            var results = await Task.WhenAll(
                new ImportReceiptService(firstContext).ClaimAsync(
                    new ImportClaimRequest(receipt.DavItemId, receipt.HistoryItemId, now.AddSeconds(1)),
                    CancellationToken.None),
                new ImportReceiptService(secondContext).ClaimAsync(
                    new ImportClaimRequest(receipt.DavItemId, receipt.HistoryItemId, now.AddSeconds(2)),
                    CancellationToken.None));

            Assert.Single(results, x => x.Changed);
            Assert.All(results, x => Assert.Equal(ImportReceiptState.UnlinkClaimed, x.State));
            Assert.Equal(2, barrier.Arrivals);
        });
    }

    [PostgreSqlFact]
    public async Task ConcurrentLegacyClaimsCreateOneDurableReceipt()
    {
        await WithSchemaAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var davItemId = Guid.NewGuid();
            var historyItemId = Guid.NewGuid();
            var barrier = new ReceiptInsertSaveBarrier(2);
            var concurrentOptions = AddInterceptor(options, barrier);
            await using var firstContext = new PostgreSqlDavDatabaseContext(concurrentOptions);
            await using var secondContext = new PostgreSqlDavDatabaseContext(concurrentOptions);

            var results = await Task.WhenAll(
                new ImportReceiptService(firstContext).ClaimAsync(
                    new ImportClaimRequest(davItemId, historyItemId, now), CancellationToken.None),
                new ImportReceiptService(secondContext).ClaimAsync(
                    new ImportClaimRequest(davItemId, historyItemId, now.AddSeconds(1)), CancellationToken.None));

            Assert.Single(results, x => x.Changed);
            Assert.All(results, x => Assert.Equal(ImportReceiptState.UnlinkClaimed, x.State));
            await using var assertionContext = new PostgreSqlDavDatabaseContext(options);
            Assert.Equal(1, await assertionContext.ImportReceipts.CountAsync(x =>
                x.DavItemId == davItemId && x.HistoryItemId == historyItemId));
        });
    }

    [PostgreSqlFact]
    public async Task StaleImportedTransitionCannotOverwriteRemoved()
    {
        await WithSchemaAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(now);
            receipt.State = ImportReceiptState.UnlinkClaimed;
            await SeedAsync(options, receipt);
            await using var staleContext = new PostgreSqlDavDatabaseContext(options);
            await using var removingContext = new PostgreSqlDavDatabaseContext(options);
            _ = await staleContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id);

            await new ImportReceiptService(removingContext)
                .MarkRemovedAsync(receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None);
            var staleResult = await new ImportReceiptService(staleContext)
                .MarkImportedAsync(
                    receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(2), CancellationToken.None);

            Assert.False(staleResult.Changed);
            Assert.Equal(ImportReceiptState.Removed, staleResult.State);
        });
    }

    [PostgreSqlFact]
    public async Task CallerOwnedSabRemovalRollsBackToSavepointBeforeOuterCommit()
    {
        await WithSchemaAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var historyId = Guid.NewGuid();
            var receipt = CreateReceipt(now);
            receipt.HistoryItemId = historyId;
            receipt.State = ImportReceiptState.Imported;
            await using (var setup = new PostgreSqlDavDatabaseContext(options))
            {
                setup.HistoryItems.Add(new HistoryItem
                {
                    Id = historyId,
                    CreatedAt = DateTime.SpecifyKind(
                        new DateTime(2026, 7, 12, 12, 0, 0),
                        DateTimeKind.Unspecified),
                    FileName = "postgres-savepoint.nzb",
                    JobName = "postgres-savepoint",
                    Category = "movies",
                    DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                    TotalSegmentBytes = 1024,
                    DownloadTimeSeconds = 1
                });
                setup.ImportReceipts.Add(receipt);
                setup.ConfigItems.Add(new ConfigItem { ConfigName = "test.outer", ConfigValue = "before" });
                await setup.SaveChangesAsync();
            }
            var failingOptions = AddInterceptor(options, new FailingConcurrencySaveInterceptor());
            var websocketManager = new WebsocketManager();
            await using (var callerContext = new PostgreSqlDavDatabaseContext(failingOptions))
            await using (var outerTransaction = await callerContext.Database.BeginTransactionAsync())
            {
                var callerConfig = await callerContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer");
                callerConfig.ConfigValue = "after";
                var httpContext = new DefaultHttpContext();
                httpContext.Request.QueryString = new QueryString($"?value={historyId}");
                var request = await RemoveFromHistoryRequest.New(httpContext);
                var controller = new RemoveFromHistoryController(
                    httpContext,
                    new DavDatabaseClient(callerContext),
                    new ConfigManager(),
                    websocketManager);

                var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => controller.RemoveFromHistory(request));

                Assert.Equal("forced PostgreSQL savepoint rollback", exception.Message);
                Assert.Equal(EntityState.Modified, callerContext.Entry(callerConfig).State);
                Assert.False(WasHistoryRemovalBroadcast(websocketManager));
                await callerContext.SaveChangesAsync(CancellationToken.None);
                await outerTransaction.CommitAsync(CancellationToken.None);
            }

            await using var assertionContext = new PostgreSqlDavDatabaseContext(options);
            Assert.NotNull(await assertionContext.HistoryItems.SingleOrDefaultAsync(x => x.Id == historyId));
            Assert.Equal(
                ImportReceiptState.Imported,
                (await assertionContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id)).State);
            Assert.Empty(await assertionContext.HistoryCleanupItems.ToListAsync());
            Assert.Equal(
                "after",
                (await assertionContext.ConfigItems.SingleAsync(x => x.ConfigName == "test.outer")).ConfigValue);
        });
    }

    private static async Task WithSchemaAsync(
        Func<DbContextOptions<PostgreSqlDavDatabaseContext>, Task> test)
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("import_receipt");
        await PostgreSqlNativeMigrator.MigrateAsync(schema.ConnectionString);
        await test(schema.CreateOptions());
    }

    private static DbContextOptions<PostgreSqlDavDatabaseContext> AddInterceptor(
        DbContextOptions<PostgreSqlDavDatabaseContext> options,
        IInterceptor interceptor)
    {
        var connectionString = options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString!;
        return new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(DatabaseMigrationPolicy.PostgreSqlHistoryTableName))
            .AddInterceptors(interceptor)
            .Options;
    }

    private static async Task SeedAsync(
        DbContextOptions<PostgreSqlDavDatabaseContext> options,
        ImportReceipt receipt)
    {
        await using var context = new PostgreSqlDavDatabaseContext(options);
        context.ImportReceipts.Add(receipt);
        await context.SaveChangesAsync();
    }

    private static ImportReceipt CreateReceipt(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = Guid.NewGuid(),
        HistoryItemId = Guid.NewGuid(),
        State = ImportReceiptState.Available,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static bool WasHistoryRemovalBroadcast(WebsocketManager websocketManager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_lastMessage",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var messages = Assert.IsType<Dictionary<WebsocketTopic, string>>(field.GetValue(websocketManager));
        return messages.ContainsKey(WebsocketTopic.HistoryItemRemoved);
    }

    private sealed class ReceiptCasCommandBarrier(int participants) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("ImportReceipts", StringComparison.OrdinalIgnoreCase))
                return result;

            if (Interlocked.Increment(ref _arrivals) >= participants)
                _release.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class ReceiptInsertSaveBarrier(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<ImportReceipt>()
                    .Any(x => x.State is EntityState.Added or EntityState.Modified) != true)
                return result;

            if (Interlocked.Increment(ref _arrivals) >= participants)
                _release.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class FailingConcurrencySaveInterceptor : SaveChangesInterceptor
    {
        private int _saveAttempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            return Interlocked.Increment(ref _saveAttempts) == 1
                ? ValueTask.FromException<InterceptionResult<int>>(
                    new DbUpdateConcurrencyException("forced PostgreSQL savepoint rollback"))
                : ValueTask.FromResult(result);
        }
    }
}

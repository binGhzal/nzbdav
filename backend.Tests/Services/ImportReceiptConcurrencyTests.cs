using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ImportReceiptConcurrencyTests
{
    [Fact]
    public async Task ConcurrentClaimsOfAvailableReceiptReportExactlyOneChange()
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(ImportReceiptState.Available, now);
            await SeedAsync(options, receipt);
            var barrier = new ReceiptCasCommandBarrier(2);
            var concurrentOptions = AddInterceptor(options, barrier);
            await using var firstContext = new DavDatabaseContext(concurrentOptions);
            await using var secondContext = new DavDatabaseContext(concurrentOptions);

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

    [Fact]
    public async Task ConcurrentLegacyClaimsCreateOneDurableReceiptAndBothSucceed()
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var davItemId = Guid.NewGuid();
            var historyItemId = Guid.NewGuid();
            var barrier = new ReceiptInsertSaveBarrier(2);
            var concurrentOptions = AddInterceptor(options, barrier);
            await using var firstContext = new DavDatabaseContext(concurrentOptions);
            await using var secondContext = new DavDatabaseContext(concurrentOptions);

            var results = await Task.WhenAll(
                new ImportReceiptService(firstContext).ClaimAsync(
                    new ImportClaimRequest(davItemId, historyItemId, now), CancellationToken.None),
                new ImportReceiptService(secondContext).ClaimAsync(
                    new ImportClaimRequest(davItemId, historyItemId, now.AddSeconds(1)), CancellationToken.None));

            Assert.Single(results, x => x.Changed);
            Assert.All(results, x => Assert.Equal(ImportReceiptState.UnlinkClaimed, x.State));
            await using var assertionContext = new DavDatabaseContext(options);
            Assert.Equal(1, await assertionContext.ImportReceipts.CountAsync(x =>
                x.DavItemId == davItemId && x.HistoryItemId == historyItemId));
        });
    }

    [Theory]
    [InlineData(ImportReceiptState.Imported)]
    [InlineData(ImportReceiptState.NeedsReview)]
    public async Task StaleTransitionCannotOverwriteConcurrentRemoved(ImportReceiptState staleTarget)
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(ImportReceiptState.UnlinkClaimed, now);
            await SeedAsync(options, receipt);
            await using var staleContext = new DavDatabaseContext(options);
            await using var removingContext = new DavDatabaseContext(options);
            _ = await staleContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id);

            await new ImportReceiptService(removingContext)
                .MarkRemovedAsync(receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None);
            if (staleTarget == ImportReceiptState.Imported)
            {
                await new ImportReceiptService(staleContext)
                    .MarkImportedAsync(receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(2), CancellationToken.None);
            }
            else
            {
                await new ImportReceiptService(staleContext)
                    .MarkNeedsReviewAsync(
                        receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(2), "stale", CancellationToken.None);
            }

            staleContext.ChangeTracker.Clear();
            Assert.Equal(
                ImportReceiptState.Removed,
                (await staleContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id)).State);
        });
    }

    [Fact]
    public async Task StaleClaimAfterRemovalReturnsRemovedWithoutMovingBackward()
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(ImportReceiptState.Available, now);
            await SeedAsync(options, receipt);
            await using var staleClaimContext = new DavDatabaseContext(options);
            await using var removingContext = new DavDatabaseContext(options);
            _ = await staleClaimContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id);
            await new ImportReceiptService(removingContext)
                .MarkRemovedAsync(receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None);

            var result = await new ImportReceiptService(staleClaimContext).ClaimAsync(
                new ImportClaimRequest(receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(2)),
                CancellationToken.None);

            Assert.False(result.Changed);
            Assert.Equal(ImportReceiptState.Removed, result.State);
        });
    }

    [Fact]
    public async Task ConcurrentClaimVersusRemovalFinishesAtTerminalRemoved()
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(ImportReceiptState.Available, now);
            await SeedAsync(options, receipt);
            await using var claimContext = new DavDatabaseContext(options);
            await using var removalContext = new DavDatabaseContext(options);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var claimTask = Task.Run(async () =>
            {
                await gate.Task;
                return await new ImportReceiptService(claimContext).ClaimAsync(
                    new ImportClaimRequest(receipt.DavItemId, receipt.HistoryItemId, now.AddMinutes(1)),
                    CancellationToken.None);
            });
            var removalTask = Task.Run(async () =>
            {
                await gate.Task;
                return await new ImportReceiptService(removalContext).MarkRemovedAsync(
                    receipt.HistoryItemId, now.AddMinutes(1), CancellationToken.None);
            });
            gate.SetResult();
            await Task.WhenAll(claimTask, removalTask);

            await using var assertionContext = new DavDatabaseContext(options);
            Assert.Equal(
                ImportReceiptState.Removed,
                (await assertionContext.ImportReceipts.SingleAsync(x => x.Id == receipt.Id)).State);
            Assert.Equal(ImportReceiptState.Removed, Assert.Single(removalTask.Result).State);
        });
    }

    [Fact]
    public async Task LegacyClaimDoesNotSwallowUnrelatedDatabaseFailure()
    {
        await WithSqliteDatabaseAsync(async options =>
        {
            var failingOptions = AddInterceptor(options, new UnrelatedFailureInterceptor());
            await using var context = new DavDatabaseContext(failingOptions);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new ImportReceiptService(context).ClaimAsync(
                    new ImportClaimRequest(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow),
                    CancellationToken.None));
        });
    }

    private static DbContextOptions<DavDatabaseContext> AddInterceptor(
        DbContextOptions<DavDatabaseContext> options,
        IInterceptor interceptor)
    {
        var connectionString = options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString!;
        return new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
    }

    private static async Task WithSqliteDatabaseAsync(
        Func<DbContextOptions<DavDatabaseContext>, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"receipt-{Guid.NewGuid():N}.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={path};Default Timeout=30")
            .Options;
        try
        {
            await using (var setup = new DavDatabaseContext(options))
                await setup.Database.EnsureCreatedAsync();
            await test(options);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static async Task SeedAsync(
        DbContextOptions<DavDatabaseContext> options,
        ImportReceipt receipt)
    {
        await using var context = new DavDatabaseContext(options);
        context.ImportReceipts.Add(receipt);
        await context.SaveChangesAsync();
    }

    private static ImportReceipt CreateReceipt(ImportReceiptState state, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DavItemId = Guid.NewGuid(),
        HistoryItemId = Guid.NewGuid(),
        State = state,
        CreatedAt = now,
        UpdatedAt = now
    };

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

    private sealed class UnrelatedFailureInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("unrelated database failure"));
    }
}

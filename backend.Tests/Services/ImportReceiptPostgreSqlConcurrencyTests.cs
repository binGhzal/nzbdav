using backend.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

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
            var barrier = new ReceiptSaveBarrier(2);
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
            var barrier = new ReceiptSaveBarrier(2);
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

    [PostgreSqlFact]
    public async Task StaleImportedTransitionCannotOverwriteRemoved()
    {
        await WithSchemaAsync(async options =>
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = CreateReceipt(now);
            receipt.State = ImportReceiptState.UnlinkClaimed;
            await SeedAsync(options, receipt);
            await using var staleContext = new DavDatabaseContext(options);
            await using var removingContext = new DavDatabaseContext(options);
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

    private static async Task WithSchemaAsync(
        Func<DbContextOptions<DavDatabaseContext>, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.TestConnectionStringVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"import_receipt_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schemaName}\"");
        try
        {
            var schemaConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schemaName
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseNpgsql(schemaConnectionString)
                .Options;
            await using (var setup = new DavDatabaseContext(options))
                await setup.Database.MigrateAsync();
            await test(options);
        }
        finally
        {
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE");
        }
    }

    private static DbContextOptions<DavDatabaseContext> AddInterceptor(
        DbContextOptions<DavDatabaseContext> options,
        IInterceptor interceptor)
    {
        var connectionString = options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString!;
        return new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptor)
            .Options;
    }

    private static async Task SeedAsync(
        DbContextOptions<DavDatabaseContext> options,
        ImportReceipt receipt)
    {
        await using var context = new DavDatabaseContext(options);
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ReceiptSaveBarrier(int participants) : SaveChangesInterceptor
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
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}

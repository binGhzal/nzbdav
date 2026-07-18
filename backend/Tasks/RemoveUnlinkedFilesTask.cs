using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class RemoveUnlinkedFilesTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    bool isDryRun,
    MaintenanceProgressReporter? progressReporter = null
) : BaseTask(websocketManager, WebsocketTopic.CleanupTaskProgress, progressReporter)
{
    private static readonly object AuditReportLock = new();
    private static List<string> _allRemovedPaths = [];

    private record UnlinkedItemInfo(string Id, int Type, string Path);
    private record ParsedUnlinkedItemInfo(string RawId, Guid Id, int Type, string Path);
    private record ParsedUnlinkedItemBatch(
        List<ParsedUnlinkedItemInfo> ValidItems,
        List<UnlinkedItemInfo> MalformedItems)
    {
        public int Count => ValidItems.Count + MalformedItems.Count;
        public IEnumerable<string> Paths => ValidItems.Select(x => x.Path).Concat(MalformedItems.Select(x => x.Path));
    }

    protected override Task ExecuteInternal(CancellationToken cancellationToken)
    {
        return RemoveUnlinkedFiles(cancellationToken);
    }

    private async Task RemoveUnlinkedFiles(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearAuditReport();

        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await RunWithConnectionCleanupAsync(
            async () =>
            {
                await CreateLinkedIdsTemporaryTableAsync(dbContext, cancellationToken).ConfigureAwait(false);

                // get linked file paths
                Report("Scanning all linked files...");
                var startTime = DateTime.Now;
                var linkedIdCount = await WriteLinkedIdsToTable(dbContext).ConfigureAwait(false);
                if (linkedIdCount < 5)
                {
                    Report($"Aborted: " +
                           $"There are less than five unique linked files found in your library. " +
                           $"Cancelling operation to prevent accidental bulk deletion.");
                    return;
                }

                Report("Searching for unlinked webdav items...");
                var unlinkedItems = await CountUnlinkedItems(dbContext, startTime).ConfigureAwait(false);
                Report($"Found {unlinkedItems} webdav items to remove.");

                if (isDryRun)
                {
                    await DryRunIdentifyUnlinkedFiles(dbContext, startTime).ConfigureAwait(false);
                    Report($"Done. Identified {GetAuditReportPathCount()} unlinked files.");
                }
                else
                {
                    await RemoveUnlinkedItems(dbContext, startTime, unlinkedItems).ConfigureAwait(false);
                    await RemoveEmptyDirectories(dbContext, startTime).ConfigureAwait(false);
                    Report($"Done. Removed {GetAuditReportPathCount()} unlinked files.");
                }
            },
            () => DropLinkedIdsTemporaryTableAsync(dbContext),
            () => dbContext.Database.CloseConnectionAsync()).ConfigureAwait(false);
    }

    internal static async Task RunWithConnectionCleanupAsync(
        Func<Task> body,
        Func<Task> dropTemporaryTable,
        Func<Task> closeConnection)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(dropTemporaryTable);
        ArgumentNullException.ThrowIfNull(closeConnection);

        Exception? primaryFailure = null;
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        var cleanupFailures = new List<Exception>(2);
        try
        {
            await dropTemporaryTable().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
            Log.Error(exception, "Failed to drop the remove-unlinked temporary table during connection cleanup.");
        }

        try
        {
            await closeConnection().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
            Log.Error(exception, "Failed to close the remove-unlinked database connection during cleanup.");
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException("Multiple remove-unlinked connection cleanup operations failed.", cleanupFailures);
    }

    private static async Task CreateLinkedIdsTemporaryTableAsync(
        DavDatabaseContext dbContext,
        CancellationToken cancellationToken)
    {
        var isPostgreSql = dbContext.Database.IsNpgsql();

        // The repeated unqualified drops clean both a pooled connection's old temp
        // object and the persistent tables left by older releases. Temporary tables
        // take name-resolution precedence over application-schema tables.
        await dbContext.Database.ExecuteSqlRawAsync(
            isPostgreSql
                ? """
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS tmp_linked_files;
                  DROP TABLE IF EXISTS tmp_linked_files;
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS tmp_linked_files_unique;
                  DROP TABLE IF EXISTS tmp_linked_files_unique;
                  """
                : """
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  """,
            cancellationToken).ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlRawAsync(
            isPostgreSql
                ? """
                  CREATE TEMPORARY TABLE "TMP_LINKED_FILES" (
                      "Id" uuid NOT NULL PRIMARY KEY
                  ) ON COMMIT PRESERVE ROWS;
                  """
                : """
                  CREATE TEMPORARY TABLE "TMP_LINKED_FILES" (
                      "Id" TEXT NOT NULL PRIMARY KEY
                  );
                  """,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task DropLinkedIdsTemporaryTableAsync(DavDatabaseContext dbContext)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            dbContext.Database.IsNpgsql()
                ? """
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS tmp_linked_files;
                  DROP TABLE IF EXISTS tmp_linked_files;
                  DROP TABLE IF EXISTS tmp_linked_files_unique;
                  DROP TABLE IF EXISTS tmp_linked_files_unique;
                  """
                : """
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
                  """,
            CancellationToken.None);
    }

    private async Task<long> WriteLinkedIdsToTable(DavDatabaseContext dbContext)
    {
        var isPostgreSql = dbContext.Database.IsNpgsql();

        long scannedCount = 0;
        long uniqueCount = 0;
        var batches = GetLinkedIds().ToBatches(100);
        foreach (var batch in batches)
        {
            ExecutionCancellationToken.ThrowIfCancellationRequested();
            var parameters = isPostgreSql
                ? batch.Select(id => (object)id).ToArray()
                : batch.Select(id => (object)id.ToString().ToUpperInvariant()).ToArray();
            var values = string.Join(",", parameters.Select((_, index) => $"({{{index}}})"));
            var insertedCount = await dbContext.Database.ExecuteSqlAsync(
                FormattableStringFactory.Create(
                    (isPostgreSql
                        ? "INSERT INTO \"TMP_LINKED_FILES\" (\"Id\") VALUES " + values
                          + " ON CONFLICT (\"Id\") DO NOTHING"
                        : "INSERT OR IGNORE INTO \"TMP_LINKED_FILES\" (\"Id\") VALUES " + values),
                    parameters),
                ExecutionCancellationToken).ConfigureAwait(false);
            scannedCount += batch.Count;
            uniqueCount += insertedCount;
        }

        Report($"Indexing {uniqueCount} unique linked files from {scannedCount} library entries...");
        ExecutionCancellationToken.ThrowIfCancellationRequested();
        return uniqueCount;
    }

    private IEnumerable<Guid> GetLinkedIds()
    {
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
        var linkedIds = OrganizedLinksUtil
            .GetLibraryDavItemLinks(configManager)
            .Select(x => x.DavItemId);

        long count = 0;
        foreach (var linkedId in linkedIds)
        {
            ExecutionCancellationToken.ThrowIfCancellationRequested();
            count++;
            debounce(() => Report($"Scanning all linked files...\nFound {count}..."));
            yield return linkedId;
        }

        Report($"Scanning all linked files...\nFound {count}...");
    }

    private async Task<long> CountUnlinkedItems(DavDatabaseContext dbContext, DateTime createdBefore)
    {
        var createdBeforeParameter = CreateCreatedBeforeParameter(dbContext, createdBefore);
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;

        var count = await dbContext.Database
            .SqlQueryRaw<long>(
                """
                SELECT COUNT(i."Id") AS "Value" FROM "DavItems" AS i
                LEFT JOIN "TMP_LINKED_FILES" AS t ON i."Id" = t."Id"
                WHERE i."Type" = {0}
                  AND i."HistoryItemId" IS NULL
                  AND i."CreatedAt" < {1}
                  AND t."Id" IS NULL
                """,
                usenetFileType,
                createdBeforeParameter)
            .FirstAsync(ExecutionCancellationToken);

        return count;
    }

    private async Task RemoveUnlinkedItems(
        DavDatabaseContext dbContext,
        DateTime createdBefore,
        long totalCount)
    {
        Report("Removing unlinked items...");
        long removed = 0;

        while (true)
        {
            ExecutionCancellationToken.ThrowIfCancellationRequested();
            // Select items to delete (batch of 100)
            var itemsToDelete = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    """
                    SELECT CAST("Id" AS TEXT) AS "Id", "Type", "Path" FROM "DavItems"
                    WHERE "Type" = {0}
                      AND "HistoryItemId" IS NULL
                      AND "CreatedAt" < {1}
                      AND "Id" NOT IN (SELECT "Id" FROM "TMP_LINKED_FILES")
                    LIMIT 100
                    """,
                    (int)DavItem.ItemType.UsenetFile,
                    CreateCreatedBeforeParameter(dbContext, createdBefore))
                .ToListAsync(ExecutionCancellationToken);

            // If there are no more items to delete, we're done.
            if (itemsToDelete.Count == 0)
                break;

            var itemsToDeleteBatch = ParseUnlinkedItems(itemsToDelete);
            if (itemsToDeleteBatch.Count == 0)
            {
                Report($"Removing unlinked items...\nSkipped {itemsToDelete.Count} malformed item IDs.");
                break;
            }

            await DeleteBatchAndCommitInvalidationsAsync(
                    dbContext,
                    itemsToDeleteBatch,
                    "Committing unlinked item batch...")
                .ConfigureAwait(false);

            // The database mutation and its visibility fence are durable before
            // the content snapshot is requested or flushed.
            AddAuditReportPaths(itemsToDeleteBatch.Paths);
            ContentIndexSnapshotWriterService.RequestSnapshot();
            await ContentIndexSnapshotWriterService.FlushNowAsync(ExecutionCancellationToken).ConfigureAwait(false);

            removed += itemsToDeleteBatch.Count;

            var progress = ToProgressValues(removed, totalCount);
            Report(
                $"Removing unlinked items...\nRemoved {removed}/{totalCount}...",
                progress.Current,
                progress.Total);
        }

        Report($"Removing unlinked items...\nRemoved {removed} of {totalCount}...");
    }

    private async Task RemoveEmptyDirectories(DavDatabaseContext dbContext, DateTime createdBefore)
    {
        Report($"Removing empty directories...");
        var removed = 0;

        while (true)
        {
            ExecutionCancellationToken.ThrowIfCancellationRequested();
            // Find empty directories (no children).
            // Only target regular directories (SubType = Directory), not root folders.
            var emptyDirs = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    """
                    SELECT CAST(d."Id" AS TEXT) AS "Id", d."Type", d."Path" FROM "DavItems" AS d
                    LEFT JOIN "DavItems" AS c ON c."ParentId" = d."Id"
                    WHERE d."SubType" = {0}
                      AND d."CreatedAt" < {1}
                      AND c."Id" IS NULL
                    LIMIT 100
                    """,
                    (int)DavItem.ItemSubType.Directory,
                    CreateCreatedBeforeParameter(dbContext, createdBefore))
                .ToListAsync(ExecutionCancellationToken);

            if (emptyDirs.Count == 0)
                break;

            var emptyDirBatch = ParseUnlinkedItems(emptyDirs);
            if (emptyDirBatch.Count == 0)
            {
                Report($"Removing empty directories...\nSkipped {emptyDirs.Count} malformed item IDs.");
                break;
            }

            await DeleteBatchAndCommitInvalidationsAsync(
                    dbContext,
                    emptyDirBatch,
                    "Committing empty-directory batch...")
                .ConfigureAwait(false);

            ContentIndexSnapshotWriterService.RequestSnapshot();
            await ContentIndexSnapshotWriterService.FlushNowAsync(ExecutionCancellationToken).ConfigureAwait(false);

            removed += emptyDirBatch.Count;
            Report($"Removing empty directories...\nRemoved {removed}...");
        }
    }

    private async Task DeleteBatchAndCommitInvalidationsAsync(
        DavDatabaseContext dbContext,
        ParsedUnlinkedItemBatch batch,
        string commitProgressMessage)
    {
        // The durable maintenance reporter writes through a separate context.
        // Persist this boundary before taking SQLite's single writer lock.
        Report(commitProgressMessage);
        ExecutionCancellationToken.ThrowIfCancellationRequested();

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(ExecutionCancellationToken)
            .ConfigureAwait(false);
        try
        {
            await DeleteDavItemsAsync(dbContext, batch, ExecutionCancellationToken).ConfigureAwait(false);

            dbContext.EnqueueRcloneVfsForget(batch.ValidItems.Select(x => new DavItem
            {
                Id = x.Id,
                Type = (DavItem.ItemType)x.Type,
                Path = x.Path
            }).ToList());
            dbContext.EnqueueRcloneVfsForgetPaths(GetParentDirectories(batch.MalformedItems.Select(x => x.Path)));
            await dbContext.SaveChangesAsync(ExecutionCancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(ExecutionCancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                Log.Error(
                    rollbackFailure,
                    "Failed to roll back a remove-unlinked batch after {FailureType}.",
                    primaryFailure.GetType().Name);
            }

            dbContext.ClearChangeTracker();
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    private async Task DryRunIdentifyUnlinkedFiles(DavDatabaseContext dbContext, DateTime createdBefore)
    {
        var unlinkedFiles = await dbContext.Database
            .SqlQueryRaw<UnlinkedItemInfo>(
                """
                SELECT CAST("Id" AS TEXT) AS "Id", "Type", "Path" FROM "DavItems"
                WHERE "Type" = {0}
                  AND "HistoryItemId" IS NULL
                  AND "CreatedAt" < {1}
                  AND "Id" NOT IN (SELECT "Id" FROM "TMP_LINKED_FILES")
                """,
                (int)DavItem.ItemType.UsenetFile,
                CreateCreatedBeforeParameter(dbContext, createdBefore))
            .ToListAsync(ExecutionCancellationToken);

        ReplaceAuditReport(unlinkedFiles.Select(x => x.Path));
    }

    private static ParsedUnlinkedItemBatch ParseUnlinkedItems(IReadOnlyList<UnlinkedItemInfo> items)
    {
        var validItems = new List<ParsedUnlinkedItemInfo>(items.Count);
        var malformedItems = new List<UnlinkedItemInfo>();
        foreach (var item in items)
        {
            if (Guid.TryParse(item.Id, out var id))
            {
                validItems.Add(new ParsedUnlinkedItemInfo(item.Id, id, item.Type, item.Path));
                continue;
            }

            Log.Warning(
                "Removing cleanup candidate with malformed DavItem Id {Id} at {Path}",
                item.Id,
                item.Path);
            malformedItems.Add(item);
        }

        return new ParsedUnlinkedItemBatch(validItems, malformedItems);
    }

    private static Task DeleteDavItemsAsync
    (
        DavDatabaseContext dbContext,
        ParsedUnlinkedItemBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (dbContext.Database.IsNpgsql())
        {
            var ids = batch.ValidItems.Select(x => x.Id).ToArray();
            return dbContext.Items
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var parameters = batch.ValidItems
            .Select(x => x.RawId)
            .Concat(batch.MalformedItems.Select(x => x.Id))
            .Select(x => (object)x)
            .ToArray();
        var placeholders = string.Join(",", parameters.Select((_, index) => $"{{{index}}}"));
        var sql = $"""DELETE FROM "DavItems" WHERE "Id" IN ({placeholders})""";
        return dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    private static object CreateCreatedBeforeParameter(DavDatabaseContext dbContext, DateTime value)
    {
        var localWallTime = LocalWallQueryBounds.NormalizeExclusiveUpperBound(dbContext, value);
        if (!dbContext.Database.IsNpgsql()) return localWallTime;

        return new NpgsqlParameter("createdBefore", NpgsqlDbType.Timestamp)
        {
            Value = localWallTime
        };
    }

    private static IEnumerable<string> GetParentDirectories(IEnumerable<string> paths)
    {
        return paths
            .Select(Path.GetDirectoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!);
    }

    internal static (int? Current, int? Total) ToProgressValues(long current, long total)
    {
        if (current < 0 || total < 0 || current > int.MaxValue || total > int.MaxValue)
            return (null, null);

        return (checked((int)current), checked((int)total));
    }

    protected override void Report(string message, int? current = null, int? total = null)
    {
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;
        base.Report($"{dryRun}{message}", current, total);
    }

    public static string GetAuditReport()
    {
        var paths = GetAuditReportSnapshot();
        return paths.Count > 0
            ? string.Join("\n", paths)
            : "This list is Empty.\nYou must first run the task.";
    }

    private static void ClearAuditReport()
    {
        lock (AuditReportLock)
        {
            _allRemovedPaths.Clear();
        }
    }

    private static void AddAuditReportPaths(IEnumerable<string> paths)
    {
        lock (AuditReportLock)
        {
            _allRemovedPaths.AddRange(paths);
        }
    }

    private static void ReplaceAuditReport(IEnumerable<string> paths)
    {
        lock (AuditReportLock)
        {
            _allRemovedPaths = paths.ToList();
        }
    }

    private static List<string> GetAuditReportSnapshot()
    {
        lock (AuditReportLock)
        {
            return _allRemovedPaths.ToList();
        }
    }

    private static int GetAuditReportPathCount()
    {
        lock (AuditReportLock)
        {
            return _allRemovedPaths.Count;
        }
    }
}

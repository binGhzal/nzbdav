using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
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
    bool isDryRun
) : BaseTask(websocketManager, WebsocketTopic.CleanupTaskProgress)
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

    protected override async Task ExecuteInternal()
    {
        try
        {
            await RemoveUnlinkedFiles().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to remove unlinked files.");
        }
    }

    private async Task RemoveUnlinkedFiles()
    {
        ClearAuditReport();

        // get linked file paths
        Report("Scanning all linked files...");
        var startTime = DateTime.Now;
        var linkedIdCount = await WriteLinkedIdsToTable();
        if (linkedIdCount < 5)
        {
            Report($"Aborted: " +
                   $"There are less than five linked files found in your library. " +
                   $"Cancelling operation to prevent accidental bulk deletion.");
            return;
        }

        Report("Searching for unlinked webdav items...");
        var unlinkedItems = await CountUnlinkedItems(startTime);
        Report($"Found {unlinkedItems} webdav items to remove.");

        if (isDryRun)
        {
            await DryRunIdentifyUnlinkedFiles(startTime);
            Report($"Done. Identified {GetAuditReportPathCount()} unlinked files.");
        }
        else
        {
            await RemoveUnlinkedItems(startTime, unlinkedItems);
            await RemoveEmptyDirectories(startTime);
            Report($"Done. Removed {GetAuditReportPathCount()} unlinked files.");
        }
    }

    private async Task<int> WriteLinkedIdsToTable()
    {
        await using var dbContext = new DavDatabaseContext();

        // Create a new table "TMP_LINKED_FILES", dropping old one if it already exists.
        // No index initially for fast writes.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS TMP_LINKED_FILES;
            CREATE TABLE TMP_LINKED_FILES (Id TEXT NOT NULL);
            """);

        var count = 0;
        var batches = GetLinkedIds().ToBatches(100);
        foreach (var batch in batches)
        {
            var parameters = batch.Select(id => (object)id.ToString().ToUpperInvariant()).ToArray();
            var values = string.Join(",", parameters.Select((_, index) => $"({{{index}}})"));
            await dbContext.Database.ExecuteSqlAsync(
                FormattableStringFactory.Create(
                    "INSERT INTO TMP_LINKED_FILES (Id) VALUES " + values,
                    parameters));
            count += batch.Count;
        }

        // Remove duplicates and add primary key index.
        // Create a new table with unique constraint, copy distinct values, then swap.
        Report($"Indexing {count} linked files...");
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE TMP_LINKED_FILES_UNIQUE (Id TEXT NOT NULL PRIMARY KEY);
            INSERT OR IGNORE INTO TMP_LINKED_FILES_UNIQUE (Id) SELECT Id FROM TMP_LINKED_FILES;
            DROP TABLE TMP_LINKED_FILES;
            ALTER TABLE TMP_LINKED_FILES_UNIQUE RENAME TO TMP_LINKED_FILES;
            """);

        return count;
    }

    private IEnumerable<Guid> GetLinkedIds()
    {
        var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
        var linkedIds = OrganizedLinksUtil
            .GetLibraryDavItemLinks(configManager)
            .Select(x => x.DavItemId);

        var count = 0;
        foreach (var linkedId in linkedIds)
        {
            count++;
            debounce(() => Report($"Scanning all linked files...\nFound {count}..."));
            yield return linkedId;
        }

        Report($"Scanning all linked files...\nFound {count}...");
    }

    private async Task<int> CountUnlinkedItems(DateTime createdBefore)
    {
        await using var dbContext = new DavDatabaseContext();
        var createdBeforeStr = createdBefore.ToString("yyyy-MM-dd HH:mm:ss");
        var usenetFileType = (int)DavItem.ItemType.UsenetFile;

        var count = await dbContext.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(i.Id) AS Value FROM DavItems i
                LEFT JOIN TMP_LINKED_FILES t ON i.Id = t.Id
                WHERE i.Type = {0}
                  AND i.HistoryItemId IS NULL
                  AND i.CreatedAt < {1}
                  AND t.Id IS NULL
                """,
                usenetFileType,
                createdBeforeStr)
            .FirstAsync();

        return count;
    }

    private async Task RemoveUnlinkedItems(DateTime createdBefore, int totalCount)
    {
        Report("Removing unlinked items...");
        await using var dbContext = new DavDatabaseContext();
        var removed = 0;

        while (true)
        {
            // Select items to delete (batch of 100)
            var itemsToDelete = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    """
                    SELECT Id, Type, Path FROM DavItems
                    WHERE Type = {0}
                      AND HistoryItemId IS NULL
                      AND CreatedAt < {1}
                      AND Id NOT IN (SELECT Id FROM TMP_LINKED_FILES)
                    LIMIT 100
                    """,
                    (int)DavItem.ItemType.UsenetFile,
                    createdBefore.ToString("yyyy-MM-dd HH:mm:ss"))
                .ToListAsync();

            // If there are no more items to delete, we're done.
            if (itemsToDelete.Count == 0)
                break;

            var itemsToDeleteBatch = ParseUnlinkedItems(itemsToDelete);
            if (itemsToDeleteBatch.Count == 0)
            {
                Report($"Removing unlinked items...\nSkipped {itemsToDelete.Count} malformed item IDs.");
                break;
            }

            // Delete the items.
            await DeleteDavItemsAsync(dbContext, itemsToDeleteBatch);

            // Queue rclone vfs/forget for deleted items
            dbContext.EnqueueRcloneVfsForget(itemsToDeleteBatch.ValidItems.Select(x => new DavItem
            {
                Id = x.Id,
                Type = (DavItem.ItemType)x.Type,
                Path = x.Path
            }).ToList());
            dbContext.EnqueueRcloneVfsForgetPaths(GetParentDirectories(itemsToDeleteBatch.MalformedItems.Select(x => x.Path)));
            ContentIndexSnapshotWriterService.RequestSnapshot();
            await dbContext.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken).ConfigureAwait(false);

            // Track removed paths
            AddAuditReportPaths(itemsToDeleteBatch.Paths);
            removed += itemsToDeleteBatch.Count;

            Report($"Removing unlinked items...\nRemoved {removed}/{totalCount}...");
        }

        Report($"Removing unlinked items...\nRemoved {removed} of {removed}...");
    }

    private async Task RemoveEmptyDirectories(DateTime createdBefore)
    {
        Report($"Removing empty directories...");
        await using var dbContext = new DavDatabaseContext();
        var removed = 0;

        while (true)
        {
            // Find empty directories (no children).
            // Only target regular directories (SubType = Directory), not root folders.
            var emptyDirs = await dbContext.Database
                .SqlQueryRaw<UnlinkedItemInfo>(
                    """
                    SELECT d.Id, d.Type, d.Path FROM DavItems d
                    LEFT JOIN DavItems c ON c.ParentId = d.Id
                    WHERE d.SubType = {0}
                      AND d.CreatedAt < {1}
                      AND c.Id IS NULL
                    LIMIT 100
                    """,
                    (int)DavItem.ItemSubType.Directory,
                    createdBefore.ToString("yyyy-MM-dd HH:mm:ss"))
                .ToListAsync();

            if (emptyDirs.Count == 0)
                break;

            var emptyDirBatch = ParseUnlinkedItems(emptyDirs);
            if (emptyDirBatch.Count == 0)
            {
                Report($"Removing empty directories...\nSkipped {emptyDirs.Count} malformed item IDs.");
                break;
            }

            // Delete the empty directories.
            await DeleteDavItemsAsync(dbContext, emptyDirBatch);

            // Queue rclone vfs/forget for deleted directories
            dbContext.EnqueueRcloneVfsForget(emptyDirBatch.ValidItems.Select(x => new DavItem
            {
                Id = x.Id,
                Type = (DavItem.ItemType)x.Type,
                Path = x.Path
            }).ToList());
            dbContext.EnqueueRcloneVfsForgetPaths(GetParentDirectories(emptyDirBatch.MalformedItems.Select(x => x.Path)));
            ContentIndexSnapshotWriterService.RequestSnapshot();
            await dbContext.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
            await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken).ConfigureAwait(false);

            removed += emptyDirBatch.Count;
            Report($"Removing empty directories...\nRemoved {removed}...");
        }
    }

    private async Task DryRunIdentifyUnlinkedFiles(DateTime createdBefore)
    {
        await using var dbContext = new DavDatabaseContext();
        var unlinkedFiles = await dbContext.Database
            .SqlQueryRaw<UnlinkedItemInfo>(
                """
                SELECT Id, Type, Path FROM DavItems
                WHERE Type = {0}
                  AND HistoryItemId IS NULL
                  AND CreatedAt < {1}
                  AND Id NOT IN (SELECT Id FROM TMP_LINKED_FILES)
                """,
                (int)DavItem.ItemType.UsenetFile,
                createdBefore.ToString("yyyy-MM-dd HH:mm:ss"))
            .ToListAsync();

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
        ParsedUnlinkedItemBatch batch
    )
    {
        if (DavDatabaseContext.IsPostgres)
        {
            var ids = batch.ValidItems.Select(x => x.Id).ToArray();
            return dbContext.Items
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync();
        }

        var parameters = batch.ValidItems
            .Select(x => x.RawId)
            .Concat(batch.MalformedItems.Select(x => x.Id))
            .Select(x => (object)x)
            .ToArray();
        var placeholders = string.Join(",", parameters.Select((_, index) => $"{{{index}}}"));
        var sql = $"""DELETE FROM "DavItems" WHERE "Id" IN ({placeholders})""";
        return dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
    }

    private static IEnumerable<string> GetParentDirectories(IEnumerable<string> paths)
    {
        return paths
            .Select(Path.GetDirectoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!);
    }

    protected override void Report(string message)
    {
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;
        base.Report($"{dryRun}{message}");
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

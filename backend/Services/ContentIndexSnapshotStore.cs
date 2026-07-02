using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public static class ContentIndexSnapshotStore
{
    public const int CurrentVersion = 1;

    private const string SnapshotFileName = "content-index.snapshot.json";
    private const string BackupSnapshotFileName = "content-index.snapshot.backup.json";
    private static readonly SemaphoreSlim Mutex = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string SnapshotFilePath => Path.Join(DavDatabaseContext.ConfigPath, SnapshotFileName);
    public static string BackupSnapshotFilePath => Path.Join(DavDatabaseContext.ConfigPath, BackupSnapshotFileName);

    public static async Task WriteAsync(DavDatabaseContext dbContext, CancellationToken cancellationToken)
    {
        var snapshot = await CreateSnapshotAsync(dbContext, cancellationToken).ConfigureAwait(false);
        snapshot = PruneInvalidSnapshotEntries(snapshot, out var prunedEntryCount);
        if (prunedEntryCount > 0)
        {
            Log.Warning(
                "Pruned {Count} invalid /content rows while writing recovery snapshot.",
                prunedEntryCount);
        }

        if (!TryValidate(snapshot, out var validationError))
        {
            throw new InvalidOperationException(
                $"Refused to overwrite /content recovery snapshot with inconsistent state: {validationError}"
            );
        }

        await Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
            var tempFilePath = SnapshotFilePath + ".tmp";

            try
            {
                await using (var stream = File.Create(tempFilePath))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempFilePath, SnapshotFilePath, overwrite: true);
                File.Copy(SnapshotFilePath, BackupSnapshotFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
        }
        finally
        {
            Mutex.Release();
        }
    }

    public static async Task<SnapshotReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        await Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var warnings = new List<string>();
            var primaryResult = await TryReadPathAsync(SnapshotFilePath, cancellationToken).ConfigureAwait(false);
            if (primaryResult.Snapshot != null)
                return primaryResult;
            if (primaryResult.Warning != null)
                warnings.Add(primaryResult.Warning);

            var backupResult = await TryReadPathAsync(BackupSnapshotFilePath, cancellationToken).ConfigureAwait(false);
            if (backupResult.Snapshot != null)
            {
                warnings.AddRange(backupResult.Warnings);
                return new SnapshotReadResult
                {
                    Snapshot = backupResult.Snapshot,
                    SourcePath = backupResult.SourcePath,
                    Warnings = warnings,
                };
            }

            if (backupResult.Warning != null)
                warnings.Add(backupResult.Warning);

            return new SnapshotReadResult { Warnings = warnings };
        }
        finally
        {
            Mutex.Release();
        }
    }

    private static async Task<SnapshotReadResult> TryReadPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new SnapshotReadResult();

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer
                .DeserializeAsync<ContentIndexSnapshot>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (snapshot == null)
            {
                return new SnapshotReadResult
                {
                    Warning = $"Ignored /content recovery snapshot at '{path}' because it deserialized to null."
                };
            }

            if (!TryValidate(snapshot, out var validationError))
            {
                return new SnapshotReadResult
                {
                    Warning = $"Ignored /content recovery snapshot at '{path}': {validationError}"
                };
            }

            return new SnapshotReadResult
            {
                Snapshot = snapshot,
                SourcePath = path,
            };
        }
        catch (Exception ex)
        {
            return new SnapshotReadResult
            {
                Warning = $"Ignored /content recovery snapshot at '{path}': {ex.Message}"
            };
        }
    }

    private static bool TryValidate(ContentIndexSnapshot snapshot, out string error)
    {
        if (snapshot.Version != CurrentVersion)
        {
            error = $"unsupported version {snapshot.Version}";
            return false;
        }

        if (!TryBuildUniqueDictionary(snapshot.Items, x => x.Id, out var itemsById, out var duplicateItemId))
        {
            error = $"duplicate DavItem id {duplicateItemId}";
            return false;
        }

        foreach (var item in snapshot.Items)
        {
            if (!ContentPathUtil.IsContentChildPath(item.Path))
            {
                error = $"item '{item.Id}' has non-content path '{item.Path}'";
                return false;
            }

            if (item.ParentId != DavItem.ContentFolder.Id && !itemsById.ContainsKey(item.ParentId!.Value))
            {
                error = $"item '{item.Id}' references missing parent '{item.ParentId}'";
                return false;
            }
        }

        if (!TryBuildUniqueDictionary(snapshot.NzbFiles, x => x.Id, out var nzbFilesById, out _))
        {
            error = "duplicate DavNzbFile ids";
            return false;
        }

        if (!TryBuildUniqueDictionary(snapshot.RarFiles, x => x.Id, out var rarFilesById, out _))
        {
            error = "duplicate DavRarFile ids";
            return false;
        }

        if (!TryBuildUniqueDictionary(snapshot.MultipartFiles, x => x.Id, out var multipartFilesById, out _))
        {
            error = "duplicate DavMultipartFile ids";
            return false;
        }

        foreach (var item in snapshot.Items)
        {
            var hasRequiredMetadata = item.Type switch
            {
                DavItem.ItemType.Directory => true,
                DavItem.ItemType.UsenetFile => item.SubType switch
                {
                    DavItem.ItemSubType.NzbFile => nzbFilesById.ContainsKey(item.Id),
                    DavItem.ItemSubType.RarFile => rarFilesById.ContainsKey(item.Id),
                    DavItem.ItemSubType.MultipartFile => multipartFilesById.ContainsKey(item.Id),
                    _ => false
                },
                _ => false
            };

            if (!hasRequiredMetadata)
            {
                error = $"item '{item.Id}' is missing required metadata";
                return false;
            }
        }

        if (snapshot.NzbFiles.Any(x => !itemsById.TryGetValue(x.Id, out var item)
                                       || item.Type != DavItem.ItemType.UsenetFile
                                       || item.SubType != DavItem.ItemSubType.NzbFile))
        {
            error = "snapshot contains DavNzbFile rows without matching NzbFile items";
            return false;
        }

        if (snapshot.RarFiles.Any(x => !itemsById.TryGetValue(x.Id, out var item)
                                       || item.Type != DavItem.ItemType.UsenetFile
                                       || item.SubType != DavItem.ItemSubType.RarFile))
        {
            error = "snapshot contains DavRarFile rows without matching RarFile items";
            return false;
        }

        if (snapshot.MultipartFiles.Any(x => !itemsById.TryGetValue(x.Id, out var item)
                                             || item.Type != DavItem.ItemType.UsenetFile
                                             || item.SubType != DavItem.ItemSubType.MultipartFile))
        {
            error = "snapshot contains DavMultipartFile rows without matching MultipartFile items";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task<ContentIndexSnapshot> CreateSnapshotAsync
    (
        DavDatabaseContext dbContext,
        CancellationToken cancellationToken
    )
    {
        var items = await dbContext.Items
            .AsNoTracking()
            .Where(x => x.Path.StartsWith(ContentPathUtil.ForwardSlashPrefix) || x.Path.StartsWith(ContentPathUtil.BackslashPrefix))
            .OrderBy(x => x.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var itemIds = items.Select(x => x.Id).ToHashSet();

        var nzbFiles = await dbContext.NzbFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rarFiles = await dbContext.RarFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var multipartFiles = await dbContext.MultipartFiles
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ContentIndexSnapshot
        {
            Version = CurrentVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Items = items,
            NzbFiles = nzbFiles,
            RarFiles = rarFiles,
            MultipartFiles = multipartFiles,
        };
    }

    private static ContentIndexSnapshot PruneInvalidSnapshotEntries(ContentIndexSnapshot snapshot, out int prunedEntryCount)
    {
        var nzbFileIds = snapshot.NzbFiles.Select(x => x.Id).ToHashSet();
        var rarFileIds = snapshot.RarFiles.Select(x => x.Id).ToHashSet();
        var multipartFileIds = snapshot.MultipartFiles.Select(x => x.Id).ToHashSet();
        var eligibleItems = snapshot.Items
            .Where(item => HasRequiredMetadata(item, nzbFileIds, rarFileIds, multipartFileIds))
            .ToDictionary(x => x.Id);
        var reachableItemIds = new HashSet<Guid>();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var item in eligibleItems.Values)
            {
                if (reachableItemIds.Contains(item.Id)) continue;
                var parentIsReachable = item.ParentId == DavItem.ContentFolder.Id
                                        || item.ParentId != null
                                        && reachableItemIds.Contains(item.ParentId.Value);
                if (!parentIsReachable) continue;
                reachableItemIds.Add(item.Id);
                changed = true;
            }
        }

        var items = snapshot.Items
            .Where(x => reachableItemIds.Contains(x.Id))
            .ToList();
        var itemIds = items.Select(x => x.Id).ToHashSet();
        var nzbFiles = snapshot.NzbFiles.Where(x => itemIds.Contains(x.Id)).ToList();
        var rarFiles = snapshot.RarFiles.Where(x => itemIds.Contains(x.Id)).ToList();
        var multipartFiles = snapshot.MultipartFiles.Where(x => itemIds.Contains(x.Id)).ToList();

        prunedEntryCount =
            snapshot.Items.Count - items.Count
            + snapshot.NzbFiles.Count - nzbFiles.Count
            + snapshot.RarFiles.Count - rarFiles.Count
            + snapshot.MultipartFiles.Count - multipartFiles.Count;

        if (prunedEntryCount == 0) return snapshot;

        return new ContentIndexSnapshot
        {
            Version = snapshot.Version,
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Items = items,
            NzbFiles = nzbFiles,
            RarFiles = rarFiles,
            MultipartFiles = multipartFiles,
        };
    }

    private static bool HasRequiredMetadata
    (
        DavItem item,
        HashSet<Guid> nzbFileIds,
        HashSet<Guid> rarFileIds,
        HashSet<Guid> multipartFileIds
    )
    {
        return item.Type switch
        {
            DavItem.ItemType.Directory => true,
            DavItem.ItemType.UsenetFile => item.SubType switch
            {
                DavItem.ItemSubType.NzbFile => nzbFileIds.Contains(item.Id),
                DavItem.ItemSubType.RarFile => rarFileIds.Contains(item.Id),
                DavItem.ItemSubType.MultipartFile => multipartFileIds.Contains(item.Id),
                _ => false
            },
            _ => false
        };
    }

    private static bool TryBuildUniqueDictionary<TValue>
    (
        IEnumerable<TValue> values,
        Func<TValue, Guid> keySelector,
        out Dictionary<Guid, TValue> result,
        out Guid duplicateKey
    )
    {
        result = new Dictionary<Guid, TValue>();
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
            {
                duplicateKey = key;
                return false;
            }
        }

        duplicateKey = Guid.Empty;
        return true;
    }

    public sealed class SnapshotReadResult
    {
        public ContentIndexSnapshot? Snapshot { get; init; }
        public string? SourcePath { get; init; }
        public string? Warning { get; init; }
        public List<string> Warnings { get; init; } = [];
    }

    public sealed class ContentIndexSnapshot
    {
        public int Version { get; init; }
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public List<DavItem> Items { get; init; } = [];
        public List<DavNzbFile> NzbFiles { get; init; } = [];
        public List<DavRarFile> RarFiles { get; init; } = [];
        public List<DavMultipartFile> MultipartFiles { get; init; } = [];
    }
}

using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Mount;

public sealed class DfsDavPathResolver(DavDatabaseClient dbClient, ConfigManager configManager)
{
    private const string RcloneLinkSuffix = ".rclonelink";
    private const string HexAlphabet = "0123456789abcdef";
    private static readonly TimeSpan DeletedSymlinkTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> DeletedSymlinks = new(StringComparer.Ordinal);

    public async Task<DfsDavNode?> ResolveAsync(string path, CancellationToken ct = default)
    {
        var parts = SplitPath(path);
        if (parts.Length == 0) return FromDavDirectory(DavItem.Root, "/");

        if (IsIdsRoot(parts[0])) return await ResolveIdsAsync(parts, ct).ConfigureAwait(false);
        if (parts[0] == DavItem.SymlinkFolder.Name)
            return await ResolveSymlinkAsync(parts, ct).ConfigureAwait(false);

        return await ResolveDavTreeAsync(parts, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DfsDavNode>> ListAsync(string path, CancellationToken ct = default)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is null || !node.IsDirectory) return [];

        return node.Kind == DfsDavNodeKind.IdsDirectory
            ? await ListIdsAsync(node, ct).ConfigureAwait(false)
            : node.TargetDirectoryId.HasValue
                ? await ListSymlinkDirectoryAsync(node, ct).ConfigureAwait(false)
                : await ListDavDirectoryAsync(node, ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkCompletedSymlinkDeletedAsync(string path, CancellationToken ct = default)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not { Kind: DfsDavNodeKind.Symlink, Item: not null }) return false;

        var parentPath = Path.GetDirectoryName(NormalizePath(path)) ?? "/";
        var parent = await ResolveAsync(parentPath, ct).ConfigureAwait(false);
        if (parent?.TargetDirectoryId == null) return false;

        DeletedSymlinks[GetDeletedSymlinkKey(parent.TargetDirectoryId.Value, node.Name)] =
            DateTimeOffset.UtcNow + DeletedSymlinkTtl;
        return true;
    }

    private async Task<DfsDavNode?> ResolveDavTreeAsync(IReadOnlyList<string> parts, CancellationToken ct)
    {
        var current = await dbClient.GetDirectoryChildAsync(DavItem.Root.Id, parts[0], ct).ConfigureAwait(false);
        if (current == null) return null;

        for (var i = 1; i < parts.Count; i++)
        {
            if (current.Type != DavItem.ItemType.Directory) return null;
            current = await dbClient.GetDirectoryChildAsync(current.Id, parts[i], ct).ConfigureAwait(false);
            if (current == null) return null;
        }

        return FromDavItem(current);
    }

    private async Task<DfsDavNode?> ResolveSymlinkAsync(IReadOnlyList<string> parts, CancellationToken ct)
    {
        if (parts.Count == 1)
            return FromSymlinkDirectory(DavItem.SymlinkFolder, DavItem.ContentFolder.Id, "/" + DavItem.SymlinkFolder.Name);

        var current = await ResolveSymlinkChildAsync(DavItem.SymlinkFolder, DavItem.ContentFolder.Id, parts[1], ct)
            .ConfigureAwait(false);
        if (current == null) return null;

        for (var i = 2; i < parts.Count; i++)
        {
            if (current.Kind != DfsDavNodeKind.Directory || current.Item == null || current.TargetDirectoryId == null)
                return null;

            current = await ResolveSymlinkChildAsync(current.Item, current.TargetDirectoryId.Value, parts[i], ct)
                .ConfigureAwait(false);
            if (current == null) return null;
        }

        return current;
    }

    private async Task<DfsDavNode?> ResolveSymlinkChildAsync
    (
        DavItem visibleDirectory,
        Guid targetDirectoryId,
        string requestedName,
        CancellationToken ct
    )
    {
        var childName = StripRcloneLinkSuffix(requestedName);
        if (IsDeletedSymlink(targetDirectoryId, childName) || IsDeletedSymlink(targetDirectoryId, requestedName))
            return null;

        var child = await dbClient.GetDirectoryChildAsync(targetDirectoryId, childName, ct).ConfigureAwait(false);
        if (child == null && visibleDirectory.Id == DavItem.SymlinkFolder.Id
                          && configManager.GetApiCategories().Contains(childName))
        {
            return FromSymlinkDirectory(
                CreateVirtualCategory(childName),
                targetDirectoryId,
                "/" + Path.Join(DavItem.SymlinkFolder.Name, childName));
        }

        if (child == null) return null;
        return child.Type == DavItem.ItemType.Directory
            ? FromSymlinkDirectory(child, child.Id, "/" + Path.Join(DavItem.SymlinkFolder.Name, child.Path["/content/".Length..]))
            : FromSymlink(child);
    }

    private async Task<DfsDavNode?> ResolveIdsAsync(IReadOnlyList<string> parts, CancellationToken ct)
    {
        if (parts.Count == 1)
            return FromIdsDirectory(DavItem.IdsFolder.Name, "", "/" + DavItem.IdsFolder.Name);

        var prefixLength = Math.Min(parts.Count - 1, DavItem.IdPrefixLength);
        for (var i = 1; i <= prefixLength; i++)
        {
            if (parts[i].Length != 1 || !HexAlphabet.Contains(parts[i][0])) return null;
        }

        if (parts.Count <= DavItem.IdPrefixLength + 1)
        {
            var prefix = string.Join("", parts.Skip(1));
            return FromIdsDirectory(parts[^1], prefix, "/" + string.Join('/', parts));
        }

        if (parts.Count != DavItem.IdPrefixLength + 2) return null;
        if (!Guid.TryParse(parts[^1], out _)) return null;

        var item = await dbClient.GetFileById(parts[^1]).ConfigureAwait(false);
        return item == null ? null : FromDavFile(item, "/" + string.Join('/', parts));
    }

    private async Task<IReadOnlyList<DfsDavNode>> ListDavDirectoryAsync(DfsDavNode directory, CancellationToken ct)
    {
        if (directory.Item == null) return [];
        var children = await dbClient.GetDirectoryChildrenAsync(directory.Item.Id, ct).ConfigureAwait(false);
        var result = children.Select(FromDavItem).ToList();

        if (directory.Item.Id == DavItem.ContentFolder.Id)
        {
            result.AddRange(configManager.GetApiCategories()
                .Except(children.Select(x => x.Name))
                .Select(category => FromDavDirectory(CreateVirtualCategory(category), "/" + Path.Join(directory.Item.Path, category))));
        }

        return result;
    }

    private async Task<IReadOnlyList<DfsDavNode>> ListSymlinkDirectoryAsync(DfsDavNode directory, CancellationToken ct)
    {
        if (directory.Item == null || directory.TargetDirectoryId == null) return [];

        var children = directory.Item.ParentId == DavItem.ContentFolder.Id
            ? await dbClient.GetCompletedSymlinkCategoryChildren(directory.Item.Name, ct).ConfigureAwait(false)
            : await dbClient.GetDirectoryChildrenAsync(directory.TargetDirectoryId.Value, ct).ConfigureAwait(false);

        var result = children
            .Select(child => child.Type == DavItem.ItemType.Directory
                ? FromSymlinkDirectory(child, child.Id, child.Path.Replace("/content", "/" + DavItem.SymlinkFolder.Name))
                : FromSymlink(child))
            .Where(child => !IsDeletedSymlink(directory.TargetDirectoryId.Value, child.Name))
            .ToList();

        if (directory.Item.Id == DavItem.SymlinkFolder.Id)
        {
            result.AddRange(configManager.GetApiCategories()
                .Except(children.Select(x => x.Name))
                .Select(category => FromSymlinkDirectory(
                    CreateVirtualCategory(category),
                    DavItem.ContentFolder.Id,
                    "/" + Path.Join(DavItem.SymlinkFolder.Name, category))));
        }

        return result;
    }

    private async Task<IReadOnlyList<DfsDavNode>> ListIdsAsync(DfsDavNode directory, CancellationToken ct)
    {
        var prefix = directory.IdPrefix ?? "";
        if (prefix.Length < DavItem.IdPrefixLength)
        {
            return HexAlphabet
                .Select(x => FromIdsDirectory(x.ToString(), prefix + x, Path.Join(directory.Path, x.ToString())))
                .ToArray();
        }

        return (await dbClient.GetFilesByIdPrefix(prefix).ConfigureAwait(false))
            .Select(item => FromDavFile(item, Path.Join(directory.Path, item.Id.ToString())))
            .ToArray();
    }

    private DfsDavNode FromDavItem(DavItem item)
    {
        return item.Type == DavItem.ItemType.Directory ? FromDavDirectory(item, item.Path) : FromDavFile(item, item.Path);
    }

    private static DfsDavNode FromDavDirectory(DavItem item, string path)
    {
        return new DfsDavNode
        {
            Kind = DfsDavNodeKind.Directory,
            Name = item.Name,
            Path = NormalizePath(path),
            Item = item,
            CreatedAt = item.CreatedAt,
            Size = 0
        };
    }

    private static DfsDavNode FromDavFile(DavItem item, string path)
    {
        return new DfsDavNode
        {
            Kind = DfsDavNodeKind.File,
            Name = item.Name,
            Path = NormalizePath(path),
            Item = item,
            CreatedAt = item.CreatedAt,
            Size = item.FileSize ?? 0
        };
    }

    private DfsDavNode FromSymlink(DavItem item)
    {
        return new DfsDavNode
        {
            Kind = DfsDavNodeKind.Symlink,
            Name = item.Name,
            Path = NormalizePath(item.Path.Replace("/content", "/" + DavItem.SymlinkFolder.Name)),
            Item = item,
            CreatedAt = item.CreatedAt,
            Size = item.FileSize ?? 0,
            SymlinkTarget = DatabaseStoreSymlinkFile.GetTargetPath(item.Id, configManager.GetMountDir())
        };
    }

    private static DfsDavNode FromSymlinkDirectory(DavItem item, Guid targetDirectoryId, string path)
    {
        return new DfsDavNode
        {
            Kind = DfsDavNodeKind.Directory,
            Name = item.Name,
            Path = NormalizePath(path),
            Item = item,
            TargetDirectoryId = targetDirectoryId,
            CreatedAt = item.CreatedAt,
            Size = 0
        };
    }

    private static DfsDavNode FromIdsDirectory(string name, string prefix, string path)
    {
        return new DfsDavNode
        {
            Kind = DfsDavNodeKind.IdsDirectory,
            Name = name,
            Path = NormalizePath(path),
            IdPrefix = prefix,
            Size = 0
        };
    }

    private static DavItem CreateVirtualCategory(string category)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.GetFiveLengthPrefix(),
            ParentId = DavItem.ContentFolder.Id,
            Name = category,
            FileSize = null,
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = Path.Join(DavItem.ContentFolder.Path, category),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string[] SplitPath(string path)
    {
        return NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static bool IsIdsRoot(string name)
    {
        return name == DavItem.IdsFolder.Name || name == "ids";
    }

    private static string StripRcloneLinkSuffix(string name)
    {
        return name.EndsWith(RcloneLinkSuffix, StringComparison.Ordinal)
            ? name[..^RcloneLinkSuffix.Length]
            : name;
    }

    private static bool IsDeletedSymlink(Guid directoryId, string name)
    {
        var key = GetDeletedSymlinkKey(directoryId, name);
        if (!DeletedSymlinks.TryGetValue(key, out var expiry)) return false;
        if (expiry > DateTimeOffset.UtcNow) return true;
        DeletedSymlinks.TryRemove(key, out _);
        return false;
    }

    private static string GetDeletedSymlinkKey(Guid directoryId, string name)
    {
        return $"{directoryId:N}/{StripRcloneLinkSuffix(name)}";
    }
}

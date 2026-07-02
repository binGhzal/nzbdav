using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Mount;

public sealed record DfsDavNode
{
    public required DfsDavNodeKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DavItem? Item { get; init; }
    public Guid? TargetDirectoryId { get; init; }
    public string? IdPrefix { get; init; }
    public string? SymlinkTarget { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public long Size { get; init; }

    public bool IsDirectory => Kind is DfsDavNodeKind.Directory or DfsDavNodeKind.IdsDirectory;
    public bool IsFile => Kind is DfsDavNodeKind.File;
    public bool IsSymlink => Kind is DfsDavNodeKind.Symlink;
}

public enum DfsDavNodeKind
{
    Directory,
    File,
    Symlink,
    IdsDirectory
}

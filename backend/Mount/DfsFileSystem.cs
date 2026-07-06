using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Mono.Fuse.NETStandard;
using Mono.Unix.Native;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Streams.Caching;
using Serilog;

namespace NzbWebDAV.Mount;

public sealed class DfsFileSystem : FileSystem
{
    private readonly string mountPoint;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ConfigManager configManager;
    private readonly UsenetStreamingClient usenetClient;
    private readonly StreamingConnectionLimiter streamingConnectionLimiter;
    private readonly ActiveStreamTracker activeStreamTracker;
    private readonly MountStatusProvider mountStatus;

    public DfsFileSystem(
        string mountPoint,
        IServiceScopeFactory scopeFactory,
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        StreamingConnectionLimiter streamingConnectionLimiter,
        ActiveStreamTracker activeStreamTracker,
        MountStatusProvider mountStatus
    ) : base(mountPoint)
    {
        this.mountPoint = mountPoint;
        this.scopeFactory = scopeFactory;
        this.configManager = configManager;
        this.usenetClient = usenetClient;
        this.streamingConnectionLimiter = streamingConnectionLimiter;
        this.activeStreamTracker = activeStreamTracker;
        this.mountStatus = mountStatus;
    }

    public void MarkReady()
    {
        mountStatus.SetReady(mountPoint);
    }

    protected override void OnInit(ConnectionInformation connection)
    {
        MarkReady();
    }

    protected override Errno OnGetPathStatus(string path, out Stat stat)
    {
        using var operation = mountStatus.TrackOperation();
        try
        {
            var node = Resolve(path);
            if (node == null)
            {
                stat = default;
                return Errno.ENOENT;
            }

            stat = CreateStat(node);
            return 0;
        }
        catch (Exception ex)
        {
            stat = default;
            mountStatus.RecordFuseError($"DFS getattr failed for {path}: {ex.Message}");
            Log.Warning(ex, "DFS getattr failed for {Path}", path);
            return Errno.EIO;
        }
    }

    protected override Errno OnReadDirectory(string directory, OpenedPathInfo info, out IEnumerable<DirectoryEntry> paths)
    {
        using var operation = mountStatus.TrackOperation();
        try
        {
            var children = List(directory);
            paths = children
                .Prepend(new DfsDavNode { Kind = DfsDavNodeKind.Directory, Name = "..", Path = "/" })
                .Prepend(new DfsDavNode { Kind = DfsDavNodeKind.Directory, Name = ".", Path = "/" })
                .Select(ToDirectoryEntry)
                .ToArray();
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            paths = [];
            return Errno.ENOENT;
        }
        catch (InvalidOperationException)
        {
            paths = [];
            return Errno.ENOTDIR;
        }
        catch (Exception ex)
        {
            paths = [];
            mountStatus.RecordFuseError($"DFS readdir failed for {directory}: {ex.Message}");
            Log.Warning(ex, "DFS readdir failed for {Path}", directory);
            return Errno.EIO;
        }
    }

    protected override Errno OnReadSymbolicLink(string link, out string target)
    {
        using var operation = mountStatus.TrackOperation();
        try
        {
            var node = Resolve(link);
            if (node is not { Kind: DfsDavNodeKind.Symlink, SymlinkTarget: not null })
            {
                target = "";
                return Errno.ENOENT;
            }

            target = node.SymlinkTarget;
            return 0;
        }
        catch (Exception ex)
        {
            target = "";
            mountStatus.RecordFuseError($"DFS readlink failed for {link}: {ex.Message}");
            Log.Warning(ex, "DFS readlink failed for {Path}", link);
            return Errno.EIO;
        }
    }

    protected override Errno OnOpenHandle(string file, OpenedPathInfo info)
    {
        using var operation = mountStatus.TrackOperation();
        if (info.OpenAccess != OpenFlags.O_RDONLY) return Errno.EACCES;

        try
        {
            var node = Resolve(file);
            if (node == null) return Errno.ENOENT;
            if (node.IsDirectory) return Errno.EISDIR;
            if (node.IsSymlink) return Errno.ELOOP;
            if (node.Item == null) return Errno.ENOENT;

            var handle = OpenHandle(file, node.Item);
            var gcHandle = GCHandle.Alloc(handle);
            info.Handle = GCHandle.ToIntPtr(gcHandle);
            info.DirectIO = false;
            info.KeepCache = false;
            return 0;
        }
        catch (FileNotFoundException)
        {
            return Errno.ENOENT;
        }
        catch (Exception ex)
        {
            mountStatus.RecordFuseError($"DFS open failed for {file}: {ex.Message}");
            Log.Warning(ex, "DFS open failed for {Path}", file);
            return Errno.EIO;
        }
    }

    protected override Errno OnReadHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesWritten)
    {
        using var operation = mountStatus.TrackOperation();
        bytesWritten = 0;
        try
        {
            var handle = GetHandle(info);
            bytesWritten = handle.Reader
                .ReadAtAsync(offset, buf.AsMemory(0, buf.Length), handle.CancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            mountStatus.RecordFuseError($"DFS read failed for {file}: {ex.Message}");
            Log.Warning(ex, "DFS read failed for {Path}", file);
            return Errno.EIO;
        }
    }

    protected override Errno OnReleaseHandle(string file, OpenedPathInfo info)
    {
        using var operation = mountStatus.TrackOperation();
        try
        {
            if (info.Handle == IntPtr.Zero) return 0;
            var gcHandle = GCHandle.FromIntPtr(info.Handle);
            if (gcHandle.Target is DfsOpenFileHandle handle)
                handle.Dispose();
            gcHandle.Free();
            info.Handle = IntPtr.Zero;
            return 0;
        }
        catch (Exception ex)
        {
            mountStatus.RecordFuseError($"DFS release failed for {file}: {ex.Message}");
            Log.Warning(ex, "DFS release failed for {Path}", file);
            return Errno.EIO;
        }
    }

    protected override Errno OnRemoveFile(string file)
    {
        using var operation = mountStatus.TrackOperation();
        try
        {
            if (!MarkCompletedSymlinkDeleted(file)) return Errno.EROFS;
            mountStatus.RecordInvalidation();
            return 0;
        }
        catch (Exception ex)
        {
            mountStatus.RecordFuseError($"DFS unlink failed for {file}: {ex.Message}");
            Log.Warning(ex, "DFS unlink failed for {Path}", file);
            return Errno.EIO;
        }
    }

    private DfsDavNode? Resolve(string path)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var resolver = new DfsDavPathResolver(new DavDatabaseClient(dbContext), configManager);
        return resolver.ResolveAsync(path).GetAwaiter().GetResult();
    }

    private IReadOnlyList<DfsDavNode> List(string path)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var resolver = new DfsDavPathResolver(new DavDatabaseClient(dbContext), configManager);
        var node = resolver.ResolveAsync(path).GetAwaiter().GetResult();
        if (node == null) throw new DirectoryNotFoundException(path);
        if (!node.IsDirectory) throw new InvalidOperationException($"{path} is not a directory.");
        return resolver.ListAsync(path).GetAwaiter().GetResult();
    }

    private bool MarkCompletedSymlinkDeleted(string path)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var resolver = new DfsDavPathResolver(new DavDatabaseClient(dbContext), configManager);
        return resolver.MarkCompletedSymlinkDeletedAsync(path).GetAwaiter().GetResult();
    }

    private DfsOpenFileHandle OpenHandle(string path, DavItem item)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var dbClient = new DavDatabaseClient(dbContext);
        var reader = CreateRangeReader(dbClient, item);
        var connectionLimiter = new SemaphoreSlim(configManager.GetAdaptiveMaxStreamingConnections());
        var cts = new CancellationTokenSource();
        var priorityContext = new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
            ConnectionLimiters =
            [
                new SemaphoreSlimConnectionLimiter(connectionLimiter),
                streamingConnectionLimiter
            ]
        };
        var priorityScope = cts.Token.SetContext(priorityContext);
        var timeoutScope = cts.Token.SetContext(new StreamingTimeoutContext
        {
            PerAttemptTimeout = configManager.GetStreamingSegmentTimeout(),
            MaxRetries = configManager.GetStreamingSegmentRetries()
        });
        var activeStreamLease = activeStreamTracker.Open($"dfs:{path}", "fuse");

        return new DfsOpenFileHandle(
            reader,
            cts,
            priorityScope,
            timeoutScope,
            connectionLimiter,
            activeStreamLease);
    }

    private IFileRangeReader CreateRangeReader(DavDatabaseClient dbClient, DavItem item)
    {
        return item.SubType switch
        {
            DavItem.ItemSubType.NzbFile => CreateNzbRangeReader(dbClient, item),
            DavItem.ItemSubType.RarFile => CreateRarRangeReader(dbClient, item),
            DavItem.ItemSubType.MultipartFile => CreateMultipartRangeReader(dbClient, item),
            _ => throw new FileNotFoundException($"Unsupported DFS file type {item.SubType} for {item.Id}")
        };
    }

    private IFileRangeReader CreateNzbRangeReader(DavDatabaseClient dbClient, DavItem item)
    {
        var nzbFile = dbClient.GetDavNzbFileAsync(item).GetAwaiter().GetResult()
                      ?? throw new FileNotFoundException($"Could not find nzb file with id: {item.Id}");
        return CreateSegmentRangeReader(nzbFile.SegmentIds, item.FileSize ?? 0);
    }

    private IFileRangeReader CreateRarRangeReader(DavDatabaseClient dbClient, DavItem item)
    {
        var rarFile = dbClient.GetDavRarFileAsync(item).GetAwaiter().GetResult()
                      ?? throw new FileNotFoundException($"Could not find rar file with id: {item.Id}");
        return new DavMultipartFileRangeReader(
            rarFile.ToDavMultipartFileMeta().FileParts,
            usenetClient,
            configManager.GetAdaptiveArticleBufferSize(),
            cacheOptions: configManager.GetSparseSegmentCacheOptions());
    }

    private IFileRangeReader CreateMultipartRangeReader(DavDatabaseClient dbClient, DavItem item)
    {
        var multipartFile = dbClient.GetDavMultipartFileAsync(item).GetAwaiter().GetResult()
                            ?? throw new FileNotFoundException($"Could not find multipart file with id: {item.Id}");
        if (multipartFile.Metadata.AesParams == null)
        {
            return new DavMultipartFileRangeReader(
                multipartFile.Metadata.FileParts,
                usenetClient,
                configManager.GetAdaptiveArticleBufferSize(),
                cacheOptions: configManager.GetSparseSegmentCacheOptions());
        }

        var packedStream = new DavMultipartFileStream(
            multipartFile.Metadata.FileParts,
            usenetClient,
            configManager.GetAdaptiveArticleBufferSize(),
            cacheOptions: configManager.GetSparseSegmentCacheOptions());
        return new StreamFileRangeReader(new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams));
    }

    private IFileRangeReader CreateSegmentRangeReader(string[] segmentIds, long length)
    {
        var inner = new SegmentFileRangeReader(
            segmentIds,
            length,
            usenetClient,
            configManager.GetAdaptiveArticleBufferSize());
        var options = configManager.GetSparseSegmentCacheOptions();
        if (!options.Enabled) return inner;
        return SparseSegmentCacheManager.Shared.Open(
            SparseSegmentCacheManager.CreateKey(segmentIds, length),
            inner,
            options);
    }

    private static DfsOpenFileHandle GetHandle(OpenedPathInfo info)
    {
        if (info.Handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(DfsOpenFileHandle));
        var gcHandle = GCHandle.FromIntPtr(info.Handle);
        return gcHandle.Target as DfsOpenFileHandle
               ?? throw new ObjectDisposedException(nameof(DfsOpenFileHandle));
    }

    private static DirectoryEntry ToDirectoryEntry(DfsDavNode node)
    {
        return new DirectoryEntry(node.Name)
        {
            Stat = CreateStat(node)
        };
    }

    private static Stat CreateStat(DfsDavNode node)
    {
        var mode = node.Kind switch
        {
            DfsDavNodeKind.Directory or DfsDavNodeKind.IdsDirectory =>
                FilePermissions.S_IFDIR | FilePermissions.S_IRUSR | FilePermissions.S_IWUSR | FilePermissions.S_IXUSR
                | FilePermissions.S_IRGRP | FilePermissions.S_IXGRP | FilePermissions.S_IROTH | FilePermissions.S_IXOTH,
            DfsDavNodeKind.Symlink =>
                FilePermissions.S_IFLNK | FilePermissions.S_IRUSR | FilePermissions.S_IWUSR | FilePermissions.S_IXUSR
                | FilePermissions.S_IRGRP | FilePermissions.S_IWGRP | FilePermissions.S_IXGRP
                | FilePermissions.S_IROTH | FilePermissions.S_IWOTH | FilePermissions.S_IXOTH,
            _ =>
                FilePermissions.S_IFREG | FilePermissions.S_IRUSR | FilePermissions.S_IWUSR
                | FilePermissions.S_IRGRP | FilePermissions.S_IROTH
        };
        var time = ToUnixTimeSeconds(node.CreatedAt);
        return new Stat
        {
            st_ino = GetInode(node),
            st_mode = mode,
            st_nlink = node.IsDirectory ? 2UL : 1UL,
            st_size = node.IsDirectory ? 0 : node.Size,
            st_blksize = 4096,
            st_blocks = Math.Max(1, (node.Size + 511) / 512),
            st_atime = time,
            st_mtime = time,
            st_ctime = time
        };
    }

    private static long ToUnixTimeSeconds(DateTime value)
    {
        if (value <= DateTime.UnixEpoch) return 0;

        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    private static ulong GetInode(DfsDavNode node)
    {
        var hash = HashCode.Combine(node.Path, node.Name, node.Kind);
        return (ulong)Math.Abs(hash == int.MinValue ? 1 : hash);
    }

    private sealed class DfsOpenFileHandle(
        IFileRangeReader reader,
        CancellationTokenSource cancellationTokenSource,
        IDisposable priorityContext,
        IDisposable timeoutContext,
        SemaphoreSlim connectionLimiter,
        ActiveStreamTracker.ActiveStreamLease activeStreamLease
    ) : IDisposable
    {
        private int _disposed;

        public IFileRangeReader Reader => reader;
        public CancellationToken CancellationToken => cancellationTokenSource.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            cancellationTokenSource.Cancel();
            if (reader is IDisposable disposable) disposable.Dispose();
            priorityContext.Dispose();
            timeoutContext.Dispose();
            connectionLimiter.Dispose();
            activeStreamLease.Dispose();
            cancellationTokenSource.Dispose();
        }
    }
}

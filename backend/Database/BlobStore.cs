using MemoryPack;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace NzbWebDAV.Database;

public class BlobStore
{
    private static readonly int CompressionLevel = 1;
    private static readonly string ConfigPath = DavDatabaseContext.ConfigPath;
    private static readonly Lock LockObj = new();

    public enum BlobReadStatus
    {
        Found,
        Missing,
        TemporarilyUnavailable,
        Unreadable
    }

    public sealed record BlobReadResult<T>(T? Value, BlobReadStatus Status, string? Error);

    private static string GetBlobPath(Guid id)
    {
        var guidStr = id.ToString("N"); // Without hyphens
        var firstTwo = guidStr[..2];
        var nextTwo = guidStr.Substring(2, 2);
        var fileName = id.ToString(); // With hyphens for readability

        return Path.Combine(ConfigPath, "blobs", firstTwo, nextTwo, fileName);
    }

    private static string GetBlobTempPath(string blobPath)
    {
        return $"{blobPath}.tmp-{Guid.NewGuid():N}";
    }

    private static FileStream OpenBlobWrite(
        string blobPath,
        out string tempPath,
        out IReadOnlyList<string> directoriesToFlush)
    {
        var directory = Path.GetDirectoryName(blobPath);

        // Acquire file handle inside lock to prevent race condition where
        // directory gets deleted between CreateDirectory and FileStream open.
        FileStream fileStream;
        lock (LockObj)
        {
            directoriesToFlush = GetDirectoriesToFlushAfterRename(directory!);
            Directory.CreateDirectory(directory!);
            tempPath = GetBlobTempPath(blobPath);
            fileStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        return fileStream;
    }

    public static async Task WriteBlob(Guid id, Stream stream)
    {
        await WriteBlobCore(id, fileStream => stream.CopyToAsync(fileStream));
    }

    public static async Task WriteBlob<T>(Guid id, T blob)
    {
        await WriteBlobCore(id, async fileStream =>
        {
            await using var compressionStream = new CompressionStream(fileStream, CompressionLevel, leaveOpen: true);
            await MemoryPackSerializer.SerializeAsync(compressionStream, blob);
        });
    }

    private static async Task WriteBlobCore(Guid id, Func<FileStream, Task> writeAsync)
    {
        var blobPath = GetBlobPath(id);
        var tempPath = "";
        IReadOnlyList<string> directoriesToFlush = [];
        try
        {
            await using (var fileStream = OpenBlobWrite(
                             blobPath,
                             out tempPath,
                             out directoriesToFlush))
            {
                await writeAsync(fileStream);
                await fileStream.FlushAsync().ConfigureAwait(false);
                fileStream.Flush(flushToDisk: true);
            }

            lock (LockObj)
            {
                if (Directory.Exists(blobPath))
                    TryDeleteBlobPathDirectory(blobPath);
                File.Move(tempPath, blobPath, overwrite: true);
                foreach (var directory in directoriesToFlush)
                    FlushDirectoryToDisk(directory);
            }
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static IReadOnlyList<string> GetDirectoriesToFlushAfterRename(string leafDirectory)
    {
        var blobRoot = Path.Combine(ConfigPath, "blobs");
        var firstShard = Path.GetDirectoryName(leafDirectory)!;
        var candidates = new[] { ConfigPath, blobRoot, firstShard, leafDirectory };
        var existed = candidates.ToDictionary(
            path => path,
            Directory.Exists,
            StringComparer.Ordinal);
        var result = new List<string> { leafDirectory };

        foreach (var path in candidates.Reverse())
        {
            if (existed[path]) continue;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) result.Add(parent);
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void FlushDirectoryToDisk(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(
                directory,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
                ThrowDirectoryFlushError(directory, Marshal.GetLastPInvokeError());
            if (!FlushFileBuffers(handle))
                ThrowDirectoryFlushError(directory, Marshal.GetLastPInvokeError());
            return;
        }

        var fileDescriptor = OpenUnix(directory, 0);
        if (fileDescriptor < 0)
            ThrowDirectoryFlushError(directory, Marshal.GetLastPInvokeError());
        try
        {
            if (FsyncUnix(fileDescriptor) != 0)
                ThrowDirectoryFlushError(directory, Marshal.GetLastPInvokeError());
        }
        finally
        {
            CloseUnix(fileDescriptor);
        }
    }

    private static void ThrowDirectoryFlushError(string directory, int error)
    {
        throw new IOException(
            $"Could not flush blob directory '{directory}' to disk: {new Win32Exception(error).Message}");
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FsyncUnix(int fileDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseUnix(int fileDescriptor);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    public static Stream? ReadBlob(Guid id)
    {
        var result = TryOpenReadBlob(id);
        return result.Status == BlobReadStatus.Found ? result.Value : null;
    }

    public static BlobReadResult<Stream> TryOpenReadBlob(Guid id)
    {
        var blobPath = GetBlobPath(id);
        try
        {
            return new BlobReadResult<Stream>(
                File.Open(blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                BlobReadStatus.Found,
                Error: null);
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            return new BlobReadResult<Stream>(default, BlobReadStatus.Missing, e.Message);
        }
        catch (Exception e) when (IsDirectoryAtPath(e, blobPath))
        {
            TryDeleteBlobPathDirectory(blobPath);
            return new BlobReadResult<Stream>(default, BlobReadStatus.Unreadable, e.Message);
        }
        catch (Exception e) when (IsBlobOpenFailure(e))
        {
            return new BlobReadResult<Stream>(default, BlobReadStatus.TemporarilyUnavailable, e.Message);
        }
    }

    public static BlobReadResult<FileInfo> TryStatBlob(Guid id)
    {
        var blobPath = GetBlobPath(id);
        try
        {
            var fileInfo = new FileInfo(blobPath);
            return fileInfo.Exists
                ? new BlobReadResult<FileInfo>(fileInfo, BlobReadStatus.Found, Error: null)
                : new BlobReadResult<FileInfo>(default, BlobReadStatus.Missing, "Blob file does not exist.");
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            return new BlobReadResult<FileInfo>(default, BlobReadStatus.Missing, e.Message);
        }
        catch (Exception e) when (IsDirectoryAtPath(e, blobPath))
        {
            TryDeleteBlobPathDirectory(blobPath);
            return new BlobReadResult<FileInfo>(default, BlobReadStatus.Unreadable, e.Message);
        }
        catch (Exception e) when (IsBlobOpenFailure(e))
        {
            return new BlobReadResult<FileInfo>(default, BlobReadStatus.TemporarilyUnavailable, e.Message);
        }
    }

    public static async Task<T?> ReadBlob<T>(Guid id)
    {
        var result = await TryReadBlob<T>(id).ConfigureAwait(false);
        return result.Status == BlobReadStatus.Found ? result.Value : default;
    }

    public static async Task<BlobReadResult<T>> TryReadBlob<T>(Guid id)
    {
        var blobPath = GetBlobPath(id);
        Stream stream;
        try
        {
            stream = File.Open(blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            return new BlobReadResult<T>(default, BlobReadStatus.Missing, e.Message);
        }
        catch (Exception e) when (IsDirectoryAtPath(e, blobPath))
        {
            TryDeleteBlobPathDirectory(blobPath);
            return new BlobReadResult<T>(default, BlobReadStatus.Unreadable, e.Message);
        }
        catch (Exception e) when (IsBlobOpenFailure(e))
        {
            return new BlobReadResult<T>(default, BlobReadStatus.TemporarilyUnavailable, e.Message);
        }

        try
        {
            await using var fileStream = stream;
            await using var decompressionStream = new DecompressionStream(fileStream);
            var value = await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream)
                .ConfigureAwait(false);
            return value is null
                ? new BlobReadResult<T>(default, BlobReadStatus.Unreadable, "Blob deserialized to null.")
                : new BlobReadResult<T>(value, BlobReadStatus.Found, null);
        }
        catch (Exception e) when (IsTemporarySerializedBlobReadFailure(e))
        {
            return new BlobReadResult<T>(default, BlobReadStatus.TemporarilyUnavailable, e.Message);
        }
        catch (Exception e) when (IsUnreadableSerializedBlob(e))
        {
            return new BlobReadResult<T>(default, BlobReadStatus.Unreadable, e.Message);
        }
    }

    public static void Delete(Guid id)
    {
        var blobPath = GetBlobPath(id);

        TryDeleteFile(blobPath);

        lock (LockObj)
        {
            DeleteBlobTempFiles(blobPath);

            // Clean up empty directories
            // Structure: CONFIG_PATH/blobs/{firstTwo}/{nextTwo}/{fileName}
            var nextTwoDir = Path.GetDirectoryName(blobPath);
            var firstTwoDir = Path.GetDirectoryName(nextTwoDir);

            TryDeleteEmptyDirectory(nextTwoDir);
            TryDeleteEmptyDirectory(firstTwoDir);
        }
    }

    private static void DeleteBlobTempFiles(string blobPath)
    {
        var directory = Path.GetDirectoryName(blobPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        try
        {
            var pattern = $"{Path.GetFileName(blobPath)}.tmp-*";
            foreach (var tempPath in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                TryDeleteFile(tempPath);
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            // Another cleanup pass may have removed the shard concurrently.
        }
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            if (!Directory.Exists(directory)) return;
            if (!IsDirectoryEmpty(directory)) return;
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception e) when (IsMissingFileRace(e) || e is IOException)
        {
            // Another cleanup pass may have raced us or the directory may still contain a blob.
        }
    }

    private static bool IsDirectoryEmpty(string path)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (IsMissingFileRace(e))
        {
            // Delete is idempotent; a concurrently removed blob is already cleaned up.
        }
        catch (Exception e) when (IsDirectoryAtPath(e, path))
        {
            TryDeleteBlobPathDirectory(path);
        }
    }

    private static void TryDeleteBlobPathDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (IsMissingFileRace(e) || e is IOException or UnauthorizedAccessException)
        {
            // The path is a single blob location under the blob root. If the directory cannot
            // be removed now, leave it for a later cleanup pass instead of failing the caller.
        }
    }

    private static bool IsMissingFileRace(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException;

    private static bool IsDirectoryAtPath(Exception exception, string path) =>
        exception is IOException or UnauthorizedAccessException
        && Directory.Exists(path);

    private static bool IsBlobOpenFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsTemporarySerializedBlobReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsUnreadableSerializedBlob(Exception exception) =>
        exception is InvalidDataException
            or EndOfStreamException
            or MemoryPackSerializationException
            or ZstdException;
}

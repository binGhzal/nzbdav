namespace NzbWebDAV.Utils;

public static class SymlinkUtil
{
    public static IEnumerable<SymlinkInfo> GetAllSymlinks(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            yield break;

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(directoryPath);

        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            foreach (var entry in EnumerateFileSystemEntries(currentDirectory))
            {
                var attributes = TryGetAttributes(entry);
                if (attributes is null) continue;

                var isSymlink = attributes.Value.HasFlag(FileAttributes.ReparsePoint);
                var isDirectory = attributes.Value.HasFlag(FileAttributes.Directory);

                if (isSymlink)
                {
                    var info = GetSymlinkInfo(new FileInfo(entry));
                    if (info is not null)
                        yield return info.Value;
                }

                if (isDirectory && !isSymlink)
                    pendingDirectories.Push(entry);
            }
        }
    }

    private static IEnumerable<string> EnumerateFileSystemEntries(string directoryPath)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator();
        }
        catch (Exception exception) when (IsRecoverableFilesystemException(exception))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                    current = enumerator.Current;
                }
                catch (Exception exception) when (IsRecoverableFilesystemException(exception))
                {
                    yield break;
                }

                yield return current;
            }
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (IsRecoverableFilesystemException(exception))
        {
            return null;
        }
    }

    public static SymlinkInfo? GetSymlinkInfo(FileInfo fileInfo)
    {
        try
        {
            return IsSymlink(fileInfo)
                ? new SymlinkInfo { SymlinkPath = fileInfo.FullName, TargetPath = fileInfo.LinkTarget! }
                : null;
        }
        catch (Exception exception) when (IsRecoverableFilesystemException(exception))
        {
            return null;
        }
    }

    private static bool IsSymlink(FileInfo fileInfo) =>
        fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && fileInfo.LinkTarget is not null;

    private static bool IsRecoverableFilesystemException(Exception exception) =>
        exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    public readonly struct SymlinkInfo
    {
        public required string SymlinkPath { get; init; }
        public required string TargetPath { get; init; }
    }
}

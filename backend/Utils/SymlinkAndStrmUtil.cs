namespace NzbWebDAV.Utils;

public static class SymlinkAndStrmUtil
{
    public static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrms(string directoryPath)
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
                var isStrm = IsStrmPath(entry);

                if (isSymlink || isStrm)
                {
                    var info = GetSymlinkOrStrmInfo(new FileInfo(entry));
                    if (info is not null)
                        yield return info;
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
        catch (Exception e) when (IsRecoverableFilesystemException(e))
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
                catch (Exception e) when (IsRecoverableFilesystemException(e))
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
        catch (Exception e) when (IsRecoverableFilesystemException(e))
        {
            return null;
        }
    }

    public static ISymlinkOrStrmInfo? GetSymlinkOrStrmInfo(FileInfo x)
    {
        try
        {
            return IsStrm(x) ? GetStrmInfo(x.FullName)
                : IsSymLink(x) ? new SymlinkInfo { SymlinkPath = x.FullName, TargetPath = x.LinkTarget! }
                : null;
        }
        catch (Exception e) when (IsRecoverableFilesystemException(e))
        {
            return null;
        }
    }

    private static StrmInfo? GetStrmInfo(string path)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var targetUrl = reader.ReadLine()?.Trim();
            return string.IsNullOrWhiteSpace(targetUrl)
                ? null
                : new StrmInfo { StrmPath = path, TargetUrl = targetUrl };
        }
        catch (Exception e) when (IsRecoverableFilesystemException(e))
        {
            return null;
        }
    }

    private static bool IsStrm(FileInfo x) =>
        IsStrmPath(x.FullName);

    private static bool IsStrmPath(string path) =>
        Path.GetExtension(path).Equals(".strm", StringComparison.OrdinalIgnoreCase);

    private static bool IsSymLink(FileInfo x) =>
        x.Attributes.HasFlag(FileAttributes.ReparsePoint) && x.LinkTarget is not null;

    private static bool IsRecoverableFilesystemException(Exception exception) =>
        exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    public interface ISymlinkOrStrmInfo;

    public struct SymlinkInfo : ISymlinkOrStrmInfo
    {
        public required string SymlinkPath;
        public required string TargetPath;
    }

    public struct StrmInfo : ISymlinkOrStrmInfo
    {
        public required string StrmPath;
        public required string TargetUrl;
    }
}

namespace NzbWebDAV.Utils;

public static class FilePermissionUtil
{
    private const UnixFileMode GroupWritableDirectoryMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;

    private const UnixFileMode GroupWritableFileMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.OtherRead;

    public static void CreateDirectory(string directoryPath, string permissionRoot)
    {
        Directory.CreateDirectory(directoryPath);
        ApplyDirectoryPermissions(directoryPath, permissionRoot);
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        if (await HasSameTextAsync(path, contents, ct).ConfigureAwait(false))
        {
            TrySetUnixFileMode(path, GroupWritableFileMode);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tempPath = GetTempPath(path);
            try
            {
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllTextAsync(tempPath, contents, ct).ConfigureAwait(false);
                TrySetUnixFileMode(tempPath, GroupWritableFileMode);
                File.Move(tempPath, path, overwrite: true);
                break;
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
                TryDeleteTempFile(tempPath);
                continue;
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }

        TrySetUnixFileMode(path, GroupWritableFileMode);
    }

    private static async Task<bool> HasSameTextAsync(string path, string contents, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return string.Equals(existing, contents, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is FileNotFoundException
                                      or DirectoryNotFoundException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ArgumentException)
        {
            return false;
        }
    }

    private static string GetTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is FileNotFoundException
                                      or DirectoryNotFoundException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ArgumentException)
        {
        }
    }

    private static void ApplyDirectoryPermissions(string directoryPath, string permissionRoot)
    {
        if (OperatingSystem.IsWindows()) return;

        var root = TrimDirectorySeparator(Path.GetFullPath(permissionRoot));
        var current = TrimDirectorySeparator(Path.GetFullPath(directoryPath));
        while (IsAtOrInsideRoot(current, root))
        {
            TrySetUnixFileMode(current, GroupWritableDirectoryMode);
            if (current.Equals(root, StringComparison.Ordinal)) break;

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null) break;
            current = TrimDirectorySeparator(parent);
        }
    }

    private static bool IsAtOrInsideRoot(string path, string root)
    {
        return path.Equals(root, StringComparison.Ordinal)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string TrimDirectorySeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TrySetUnixFileMode(string path, UnixFileMode unixFileMode)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, unixFileMode);
        }
        catch (Exception e) when (e is IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException
                                      or NotSupportedException
                                      or ArgumentException)
        {
        }
    }
}

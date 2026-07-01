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
        await File.WriteAllTextAsync(path, contents, ct).ConfigureAwait(false);
        TrySetUnixFileMode(path, GroupWritableFileMode);
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}

using System.Reflection;
using NzbWebDAV.Database.Transfer;

#pragma warning disable CA1416 // Every Unix mode call is guarded by the verified POSIX ABI gate.

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3PosixOwnedDirectoryTests
{
    [Fact]
    public void PrivateDirectoryCreation_NormalizesUmaskFilteredModeBeforeFirstOpen()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);
        TransferV3Posix.CreateDirectoryAt(
            parent,
            "normalized-child",
            out var created);
        using var child = TransferV3Posix.OpenDirectoryAt(parent, "normalized-child");
        var stat = TransferV3Posix.GetFileStat(child);

        Assert.True(created);
        Assert.Equal(
            TransferV3Posix.PrivateDirectoryMode,
            stat.Mode & 0x1ff);

        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3Posix.cs"));
        var method = source.IndexOf(
            "internal static void CreateDirectoryAt(",
            StringComparison.Ordinal);
        var mkdir = source.IndexOf("MkdirAt(", method, StringComparison.Ordinal);
        var chmod = source.IndexOf("FchmodAt(", mkdir, StringComparison.Ordinal);
        Assert.True(method >= 0 && mkdir > method && chmod > mkdir);
    }

    [Fact]
    public void OwnedDirectoryCleanup_HasAnIdentityConditionalDescriptorRelativeApi()
    {
        var method = typeof(TransferV3Posix).GetMethod(
            "TryUnlinkOwnedDirectory",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(
            [
                typeof(Microsoft.Win32.SafeHandles.SafeFileHandle),
                typeof(string),
                typeof(TransferV3FileIdentity),
            ],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(TransferV3OwnedDirectoryUnlinkResult), method.ReturnType);
    }

    [Fact]
    public void OwnedDirectoryCleanup_UsesTheHeaderVerifiedPlatformFlags()
    {
        static int Constant(string name) => Assert.IsType<int>(
            typeof(TransferV3Posix)
                .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue());

        Assert.Equal(0x200, Constant("RemoveDirectoryLinux"));
        Assert.Equal(0x80, Constant("RemoveDirectoryMacOs"));
    }

    [Fact]
    public void OwnedDirectoryCleanup_RemovesTheIdentityMatchedEmptyChildAndKeepsDescriptorsPinned()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        var childPath = Path.Combine(fixture.Path, "owned-child");
        Directory.CreateDirectory(childPath);
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);
        using var child = TransferV3Posix.OpenDirectoryAt(parent, "owned-child");
        var parentIdentity = TransferV3Posix.GetIdentity(parent);
        var childIdentity = TransferV3Posix.GetIdentity(child);

        var result = TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            "owned-child",
            childIdentity);

        Assert.Equal(TransferV3OwnedDirectoryUnlinkResult.Removed, result);
        Assert.False(Directory.Exists(childPath));
        Assert.Equal(parentIdentity, TransferV3Posix.GetIdentity(parent));
        Assert.Equal(childIdentity, TransferV3Posix.GetIdentity(child));
    }

    [Fact]
    public void OwnedDirectoryCleanup_ReportsAMissingChildWithoutMutatingTheParent()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);
        var parentIdentity = TransferV3Posix.GetIdentity(parent);

        var result = TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            "missing-child",
            new TransferV3FileIdentity(ulong.MaxValue, ulong.MaxValue));

        Assert.Equal(TransferV3OwnedDirectoryUnlinkResult.Missing, result);
        Assert.Equal(parentIdentity, TransferV3Posix.GetIdentity(parent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void OwnedDirectoryCleanup_RefusesAPathReplacementWithAnotherDirectoryIdentity()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        var childPath = Path.Combine(fixture.Path, "owned-child");
        var displacedPath = Path.Combine(fixture.Path, "displaced-owned-child");
        Directory.CreateDirectory(childPath);
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);
        using var ownedChild = TransferV3Posix.OpenDirectoryAt(parent, "owned-child");
        var ownedIdentity = TransferV3Posix.GetIdentity(ownedChild);
        Directory.Move(childPath, displacedPath);
        Directory.CreateDirectory(childPath);
        using var replacement = TransferV3Posix.OpenDirectoryAt(parent, "owned-child");
        var replacementIdentity = TransferV3Posix.GetIdentity(replacement);

        var result = TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            "owned-child",
            ownedIdentity);

        Assert.Equal(TransferV3OwnedDirectoryUnlinkResult.UnknownEntry, result);
        Assert.NotEqual(ownedIdentity, replacementIdentity);
        Assert.Equal(ownedIdentity, TransferV3Posix.GetIdentity(ownedChild));
        Assert.Equal(replacementIdentity, TransferV3Posix.GetIdentity(replacement));
        Assert.True(Directory.Exists(childPath));
        Assert.True(Directory.Exists(displacedPath));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("symlink")]
    public void OwnedDirectoryCleanup_RefusesNonDirectoryEntriesWithoutFollowingThem(string kind)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        var candidatePath = Path.Combine(fixture.Path, "candidate");
        if (kind == "file")
        {
            File.WriteAllText(candidatePath, "unknown-content");
        }
        else
        {
            var targetPath = Path.Combine(fixture.Path, "symlink-target");
            Directory.CreateDirectory(targetPath);
            Directory.CreateSymbolicLink(candidatePath, targetPath);
        }
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);

        var result = TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            "candidate",
            new TransferV3FileIdentity(0, 0));

        Assert.Equal(TransferV3OwnedDirectoryUnlinkResult.UnknownEntry, result);
        Assert.True(File.Exists(candidatePath) || Directory.Exists(candidatePath));
        if (kind == "file") Assert.Equal("unknown-content", File.ReadAllText(candidatePath));
    }

    [Fact]
    public void OwnedDirectoryCleanup_RefusesAnIdentityMatchedDirectoryThatIsNotEmpty()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        var childPath = Path.Combine(fixture.Path, "owned-child");
        Directory.CreateDirectory(childPath);
        File.WriteAllText(Path.Combine(childPath, "unknown-entry"), "unknown-content");
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);
        using var child = TransferV3Posix.OpenDirectoryAt(parent, "owned-child");
        var childIdentity = TransferV3Posix.GetIdentity(child);

        var failure = Assert.Throws<IOException>(() =>
            TransferV3Posix.TryUnlinkOwnedDirectory(
                parent,
                "owned-child",
                childIdentity));

        Assert.Contains("unlinkat owned directory", failure.Message, StringComparison.Ordinal);
        Assert.Equal(childIdentity, TransferV3Posix.GetIdentity(child));
        Assert.Equal("unknown-content", File.ReadAllText(
            Path.Combine(childPath, "unknown-entry")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/child")]
    [InlineData("nul\0byte")]
    public void OwnedDirectoryCleanup_RejectsAnythingOtherThanOneSafePathComponent(string name)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        using var parent = TransferV3Posix.OpenDirectory(fixture.Path);

        Assert.Throws<IOException>(() => TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            name,
            new TransferV3FileIdentity(0, 0)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void OwnedDirectoryCleanup_UsesThePinnedParentWhenItsOriginalPathIsReplaced()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = new TemporaryDirectory();
        var parentPath = Path.Combine(fixture.Path, "pinned-parent");
        var displacedPath = Path.Combine(fixture.Path, "displaced-parent");
        var originalChildPath = Path.Combine(parentPath, "owned-child");
        Directory.CreateDirectory(originalChildPath);
        using var parent = TransferV3Posix.OpenDirectory(parentPath);
        using var child = TransferV3Posix.OpenDirectoryAt(parent, "owned-child");
        var parentIdentity = TransferV3Posix.GetIdentity(parent);
        var childIdentity = TransferV3Posix.GetIdentity(child);
        Directory.Move(parentPath, displacedPath);
        Directory.CreateDirectory(originalChildPath);

        var result = TransferV3Posix.TryUnlinkOwnedDirectory(
            parent,
            "owned-child",
            childIdentity);

        Assert.Equal(TransferV3OwnedDirectoryUnlinkResult.Removed, result);
        Assert.False(Directory.Exists(Path.Combine(displacedPath, "owned-child")));
        Assert.True(Directory.Exists(originalChildPath));
        Assert.Equal(parentIdentity, TransferV3Posix.GetIdentity(parent));
        Assert.Equal(childIdentity, TransferV3Posix.GetIdentity(child));
    }

    [Fact]
    public void DuplicateHandle_UsesAtomicPlatformFcntlAndEveryDescriptorIsCloseOnExec()
    {
        static int Constant(string name) => Assert.IsType<int>(
            typeof(TransferV3Posix)
                .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue());

        Assert.Equal(1030, Constant("DuplicateCloseOnExecLinux"));
        Assert.Equal(67, Constant("DuplicateCloseOnExecMacOs"));
        Assert.Equal(1, Constant("GetDescriptorFlags"));
        Assert.Equal(1, Constant("CloseOnExecFlag"));

        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3Posix.cs"));
        var start = source.IndexOf(
            "internal static SafeFileHandle DuplicateHandle(",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "internal static void CreateDirectoryAt(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.Contains("FcntlIntArgument", method, StringComparison.Ordinal);
        Assert.Contains("SystemNativeDup", method, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsMacOS()", method, StringComparison.Ordinal);
        Assert.Contains("libSystem.Native", source, StringComparison.Ordinal);
        Assert.Contains("SystemNative_Dup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("F_SETFD", method, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("new SafeFileHandle(", StringSplitOptions.None).Length - 1);
        var ownedHandleStart = source.IndexOf(
            "private static SafeFileHandle CreateOwnedSafeFileHandle(",
            StringComparison.Ordinal);
        var ownedHandleEnd = source.IndexOf(
            "private static int DirectoryOpenFlags()",
            ownedHandleStart,
            StringComparison.Ordinal);
        Assert.True(ownedHandleStart >= 0 && ownedHandleEnd > ownedHandleStart);
        var ownedHandle = source[ownedHandleStart..ownedHandleEnd];
        Assert.Contains("catch", ownedHandle, StringComparison.Ordinal);
        Assert.Contains("Close(descriptor)", ownedHandle, StringComparison.Ordinal);

        if (!TransferV3Posix.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                TransferV3Posix.GetEffectiveUserId());
            return;
        }

        using var fixture = new TemporaryDirectory();
        var original = TransferV3Posix.OpenDirectory(fixture.Path);
        var identity = TransferV3Posix.GetIdentity(original);
        using var duplicate = TransferV3Posix.DuplicateHandle(original);

        Assert.True(TransferV3Posix.DescriptorHasCloseOnExec(original));
        Assert.True(TransferV3Posix.DescriptorHasCloseOnExec(duplicate));
        original.Dispose();
        Assert.Equal(identity, TransferV3Posix.GetIdentity(duplicate));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                "nzbdav-transfer-v3-owned-directory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.SetUnixFileMode(
                Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }
}

#pragma warning restore CA1416

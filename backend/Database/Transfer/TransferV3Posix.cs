using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal readonly record struct TransferV3FileIdentity(ulong Device, ulong Inode);

internal readonly record struct TransferV3FileFingerprint(
    ulong Device,
    ulong Inode,
    long Size,
    long ModificationSeconds,
    long ModificationNanoseconds,
    long ChangeSeconds,
    long ChangeNanoseconds)
{
    internal TransferV3FileIdentity Identity => new(Device, Inode);
}

internal readonly record struct TransferV3FileStat(
    TransferV3FileFingerprint Fingerprint,
    uint Mode,
    ulong LinkCount,
    uint OwnerUid);

internal readonly record struct TransferV3DirectoryEntry(string? Name);

internal enum TransferV3OwnedFileUnlinkResult
{
    Missing,
    Removed,
    UnknownEntry,
}

internal enum TransferV3OwnedDirectoryUnlinkResult
{
    Missing,
    Removed,
    UnknownEntry,
}

internal enum TransferV3DescriptorRoutePlatform
{
    Linux,
    MacOs,
}

internal static class TransferV3Posix
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal const uint PrivateDirectoryMode = 0x1c0; // 0700
    internal const uint PrivateFileMode = 0x180; // 0600

    private const uint RenameNoReplaceLinux = 0x1;
    private const uint RenameExclusiveMacOs = 0x4;
    private const long RenameAt2Arm64Syscall = 276;
    private const long RenameAt2X64Syscall = 316;
    // linux/fcntl.h and Darwin sys/fcntl.h use different unlinkat(2) values.
    private const int RemoveDirectoryLinux = 0x200;
    private const int RemoveDirectoryMacOs = 0x80;
    private const int InvalidArgument = 22;
    private const int FunctionNotImplemented = 38;
    private const int NoSuchFileOrDirectory = 2;
    private const int DuplicateCloseOnExecLinux = 1030;
    private const int DuplicateCloseOnExecMacOs = 67;
    private const int GetDescriptorFlags = 1;
    private const int CloseOnExecFlag = 1;
    private const int StatBufferBytes = 256;
    private const int FileStatSnapshotBytes = 120;
    private const int LinuxStatVfsBytes = 112;
    private const int MacOsStatVfsBytes = 64;
    private const uint FileTypeMask = 0xf000;
    private const uint RegularFileType = 0x8000;

    // The open(2) flag values and stat layout below are verified for the two
    // deployment architectures exercised by the transfer contract. macOS x64
    // requires the $INODE64 fstat/fstatat/fdopendir/readdir entry points, while the
    // unsuffixed symbols use the legacy layouts. Until those entry points have
    // their own tested bindings, macOS x64 must fail closed.
    internal static bool IsSupported => IsSupportedPlatform(
        OperatingSystem.IsLinux(),
        OperatingSystem.IsMacOS(),
        RuntimeInformation.ProcessArchitecture);

    internal static bool IsSupportedPlatform(
        bool linux,
        bool macOs,
        Architecture architecture) =>
        linux && architecture is Architecture.X64 or Architecture.Arm64
        || macOs && architecture == Architecture.Arm64;

    internal static uint GetEffectiveUserId()
    {
        EnsureSupported();
        return GetEffectiveUserIdNative();
    }

    internal static SafeFileHandle OpenDirectory(string path)
    {
        EnsureSupported();
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetPathRoot(path), Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            throw new IOException("A descriptor-relative directory walk requires an absolute POSIX path.");
        }

        SafeFileHandle? current = null;
        try
        {
            var rootDescriptor = OpenAt(
                OperatingSystem.IsMacOS() ? -2 : -100,
                Path.DirectorySeparatorChar.ToString(),
                DirectoryOpenFlags(),
                0);
            if (rootDescriptor < 0)
            {
                throw NativeFailure("open filesystem root");
            }

            current = CreateOwnedSafeFileHandle(rootDescriptor);
            foreach (var component in path.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenDirectoryAt(current, component);
                current.Dispose();
                current = next;
            }

            var result = current;
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    internal static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name)
    {
        EnsureSupported();
        ValidateSingleComponent(name);
        var descriptor = OpenAt(Descriptor(parent), name, DirectoryOpenFlags(), 0);
        if (descriptor < 0)
        {
            throw NativeFailure("openat directory");
        }

        return CreateOwnedSafeFileHandle(descriptor);
    }

    internal static SafeFileHandle OpenReadOnlyRegularFileAt(
        SafeFileHandle directory,
        string name)
    {
        EnsureSupported();
        ValidateSingleComponent(name);
        var descriptor = OpenAt(
            Descriptor(directory),
            name,
            ReadOnlyFileOpenFlags(),
            0);
        if (descriptor < 0)
        {
            throw NativeFailure("openat read-only no-follow file");
        }

        var handle = CreateOwnedSafeFileHandle(descriptor);
        try
        {
            if ((GetFileMode(handle) & FileTypeMask) != RegularFileType)
            {
                throw new IOException("The descriptor-relative source entry is not a regular file.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static IEnumerable<string> EnumerateDirectoryNames(SafeFileHandle directory)
    {
        foreach (var entry in EnumerateDirectoryEntries(directory))
        {
            if (entry.Name is null)
            {
                throw new IOException(
                    "A transfer directory entry name is not valid UTF-8.");
            }

            yield return entry.Name;
        }
    }

    internal static IEnumerable<TransferV3DirectoryEntry> EnumerateDirectoryEntries(
        SafeFileHandle directory)
    {
        EnsureSupported();
        // dup(2) shares the directory stream offset with the retained open file
        // description. Reopen the hard-coded current directory component so
        // every validation pass has an independent offset while remaining
        // descriptor-relative and pinned to the same directory identity.
        var reopened = OpenAt(Descriptor(directory), ".", DirectoryOpenFlags(), 0);
        if (reopened < 0)
        {
            throw NativeFailure("reopen directory descriptor");
        }

        var directoryStream = FileDescriptorOpenDirectory(reopened);
        if (directoryStream == IntPtr.Zero)
        {
            var failure = NativeFailure("fdopendir");
            _ = Close(reopened);
            throw failure;
        }

        using var stream = new SafeDirectoryStreamHandle(directoryStream);
        while (true)
        {
            Marshal.SetLastPInvokeError(0);
            var entry = ReadDirectory(stream);
            if (entry == IntPtr.Zero)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 0)
                {
                    throw NativeFailure("readdir", error);
                }

                yield break;
            }

            var name = ReadDirectoryEntryName(entry);
            if (name.Name is not "." and not "..")
            {
                yield return name;
            }
        }
    }

    internal static SafeFileHandle CreateFileAt(
        SafeFileHandle directory,
        string name,
        out TransferV3FileIdentity createdIdentity)
    {
        var descriptor = OpenAt(
            Descriptor(directory),
            name,
            FileCreateFlags(),
            PrivateFileMode);
        if (descriptor < 0)
        {
            throw NativeFailure("openat create-new file");
        }

        var handle = CreateOwnedSafeFileHandle(descriptor);
        TransferV3FileIdentity? identity = null;
        try
        {
            identity = GetIdentity(handle);
            if (Fchmod(Descriptor(handle), PrivateFileMode) != 0)
            {
                throw NativeFailure("fchmod private file");
            }

            createdIdentity = identity.Value;
            return handle;
        }
        catch (Exception primary)
        {
            var cleanupCodes = new List<string>();
            if (identity is not null)
            {
                try
                {
                    if (TryUnlinkOwnedRegularFile(directory, name, identity.Value)
                        == TransferV3OwnedFileUnlinkResult.UnknownEntry)
                    {
                        AddCleanupCode(cleanupCodes, "unknown-entry-residue");
                    }
                }
                catch
                {
                    AddCleanupCode(cleanupCodes, "owned-entry-unlink-failed");
                }
            }
            else
            {
                AddCleanupCode(cleanupCodes, "untracked-created-entry");
            }
            try
            {
                handle.Dispose();
            }
            catch
            {
                AddCleanupCode(cleanupCodes, "owned-descriptor-close-failed");
            }
            ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
            throw new InvalidOperationException("Unreachable owned-file creation cleanup path.");
        }
    }

    internal static SafeFileHandle DuplicateHandle(SafeFileHandle handle)
    {
        EnsureSupported();
        var command = OperatingSystem.IsMacOS()
            ? DuplicateCloseOnExecMacOs
            : DuplicateCloseOnExecLinux;
        var sourceDescriptor = Descriptor(handle);
        // Darwin arm64 passes variadic arguments on the stack, so a fixed
        // three-argument P/Invoke cannot call fcntl(2) correctly there. The
        // runtime's nonvariadic SystemNative_Dup bridge performs the same
        // atomic fcntl(F_DUPFD_CLOEXEC, 0) operation using the platform C ABI.
        var descriptor = OperatingSystem.IsMacOS()
            ? checked((int)SystemNativeDup((IntPtr)sourceDescriptor))
            : FcntlIntArgument(sourceDescriptor, command, 0);
        if (descriptor < 0)
        {
            throw NativeFailure("fcntl F_DUPFD_CLOEXEC retained owned file descriptor");
        }

        SafeFileHandle? duplicate = null;
        try
        {
            duplicate = CreateOwnedSafeFileHandle(descriptor);
            if (!DescriptorHasCloseOnExec(duplicate))
            {
                throw new IOException(
                    "The atomically duplicated descriptor is not close-on-exec.");
            }

            return duplicate;
        }
        catch
        {
            duplicate?.Dispose();

            throw;
        }
    }

    internal static bool DescriptorHasCloseOnExec(SafeFileHandle handle)
    {
        EnsureSupported();
        var flags = FcntlNoArgument(Descriptor(handle), GetDescriptorFlags);
        if (flags < 0)
        {
            throw NativeFailure("fcntl F_GETFD");
        }

        return (flags & CloseOnExecFlag) == CloseOnExecFlag;
    }

    internal static void CreateDirectoryAt(
        SafeFileHandle parent,
        string name,
        out bool entryCreated)
    {
        EnsureSupported();
        ValidateSingleComponent(name);
        entryCreated = false;
        if (MkdirAt(Descriptor(parent), name, PrivateDirectoryMode) != 0)
        {
            throw NativeFailure("mkdirat create-new private directory");
        }
        entryCreated = true;

        // mkdirat mode is filtered by the process umask. Normalize the unique,
        // just-created component before any caller must open it. This pathname
        // chmod is covered by the same quiescent same-UID threat model as the
        // later identity-check-to-unlink cleanup interval; after opening, every
        // caller captures and revalidates the directory identity.
        if (FchmodAt(
                Descriptor(parent),
                name,
                PrivateDirectoryMode,
                flags: 0) != 0)
        {
            throw NativeFailure("fchmodat just-created private directory");
        }
    }

    internal static void CreateDirectoryAt(SafeFileHandle parent, string name) =>
        CreateDirectoryAt(parent, name, out _);

    internal static void SetMode(SafeFileHandle handle, uint mode)
    {
        if (Fchmod(Descriptor(handle), mode) != 0)
        {
            throw NativeFailure("fchmod");
        }
    }

    internal static void Sync(SafeFileHandle handle)
    {
        if (Fsync(Descriptor(handle)) != 0)
        {
            throw NativeFailure("fsync");
        }
    }

    internal static void RenameNoReplaceAt(
        SafeFileHandle directory,
        string source,
        string destination)
    {
        var descriptor = Descriptor(directory);
        var result = OperatingSystem.IsMacOS()
            ? RenameAtX(descriptor, source, descriptor, destination, RenameExclusiveMacOs)
            : RenameAt2BySyscall(descriptor, source, destination);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is InvalidArgument or FunctionNotImplemented)
            {
                throw NativeFailure(
                    "atomic rename without replacement is unavailable; refusing overwrite fallback",
                    error);
            }

            throw NativeFailure("atomic rename without replacement", error);
        }
    }

    internal static bool TryUnlinkFile(
        SafeFileHandle directory,
        string name,
        List<Exception>? errors = null)
    {
        var result = UnlinkAt(
            Descriptor(directory),
            name,
            flags: 0);
        if (result == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 2) // ENOENT
        {
            return false;
        }

        var failure = NativeFailure("unlinkat", error);
        if (errors is null)
        {
            throw failure;
        }

        errors.Add(failure);
        return false;
    }

    internal static TransferV3OwnedFileUnlinkResult TryUnlinkOwnedRegularFile(
        SafeFileHandle directory,
        string name,
        TransferV3FileIdentity expected)
    {
        // POSIX has no atomic identity-conditional unlink. This check plus
        // pathname unlink is safe for the transfer contract's quiescent
        // private-directory cleanup. Active same-UID mutation in the final
        // fstat-to-unlink interval is explicitly outside that threat model.
        SafeFileHandle? candidate = null;
        try
        {
            candidate = OpenReadOnlyRegularFileAt(directory, name);
        }
        catch (IOException)
        {
            return EntryExistsNoFollow(directory, name)
                ? TransferV3OwnedFileUnlinkResult.UnknownEntry
                : TransferV3OwnedFileUnlinkResult.Missing;
        }

        using (candidate)
        {
            if (GetIdentity(candidate) != expected)
            {
                return TransferV3OwnedFileUnlinkResult.UnknownEntry;
            }

            var result = UnlinkAt(Descriptor(directory), name, flags: 0);
            if (result == 0)
            {
                return TransferV3OwnedFileUnlinkResult.Removed;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error == NoSuchFileOrDirectory)
            {
                return TransferV3OwnedFileUnlinkResult.Missing;
            }
            throw NativeFailure("unlinkat owned regular file", error);
        }
    }

    internal static TransferV3OwnedDirectoryUnlinkResult TryUnlinkOwnedDirectory(
        SafeFileHandle parent,
        string name,
        TransferV3FileIdentity expected)
    {
        // POSIX has no atomic identity-conditional directory unlink. This
        // identity check followed by unlinkat is valid only for the transfer
        // contract's quiescent private directories. Active same-UID mutation
        // in the final fstat-to-unlink interval remains outside that threat
        // model and must not be described as race-free cleanup.
        EnsureSupported();
        ValidateSingleComponent(name);
        SafeFileHandle candidate;
        try
        {
            candidate = OpenDirectoryAt(parent, name);
        }
        catch (IOException)
        {
            return EntryExistsNoFollow(parent, name)
                ? TransferV3OwnedDirectoryUnlinkResult.UnknownEntry
                : TransferV3OwnedDirectoryUnlinkResult.Missing;
        }

        using (candidate)
        {
            if (GetIdentity(candidate) != expected)
            {
                return TransferV3OwnedDirectoryUnlinkResult.UnknownEntry;
            }

            var result = UnlinkAt(
                Descriptor(parent),
                name,
                OperatingSystem.IsMacOS() ? RemoveDirectoryMacOs : RemoveDirectoryLinux);
            if (result == 0)
            {
                return TransferV3OwnedDirectoryUnlinkResult.Removed;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == NoSuchFileOrDirectory)
            {
                return TransferV3OwnedDirectoryUnlinkResult.Missing;
            }

            throw NativeFailure("unlinkat owned directory", error);
        }
    }

    internal static bool EntryMatches(
        SafeFileHandle parent,
        string name,
        TransferV3FileIdentity expected)
    {
        SafeFileHandle? candidate = null;
        try
        {
            candidate = OpenDirectoryAt(parent, name);
            return GetIdentity(candidate) == expected;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    internal static TransferV3FileIdentity GetIdentity(SafeFileHandle handle)
        => GetFingerprint(handle).Identity;

    internal static byte[] EncodeFingerprint(TransferV3FileFingerprint fingerprint)
    {
        var bytes = new byte[7 * sizeof(long)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), fingerprint.Device);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), fingerprint.Inode);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16, 8), fingerprint.Size);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24, 8), fingerprint.ModificationSeconds);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32, 8), fingerprint.ModificationNanoseconds);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(40, 8), fingerprint.ChangeSeconds);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(48, 8), fingerprint.ChangeNanoseconds);
        return bytes;
    }

    internal static string ProbeSqliteDescriptorRoute(SafeFileHandle retained)
    {
        EnsureSupported();
        var descriptor = Descriptor(retained);
        var platform = OperatingSystem.IsLinux()
            ? TransferV3DescriptorRoutePlatform.Linux
            : TransferV3DescriptorRoutePlatform.MacOs;
        var route = BuildDescriptorRoutePath(descriptor, platform);
        var probeDescriptor = OpenPath(route, DescriptorRouteOpenFlags(), 0);
        if (probeDescriptor < 0)
            throw NativeFailure("open descriptor route");

        using var probe = CreateOwnedSafeFileHandle(probeDescriptor);
        if ((GetFileMode(probe) & FileTypeMask) != RegularFileType
            || GetIdentity(probe) != GetIdentity(retained))
        {
            throw new IOException("The SQLite descriptor route did not resolve to the guarded source identity.");
        }

        return BuildSqliteDescriptorUri(descriptor, platform);
    }

    internal static string BuildSqliteDescriptorUri(
        int descriptor,
        TransferV3DescriptorRoutePlatform platform)
    {
        if (descriptor < 0) throw new ArgumentOutOfRangeException(nameof(descriptor));
        return "file://" + BuildDescriptorRoutePath(descriptor, platform)
               + "?mode=ro&immutable=1&cache=private";
    }

    internal static bool EntryExistsNoFollow(SafeFileHandle directory, string name)
    {
        EnsureSupported();
        ValidateSingleComponent(name);
        var buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            var flags = OperatingSystem.IsMacOS() ? 0x0020 : 0x0100;
            if (FstatAt(Descriptor(directory), name, buffer, flags) == 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            if (error == NoSuchFileOrDirectory)
                return false;
            throw NativeFailure("fstatat no-follow", error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static TransferV3FileStat GetFileStat(SafeFileHandle handle)
    {
        EnsureSupported();
        var buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            if (Fstat(Descriptor(handle), buffer) != 0)
            {
                throw NativeFailure("fstat");
            }

            return DecodeFileStat(
                new FileStatSnapshotReader(buffer),
                FileStatSnapshotBytes,
                OperatingSystem.IsLinux(),
                OperatingSystem.IsMacOS(),
                RuntimeInformation.ProcessArchitecture);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static TransferV3FileStat DecodeFileStatSnapshot(
        ReadOnlySpan<byte> snapshot,
        bool linux,
        bool macOs,
        Architecture architecture) =>
        DecodeFileStat(
            new FileStatSnapshotReader(snapshot),
            snapshot.Length,
            linux,
            macOs,
            architecture);

    private static TransferV3FileStat DecodeFileStat(
        FileStatSnapshotReader snapshot,
        int snapshotLength,
        bool linux,
        bool macOs,
        Architecture architecture)
    {
        if (linux == macOs || !IsSupportedPlatform(linux, macOs, architecture))
        {
            throw new PlatformNotSupportedException(
                "The requested Transfer-v3 stat layout is not a verified platform ABI.");
        }

        var requiredBytes = macOs ? 104 : 120;
        if (snapshotLength < requiredBytes)
        {
            throw new InvalidDataException("The Transfer-v3 stat snapshot is truncated.");
        }

        ulong device;
        uint mode;
        ulong linkCount;
        uint ownerUid;
        if (macOs)
        {
            // Darwin arm64 struct stat: dev@0 (uint32), mode@4 (uint16),
            // nlink@6 (uint16), ino@8, uid@16, mtime@48, ctime@64,
            // size@96.
            device = snapshot.ReadUInt32(0);
            mode = snapshot.ReadUInt16(4);
            linkCount = snapshot.ReadUInt16(6);
            ownerUid = snapshot.ReadUInt32(16);
        }
        else if (architecture == Architecture.Arm64)
        {
            // GNU/Linux arm64 struct stat: mode@16 (uint32), nlink@20
            // (uint32), uid@24 (uint32). The remaining fields used here match
            // Linux x64.
            device = snapshot.ReadUInt64(0);
            mode = snapshot.ReadUInt32(16);
            linkCount = snapshot.ReadUInt32(20);
            ownerUid = snapshot.ReadUInt32(24);
        }
        else
        {
            // GNU/Linux x64 struct stat: nlink@16 (ulong), mode@24 (uint32),
            // uid@28 (uint32).
            device = snapshot.ReadUInt64(0);
            linkCount = snapshot.ReadUInt64(16);
            mode = snapshot.ReadUInt32(24);
            ownerUid = snapshot.ReadUInt32(28);
        }

        var sizeOffset = macOs ? 96 : 48;
        var modificationOffset = macOs ? 48 : 88;
        var changeOffset = macOs ? 64 : 104;
        var fingerprint = new TransferV3FileFingerprint(
            device,
            snapshot.ReadUInt64(8),
            snapshot.ReadInt64(sizeOffset),
            snapshot.ReadInt64(modificationOffset),
            snapshot.ReadInt64(modificationOffset + sizeof(long)),
            snapshot.ReadInt64(changeOffset),
            snapshot.ReadInt64(changeOffset + sizeof(long)));
        return new TransferV3FileStat(fingerprint, mode, linkCount, ownerUid);
    }

    private readonly ref struct FileStatSnapshotReader
    {
        private readonly ReadOnlySpan<byte> _managed;
        private readonly IntPtr _native;
        private readonly bool _nativeBacked;

        internal FileStatSnapshotReader(ReadOnlySpan<byte> managed)
        {
            _managed = managed;
            _native = IntPtr.Zero;
            _nativeBacked = false;
        }

        internal FileStatSnapshotReader(IntPtr native)
        {
            _managed = default;
            _native = native;
            _nativeBacked = true;
        }

        internal ushort ReadUInt16(int offset) => _nativeBacked
            ? unchecked((ushort)Marshal.ReadInt16(_native, offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(_managed[offset..]);

        internal uint ReadUInt32(int offset) => _nativeBacked
            ? unchecked((uint)Marshal.ReadInt32(_native, offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(_managed[offset..]);

        internal ulong ReadUInt64(int offset) => _nativeBacked
            ? unchecked((ulong)Marshal.ReadInt64(_native, offset))
            : BinaryPrimitives.ReadUInt64LittleEndian(_managed[offset..]);

        internal long ReadInt64(int offset) => _nativeBacked
            ? Marshal.ReadInt64(_native, offset)
            : BinaryPrimitives.ReadInt64LittleEndian(_managed[offset..]);
    }

    internal static long GetAvailableBytes(SafeFileHandle handle)
    {
        EnsureSupported();
        var bufferBytes = OperatingSystem.IsMacOS()
            ? MacOsStatVfsBytes
            : LinuxStatVfsBytes;
        var buffer = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            if (FstatVfs(Descriptor(handle), buffer) != 0)
            {
                throw NativeFailure("fstatvfs");
            }

            return DecodeAvailableBytes(
                new FileStatSnapshotReader(buffer),
                bufferBytes,
                OperatingSystem.IsLinux(),
                OperatingSystem.IsMacOS(),
                RuntimeInformation.ProcessArchitecture);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static long DecodeAvailableBytesSnapshot(
        ReadOnlySpan<byte> snapshot,
        bool linux,
        bool macOs,
        Architecture architecture) =>
        DecodeAvailableBytes(
            new FileStatSnapshotReader(snapshot),
            snapshot.Length,
            linux,
            macOs,
            architecture);

    private static long DecodeAvailableBytes(
        FileStatSnapshotReader snapshot,
        int snapshotLength,
        bool linux,
        bool macOs,
        Architecture architecture)
    {
        if (linux == macOs || !IsSupportedPlatform(linux, macOs, architecture))
        {
            throw new PlatformNotSupportedException(
                "The requested Transfer-v3 statvfs layout is not a verified platform ABI.");
        }

        var requiredBytes = macOs ? MacOsStatVfsBytes : LinuxStatVfsBytes;
        if (snapshotLength < requiredBytes)
        {
            throw new InvalidDataException(
                "The Transfer-v3 statvfs snapshot is truncated.");
        }

        var fragmentSize = snapshot.ReadUInt64(8);
        if (fragmentSize == 0)
        {
            throw new InvalidDataException(
                "The Transfer-v3 statvfs fragment size is zero.");
        }

        var availableBlocks = macOs
            ? snapshot.ReadUInt32(24)
            : snapshot.ReadUInt64(32);
        if (availableBlocks == 0)
        {
            return 0;
        }

        var availableBytes = checked(availableBlocks * fragmentSize);
        return checked((long)availableBytes);
    }

    internal static TransferV3FileFingerprint GetFingerprint(SafeFileHandle handle)
        => GetFileStat(handle).Fingerprint;

    [DoesNotReturn]
    internal static void ThrowPrimaryAndCleanup(
        Exception primary,
        IReadOnlyCollection<Exception> cleanup,
        string message)
    {
        _ = message;
        ThrowPrimaryWithCleanupCodes(
            primary,
            cleanup.Count == 0
                ? Array.Empty<string>()
                : ["cleanup-failed"]);
    }

    [DoesNotReturn]
    internal static void ThrowPrimaryWithCleanupCodes(
        Exception primary,
        IEnumerable<string> cleanupCodes)
    {
        var codes = new List<string>();
        try
        {
            if (primary.Data["TransferV3CleanupCodes"] is IEnumerable<string> existing)
            {
                foreach (var code in existing) AddCleanupCode(codes, code);
            }
        }
        catch
        {
            // Hostile diagnostic storage must never replace the exact primary.
        }
        try
        {
            foreach (var code in cleanupCodes) AddCleanupCode(codes, code);
        }
        catch
        {
            // Cleanup evidence is best-effort; the primary remains authoritative.
        }
        if (codes.Count > 0)
        {
            try
            {
                primary.Data["TransferV3CleanupCodes"] = codes.AsReadOnly();
            }
            catch
            {
                // Diagnostic attachment must never replace the exact primary.
            }
        }
        ExceptionDispatchInfo.Capture(primary).Throw();
        throw new InvalidOperationException("Unreachable primary rethrow path.");
    }

    internal static void AddCleanupCode(List<string> codes, string code)
    {
        if (!codes.Contains(code, StringComparer.Ordinal)) codes.Add(code);
    }

    internal static int Descriptor(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ObjectDisposedException(nameof(handle));
        }

        return checked((int)handle.DangerousGetHandle());
    }

    private static SafeFileHandle CreateOwnedSafeFileHandle(int descriptor)
    {
        try
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        catch
        {
            _ = Close(descriptor);
            throw;
        }
    }

    private static int DirectoryOpenFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x00100000 | 0x00000100 | 0x01000000;
        }

        var arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;
        return (arm ? 0x00004000 | 0x00008000 : 0x00010000 | 0x00020000)
               | 0x00080000;
    }

    private static int FileCreateFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x00000001 | 0x00000200 | 0x00000800 | 0x00000100 | 0x01000000;
        }

        var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64
            ? 0x00008000
            : 0x00020000;
        return 0x00000001 | 0x00000040 | 0x00000080 | noFollow | 0x00080000;
    }

    private static int ReadOnlyFileOpenFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x00000004 | 0x00000100 | 0x01000000;
        }

        var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64
            ? 0x00008000
            : 0x00020000;
        return 0x00000800 | noFollow | 0x00080000;
    }

    private static int DescriptorRouteOpenFlags()
    {
        // procfs/devfs descriptor routes are links by design, so O_NOFOLLOW is
        // intentionally absent. The route is constructed only from a retained
        // integer descriptor and immediately proven with fstat identity.
        return OperatingSystem.IsMacOS()
            ? 0x00000004 | 0x01000000
            : 0x00080000;
    }

    private static string BuildDescriptorRoutePath(
        int descriptor,
        TransferV3DescriptorRoutePlatform platform)
    {
        if (descriptor < 0) throw new ArgumentOutOfRangeException(nameof(descriptor));
        var prefix = platform switch
        {
            TransferV3DescriptorRoutePlatform.Linux => "/proc/self/fd/",
            TransferV3DescriptorRoutePlatform.MacOs => "/dev/fd/",
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };
        return prefix + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static uint GetFileMode(SafeFileHandle handle) => GetFileStat(handle).Mode;

    private static TransferV3DirectoryEntry ReadDirectoryEntryName(IntPtr entry)
    {
        var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
        var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, 16));
        if (recordLength <= nameOffset)
        {
            throw new IOException("A directory entry name exceeded the supported POSIX layout.");
        }

        int length;
        if (OperatingSystem.IsMacOS())
        {
            length = unchecked((ushort)Marshal.ReadInt16(entry, 18));
            if (length is < 1 or > 255 || nameOffset + length > recordLength)
            {
                throw new IOException("A directory entry name exceeded the supported POSIX layout.");
            }
        }
        else
        {
            var available = recordLength - nameOffset;
            var scanLength = Math.Min(available, 256);
            length = 0;
            while (length < scanLength && Marshal.ReadByte(entry, nameOffset + length) != 0)
            {
                length++;
            }
            if (length is < 1 or > 255 || length == scanLength)
            {
                throw new IOException("A Linux directory entry was not validly NUL-terminated.");
            }
        }

        var bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(entry, nameOffset), bytes, 0, length);
        try
        {
            return new TransferV3DirectoryEntry(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return new TransferV3DirectoryEntry(null);
        }
    }

    private static void ValidateSingleComponent(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\0'))
        {
            throw new IOException("Descriptor-relative file access requires one nonempty path component.");
        }
    }

    private static long RenameAt2BySyscall(
        int directory,
        string source,
        string destination)
    {
        var syscallNumber = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => RenameAt2Arm64Syscall,
            Architecture.X64 => RenameAt2X64Syscall,
            _ => throw new PlatformNotSupportedException(
                "Linux renameat2 is verified only for arm64 and x64 syscall ABIs."),
        };
        return SyscallRenameAt2(
            syscallNumber,
            directory,
            source,
            directory,
            destination,
            RenameNoReplaceLinux);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Transfer v3 private snapshots require Linux x64/arm64 or macOS arm64 with a verified POSIX ABI.");
        }
    }

    private static IOException NativeFailure(string operation, int? error = null)
    {
        var errno = error ?? Marshal.GetLastPInvokeError();
        return new IOException($"{operation} failed (errno {errno}).");
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenAt(int directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenPath(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MkdirAt(int directory, string path, uint mode);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", EntryPoint = "fchmodat", SetLastError = true)]
    private static extern int FchmodAt(
        int directory,
        string path,
        uint mode,
        int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fileDescriptor);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int fileDescriptor, IntPtr statBuffer);

    [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
    private static extern int FstatVfs(int fileDescriptor, IntPtr statVfsBuffer);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int FstatAt(int directory, string path, IntPtr statBuffer, int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int UnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern long SyscallRenameAt2(
        long syscallNumber,
        int fromDirectory,
        string from,
        int toDirectory,
        string to,
        uint flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int RenameAtX(
        int fromDirectory,
        string from,
        int toDirectory,
        string to,
        uint flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlNoArgument(int fileDescriptor, int command);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlIntArgument(
        int fileDescriptor,
        int command,
        int argument);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Dup", SetLastError = true)]
    private static extern IntPtr SystemNativeDup(IntPtr fileDescriptor);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserIdNative();

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FileDescriptorOpenDirectory(int fileDescriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDirectory(SafeDirectoryStreamHandle directoryStream);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectory(IntPtr directoryStream);

    private sealed class SafeDirectoryStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeDirectoryStreamHandle(IntPtr value) : base(ownsHandle: true)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle() => CloseDirectory(handle) == 0;
    }

}

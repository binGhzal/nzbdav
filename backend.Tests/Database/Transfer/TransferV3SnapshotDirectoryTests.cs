using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotDirectoryTests
{
    [Fact]
    public void PosixPlatformContract_FailsClosedOnMacOsX64WithoutInode64Entrypoints()
    {
        var method = typeof(TransferV3Posix).GetMethod(
            "IsSupportedPlatform",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        bool Supported(bool linux, bool macOs, Architecture architecture) =>
            Assert.IsType<bool>(method.Invoke(null, [linux, macOs, architecture]));

        Assert.True(Supported(linux: true, macOs: false, Architecture.X64));
        Assert.True(Supported(linux: true, macOs: false, Architecture.Arm64));
        Assert.True(Supported(linux: false, macOs: true, Architecture.Arm64));
        Assert.False(Supported(linux: false, macOs: true, Architecture.X64));
        Assert.False(Supported(linux: false, macOs: false, Architecture.X64));
    }

    [Fact]
    public void PosixPlatformContract_IsExactInSourceAndOperatorDocumentation()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3Posix.cs"));
        var documentation = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/Contracts/README.md"));

        Assert.Contains(
            "macOs && architecture == Architecture.Arm64",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Linux x64/arm64 and macOS arm64", documentation, StringComparison.Ordinal);
        Assert.Contains("macOS x64", documentation, StringComparison.Ordinal);
        Assert.Contains("$INODE64", documentation, StringComparison.Ordinal);
        Assert.Contains("fstatat", documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void PosixFileStat_ReportsCompleteModesIdentityAndLiveHardLinkCount()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var created = snapshot.CreateFile("stat.bin"))
        {
            created.Write("stat-evidence"u8);
        }

        using var root = TransferV3Posix.OpenDirectory(output);
        var rootStat = TransferV3Posix.GetFileStat(root);
        Assert.Equal(TransferV3Posix.GetFingerprint(root), rootStat.Fingerprint);
        Assert.Equal(rootStat.Fingerprint.Identity, TransferV3Posix.GetIdentity(root));
        Assert.Equal(0x4000u, rootStat.Mode & 0xf000u);
        Assert.Equal(TransferV3Posix.PrivateDirectoryMode, rootStat.Mode & 0x1ffu);
        Assert.Equal(TransferV3Posix.GetEffectiveUserId(), rootStat.OwnerUid);
        Assert.True(rootStat.LinkCount > 0);

        using var file = TransferV3Posix.OpenReadOnlyRegularFileAt(root, "stat.bin");
        var initial = TransferV3Posix.GetFileStat(file);
        Assert.Equal(TransferV3Posix.GetFingerprint(file), initial.Fingerprint);
        Assert.Equal(initial.Fingerprint.Identity, TransferV3Posix.GetIdentity(file));
        Assert.Equal(0x8000u, initial.Mode & 0xf000u);
        Assert.Equal(TransferV3Posix.PrivateFileMode, initial.Mode & 0x1ffu);
        Assert.Equal(TransferV3Posix.GetEffectiveUserId(), initial.OwnerUid);
        Assert.Equal(1UL, initial.LinkCount);

        var aliasPath = Path.Combine(output, "stat-alias.bin");
        CreateHardLink(Path.Combine(output, "stat.bin"), aliasPath);
        var linked = TransferV3Posix.GetFileStat(file);
        Assert.Equal(initial.Fingerprint.Identity, linked.Fingerprint.Identity);
        Assert.Equal(2UL, linked.LinkCount);
        using (var alias = TransferV3Posix.OpenReadOnlyRegularFileAt(root, "stat-alias.bin"))
        {
            Assert.Equal(linked.Fingerprint.Identity, TransferV3Posix.GetFileStat(alias).Fingerprint.Identity);
        }

        File.Delete(aliasPath);
        Assert.Equal(1UL, TransferV3Posix.GetFileStat(file).LinkCount);
    }

    [Fact]
    public void PosixFileStat_RepeatedWarmReadsDoNotAllocateManagedSnapshots()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var filePath = Path.Combine(parent.Path, "stat-allocation.bin");
        File.WriteAllBytes(filePath, "allocation-evidence"u8.ToArray());
        using var directory = TransferV3Posix.OpenDirectory(parent.Path);
        using var file = TransferV3Posix.OpenReadOnlyRegularFileAt(
            directory,
            "stat-allocation.bin");

        ulong evidence = 0;
        for (var index = 0; index < 32; index++)
        {
            evidence ^= TransferV3Posix.GetFileStat(file).Fingerprint.Inode;
            evidence ^= TransferV3Posix.GetFingerprint(file).Device;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 256; index++)
        {
            evidence ^= TransferV3Posix.GetFileStat(file).Fingerprint.Inode;
            evidence ^= TransferV3Posix.GetFingerprint(file).Device;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(evidence);
        Assert.InRange(allocated, 0, 4096);
    }

    [Fact]
    public void PosixFileStatSnapshot_UsesVerifiedPlatformWidthsOffsetsAndCanonicalFingerprintEncoding()
    {
        var linuxX64Bytes = new byte[256];
        BinaryPrimitives.WriteUInt64LittleEndian(linuxX64Bytes.AsSpan(0), 0x0102030405060708UL);
        BinaryPrimitives.WriteUInt64LittleEndian(linuxX64Bytes.AsSpan(8), 0x1112131415161718UL);
        BinaryPrimitives.WriteUInt64LittleEndian(linuxX64Bytes.AsSpan(16), 0xf000000000000002UL);
        BinaryPrimitives.WriteUInt32LittleEndian(linuxX64Bytes.AsSpan(24), 0x00008180U);
        BinaryPrimitives.WriteUInt32LittleEndian(linuxX64Bytes.AsSpan(28), 0xa1b2c3d4U);
        BinaryPrimitives.WriteInt64LittleEndian(linuxX64Bytes.AsSpan(48), 0x2122232425262728L);
        BinaryPrimitives.WriteInt64LittleEndian(linuxX64Bytes.AsSpan(88), 0x3132333435363738L);
        BinaryPrimitives.WriteInt64LittleEndian(linuxX64Bytes.AsSpan(96), 0x4142434445464748L);
        BinaryPrimitives.WriteInt64LittleEndian(linuxX64Bytes.AsSpan(104), 0x5152535455565758L);
        BinaryPrimitives.WriteInt64LittleEndian(linuxX64Bytes.AsSpan(112), 0x6162636465666768L);

        var linuxX64 = TransferV3Posix.DecodeFileStatSnapshot(
            linuxX64Bytes,
            linux: true,
            macOs: false,
            Architecture.X64);
        Assert.Equal(0x00008180U, linuxX64.Mode);
        Assert.Equal(0xf000000000000002UL, linuxX64.LinkCount);
        Assert.Equal(0xa1b2c3d4U, linuxX64.OwnerUid);
        Assert.Equal(
            new TransferV3FileFingerprint(
                0x0102030405060708UL,
                0x1112131415161718UL,
                0x2122232425262728L,
                0x3132333435363738L,
                0x4142434445464748L,
                0x5152535455565758L,
                0x6162636465666768L),
            linuxX64.Fingerprint);

        var encoded = TransferV3Posix.EncodeFingerprint(linuxX64.Fingerprint);
        var expectedEncoding = new byte[56];
        BinaryPrimitives.WriteUInt64BigEndian(expectedEncoding.AsSpan(0), 0x0102030405060708UL);
        BinaryPrimitives.WriteUInt64BigEndian(expectedEncoding.AsSpan(8), 0x1112131415161718UL);
        BinaryPrimitives.WriteInt64BigEndian(expectedEncoding.AsSpan(16), 0x2122232425262728L);
        BinaryPrimitives.WriteInt64BigEndian(expectedEncoding.AsSpan(24), 0x3132333435363738L);
        BinaryPrimitives.WriteInt64BigEndian(expectedEncoding.AsSpan(32), 0x4142434445464748L);
        BinaryPrimitives.WriteInt64BigEndian(expectedEncoding.AsSpan(40), 0x5152535455565758L);
        BinaryPrimitives.WriteInt64BigEndian(expectedEncoding.AsSpan(48), 0x6162636465666768L);
        Assert.Equal(56, encoded.Length);
        Assert.Equal(expectedEncoding, encoded);

        var linuxArm64Bytes = (byte[])linuxX64Bytes.Clone();
        linuxArm64Bytes.AsSpan(16, 12).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(linuxArm64Bytes.AsSpan(16), 0x000041c0U);
        BinaryPrimitives.WriteUInt32LittleEndian(linuxArm64Bytes.AsSpan(20), 0xf0000002U);
        BinaryPrimitives.WriteUInt32LittleEndian(linuxArm64Bytes.AsSpan(24), 0xb1c2d3e4U);
        var linuxArm64 = TransferV3Posix.DecodeFileStatSnapshot(
            linuxArm64Bytes,
            linux: true,
            macOs: false,
            Architecture.Arm64);
        Assert.Equal(0x000041c0U, linuxArm64.Mode);
        Assert.Equal(0xf0000002UL, linuxArm64.LinkCount);
        Assert.Equal(0xb1c2d3e4U, linuxArm64.OwnerUid);
        Assert.Equal(linuxX64.Fingerprint, linuxArm64.Fingerprint);

        var macOsArm64Bytes = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(macOsArm64Bytes.AsSpan(0), 0x05060708U);
        BinaryPrimitives.WriteUInt16LittleEndian(macOsArm64Bytes.AsSpan(4), 0x8180);
        BinaryPrimitives.WriteUInt16LittleEndian(macOsArm64Bytes.AsSpan(6), 0xfffe);
        BinaryPrimitives.WriteUInt64LittleEndian(macOsArm64Bytes.AsSpan(8), 0x1112131415161718UL);
        BinaryPrimitives.WriteUInt32LittleEndian(macOsArm64Bytes.AsSpan(16), 0xc1d2e3f4U);
        BinaryPrimitives.WriteInt64LittleEndian(macOsArm64Bytes.AsSpan(48), 0x3132333435363738L);
        BinaryPrimitives.WriteInt64LittleEndian(macOsArm64Bytes.AsSpan(56), 0x4142434445464748L);
        BinaryPrimitives.WriteInt64LittleEndian(macOsArm64Bytes.AsSpan(64), 0x5152535455565758L);
        BinaryPrimitives.WriteInt64LittleEndian(macOsArm64Bytes.AsSpan(72), 0x6162636465666768L);
        BinaryPrimitives.WriteInt64LittleEndian(macOsArm64Bytes.AsSpan(96), 0x2122232425262728L);
        var macOsArm64 = TransferV3Posix.DecodeFileStatSnapshot(
            macOsArm64Bytes,
            linux: false,
            macOs: true,
            Architecture.Arm64);
        Assert.Equal(0x8180U, macOsArm64.Mode);
        Assert.Equal(0xfffeUL, macOsArm64.LinkCount);
        Assert.Equal(0xc1d2e3f4U, macOsArm64.OwnerUid);
        Assert.Equal(0x05060708UL, macOsArm64.Fingerprint.Device);
        Assert.Equal(linuxX64.Fingerprint with { Device = 0x05060708UL }, macOsArm64.Fingerprint);

        Assert.Throws<PlatformNotSupportedException>(() =>
            TransferV3Posix.DecodeFileStatSnapshot(
                macOsArm64Bytes,
                linux: false,
                macOs: true,
                Architecture.X64));
        Assert.Throws<InvalidDataException>(() =>
            TransferV3Posix.DecodeFileStatSnapshot(
                new byte[8],
                linux: true,
                macOs: false,
                Architecture.X64));
    }

    [Fact]
    public void PosixAvailableBytesSnapshot_UsesExactVerifiedLayoutsAndFailsClosed()
    {
        var linux = new byte[112];
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(8), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(24), ulong.MaxValue); // f_bfree: ignored
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(32), 7);
        Assert.Equal(28_672, TransferV3Posix.DecodeAvailableBytesSnapshot(
            linux,
            linux: true,
            macOs: false,
            Architecture.X64));
        Assert.Equal(28_672, TransferV3Posix.DecodeAvailableBytesSnapshot(
            linux,
            linux: true,
            macOs: false,
            Architecture.Arm64));

        var macOs = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(macOs.AsSpan(8), 8192);
        BinaryPrimitives.WriteUInt64LittleEndian(macOs.AsSpan(16), ulong.MaxValue); // f_bfree: ignored
        BinaryPrimitives.WriteUInt32LittleEndian(macOs.AsSpan(24), 9);
        Assert.Equal(73_728, TransferV3Posix.DecodeAvailableBytesSnapshot(
            macOs,
            linux: false,
            macOs: true,
            Architecture.Arm64));

        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(32), 0);
        Assert.Equal(0, TransferV3Posix.DecodeAvailableBytesSnapshot(
            linux,
            linux: true,
            macOs: false,
            Architecture.X64));

        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() =>
            TransferV3Posix.DecodeAvailableBytesSnapshot(
                linux,
                linux: true,
                macOs: false,
                Architecture.X64));
        Assert.Throws<InvalidDataException>(() =>
            TransferV3Posix.DecodeAvailableBytesSnapshot(
                new byte[111],
                linux: true,
                macOs: false,
                Architecture.X64));
        Assert.Throws<InvalidDataException>(() =>
            TransferV3Posix.DecodeAvailableBytesSnapshot(
                new byte[63],
                linux: false,
                macOs: true,
                Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() =>
            TransferV3Posix.DecodeAvailableBytesSnapshot(
                macOs,
                linux: false,
                macOs: true,
                Architecture.X64));

        linux.AsSpan().Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(linux.AsSpan(32), 2);
        Assert.Throws<OverflowException>(() =>
            TransferV3Posix.DecodeAvailableBytesSnapshot(
                linux,
                linux: true,
                macOs: false,
                Architecture.X64));
    }

    [Fact]
    public void PosixGetFingerprint_DelegatesToTypedFileStatSnapshot()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3Posix.cs"));
        var start = source.IndexOf(
            "internal static TransferV3FileFingerprint GetFingerprint",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "internal static void ThrowPrimaryAndCleanup",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.Contains("GetFileStat(handle).Fingerprint", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Fstat(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupEvidence_PreservesExactPrimaryAndExcludesCleanupExceptionText()
    {
        var primary = new IOException("stable-primary");
        var cleanup = new IOException("cleanup-secret-must-not-escape");

        var caught = Record.Exception(() =>
            TransferV3Posix.ThrowPrimaryAndCleanup(
                primary,
                [cleanup],
                "ignored-cleanup-message"));

        Assert.Same(primary, caught);
        AssertCleanupCodes(primary, "cleanup-failed");
        Assert.DoesNotContain(
            "cleanup-secret-must-not-escape",
            primary.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ignored-cleanup-message",
            primary.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupEvidence_HostilePrimaryDataCannotReplaceExactPrimary()
    {
        var primary = new HostileDataException("stable-hostile-primary");

        var caught = Record.Exception(() =>
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(
                primary,
                ["cleanup-failed"]));

        Assert.Same(primary, caught);
        Assert.DoesNotContain(
            "hostile-data-secret",
            primary.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableCloseState_BlocksReadersUntilCloseCompletesAndPreservesFailure()
    {
        var state = new TransferV3DurableCloseState();
        using var closeEntered = new ManualResetEventSlim();
        using var allowClose = new ManualResetEventSlim();
        using var snapshotStarted = new ManualResetEventSlim();
        var injected = new IOException("Injected durable-close failure.");

        var closeTask = Task.Run(() => Record.Exception((Action)(() =>
            state.ExecuteClose(() =>
            {
                closeEntered.Set();
                allowClose.Wait();
                throw injected;
            }))));
        Assert.True(closeEntered.Wait(TimeSpan.FromSeconds(5)));

        var snapshotTask = Task.Run(() =>
        {
            snapshotStarted.Set();
            return state.GetSnapshot();
        });
        Assert.True(snapshotStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(SpinWait.SpinUntil(
            () => snapshotTask.IsCompleted,
            TimeSpan.FromMilliseconds(250)));

        allowClose.Set();
        Assert.Same(injected, await closeTask);
        var snapshot = await snapshotTask;
        Assert.True(snapshot.Completed);
        Assert.Same(injected, snapshot.Failure);

        var unexpectedCloseCalls = 0;
        state.ExecuteClose(() => unexpectedCloseCalls++);
        Assert.Equal(0, unexpectedCloseCalls);
    }

    [Fact]
    public void UnsupportedPlatforms_FailClosedBeforeCreatingOutput()
    {
        if (IsSupportedPosix())
        {
            return;
        }

        var output = Path.Combine(Path.GetTempPath(), $"nzbdav-transfer-{Guid.NewGuid():N}");
        Assert.Throws<PlatformNotSupportedException>(() =>
            TransferV3SnapshotDirectory.CreateNew(output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task FinalizedSnapshot_UsesPrivateModesAndWritesManifestLast()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using (var snapshot = TransferV3SnapshotDirectory.CreateNew(output))
        {
            Assert.Equal(Path.GetFullPath(output), snapshot.RootPath);
            using (var table = snapshot.CreateFile("DavItems.jsonl"))
            {
                table.Write("private-row\n"u8);
                table.Flush();
            }

            Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
            await snapshot.WriteManifestAsync("{\"version\":3}"u8.ToArray());
            Assert.Throws<InvalidOperationException>(() => snapshot.CreateFile("late.jsonl"));
        }

        Assert.True(Directory.Exists(output));
        Assert.Equal("private-row\n", await File.ReadAllTextAsync(Path.Combine(output, "DavItems.jsonl")));
        Assert.Equal("{\"version\":3}", await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.All(
            Directory.EnumerateFiles(output),
            path => Assert.Equal((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(output) & PermissionBits);
            foreach (var path in Directory.EnumerateFiles(output))
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path) & PermissionBits);
            }
        }
    }

    [Fact]
    public void CreateNew_RejectsEveryExistingOutputKindWithoutFollowingSymlinks()
    {
        using var parent = new TemporaryDirectory();
        var directory = Path.Combine(parent.Path, "directory");
        Directory.CreateDirectory(directory);
        var directoryFailure = Assert.Throws<IOException>(() =>
            TransferV3SnapshotDirectory.CreateNew(directory));
        var residuePath = Assert.IsType<string>(
            directoryFailure.Data[TransferV3SnapshotDirectory.EmptyResiduePathDataKey]);
        Assert.True(Directory.Exists(residuePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(residuePath));
        AssertPrivateStagingResiduesAreEmptyAndPrivate(parent.Path);

        var file = Path.Combine(parent.Path, "file");
        File.WriteAllText(file, "sentinel");
        Assert.Throws<IOException>(() => TransferV3SnapshotDirectory.CreateNew(file));
        Assert.Equal("sentinel", File.ReadAllText(file));
        AssertPrivateStagingResiduesAreEmptyAndPrivate(parent.Path);

        if (!OperatingSystem.IsWindows())
        {
            var target = Path.Combine(parent.Path, "target");
            Directory.CreateDirectory(target);
            var link = Path.Combine(parent.Path, "link");
            Directory.CreateSymbolicLink(link, target);
            Assert.Throws<IOException>(() => TransferV3SnapshotDirectory.CreateNew(link));
            Assert.True(Directory.Exists(target));
            AssertPrivateStagingResiduesAreEmptyAndPrivate(parent.Path);

            var linkedParent = Path.Combine(parent.Path, "linked-parent");
            Directory.CreateSymbolicLink(linkedParent, target);
            Assert.Throws<IOException>(() =>
                TransferV3SnapshotDirectory.CreateNew(Path.Combine(linkedParent, "snapshot")));
            AssertPrivateStagingResiduesAreEmptyAndPrivate(parent.Path);
        }
    }

    [Fact]
    public void CreateNew_RejectsSymlinkInAnyAncestorNotOnlyImmediateParent()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var physical = Path.Combine(parent.Path, "physical");
        var nested = Path.Combine(physical, "nested");
        Directory.CreateDirectory(nested);
        var alias = Path.Combine(parent.Path, "alias");
        Directory.CreateSymbolicLink(alias, physical);

        Assert.Throws<IOException>(() => TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(alias, "nested", "snapshot")));
        Assert.False(Directory.Exists(Path.Combine(nested, "snapshot")));
    }

    [Theory]
    [InlineData("../escape.jsonl")]
    [InlineData("sub/file.jsonl")]
    [InlineData("sub\\file.jsonl")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("manifest.json")]
    [InlineData("/absolute.jsonl")]
    [InlineData("")]
    public void CreateFile_RejectsTraversalAbsoluteAndReservedNames(string name)
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);

        Assert.Throws<ArgumentException>(() => snapshot.CreateFile(name));
        Assert.Empty(Directory.EnumerateFiles(output));
        Assert.False(File.Exists(Path.Combine(parent.Path, "escape.jsonl")));
    }

    [Fact]
    public async Task ManifestCreation_IsCreateNewAndCleansItsTempOnFailure()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        var manifest = Path.Combine(output, "manifest.json");
        await File.WriteAllTextAsync(manifest, "sentinel");

        _ = await Assert.ThrowsAsync<IOException>(() =>
            snapshot.WriteManifestAsync("replacement"u8.ToArray()).AsTask());

        Assert.Equal("sentinel", await File.ReadAllTextAsync(manifest));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output),
            path => Path.GetFileName(path).Contains(".tmp", StringComparison.Ordinal));

        var cleanupFailure = Assert.ThrowsAny<Exception>(snapshot.Dispose);
        AssertCleanupCodes(cleanupFailure, "unknown-entry-residue");
        Assert.Equal("sentinel", await File.ReadAllTextAsync(manifest));
    }

    [Fact]
    public async Task ManifestCancellation_CleansOExclTempAndLeavesSnapshotUnfinalized()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            snapshot.WriteManifestAsync(new byte[1024 * 1024], cancellation.Token).AsTask());

        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.Empty(Directory.EnumerateFiles(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedDescriptorDuplicationFailure_RemovesIdentityTrackedFileAndPreservesPrimary(
        bool manifest)
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var injected = new IOException("stable-duplicate-primary");
        var hooks = new TransferV3SnapshotDirectoryHooks(
            DuplicateHandle: _ => throw injected);
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output, hooks);

        Exception caught;
        if (manifest)
        {
            caught = await Assert.ThrowsAsync<IOException>(() =>
                snapshot.WriteManifestAsync("manifest"u8.ToArray()).AsTask());
        }
        else
        {
            caught = Assert.Throws<IOException>(() => snapshot.CreateFile("data.jsonl"));
        }

        Assert.Same(injected, caught);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        snapshot.Dispose();
        Assert.Equal(output, snapshot.CleanupResiduePath);
    }

    [Fact]
    public async Task ManifestTemporaryFailure_ReportsExternalAliasPreservesReplacementAndPrimary()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var alias = Path.Combine(parent.Path, "secret-temporary-alias.jsonl");
        string? replacement = null;
        var injected = new IOException("stable-temporary-primary");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterManifestTemporaryCreated)
                return;
            replacement = Directory.EnumerateFileSystemEntries(output).Single();
            File.Move(replacement, alias);
            File.WriteAllText(replacement, "secret-temporary-replacement");
            throw injected;
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output, hooks);

        var caught = await Assert.ThrowsAnyAsync<IOException>(() =>
            snapshot.WriteManifestAsync("manifest"u8.ToArray()).AsTask());

        Assert.Same(injected, caught);
        AssertCleanupCodes(
            caught,
            "unknown-entry-residue",
            "external-hard-link-residue");
        Assert.NotNull(replacement);
        Assert.Equal("secret-temporary-replacement", File.ReadAllText(replacement));
        Assert.True(File.Exists(alias));
        Assert.DoesNotContain("secret-temporary-alias.jsonl", caught.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-temporary-replacement", caught.ToString(), StringComparison.Ordinal);

        var cleanupFailure = Assert.ThrowsAny<Exception>(snapshot.Dispose);
        AssertCleanupCodes(cleanupFailure, "unknown-entry-residue");
    }

    [Fact]
    public async Task ManifestPublishedFailure_RemovesOwnedAliasPreservesReplacementAndPrimary()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var manifest = Path.Combine(output, "manifest.json");
        var alias = Path.Combine(output, "secret-published-alias.jsonl");
        var injected = new IOException("stable-published-primary");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished)
                return;
            File.Move(manifest, alias);
            File.WriteAllText(manifest, "secret-published-replacement");
            throw injected;
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output, hooks);

        var caught = await Assert.ThrowsAnyAsync<IOException>(() =>
            snapshot.WriteManifestAsync("manifest"u8.ToArray()).AsTask());

        Assert.Same(injected, caught);
        AssertCleanupCodes(caught, "unknown-entry-residue");
        Assert.Equal("secret-published-replacement", File.ReadAllText(manifest));
        Assert.False(File.Exists(alias));
        Assert.DoesNotContain("secret-published-alias.jsonl", caught.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-published-replacement", caught.ToString(), StringComparison.Ordinal);

        var cleanupFailure = Assert.ThrowsAny<Exception>(snapshot.Dispose);
        AssertCleanupCodes(cleanupFailure, "unknown-entry-residue");
    }

    [Fact]
    public void Dispose_RemovesSensitiveContentsButLeavesEmptyOwnedRootFailClosed()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("partial.jsonl"))
        {
            file.WriteByte(1);
        }

        snapshot.Dispose();

        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal(output, snapshot.CleanupResiduePath);
        Assert.True(Directory.Exists(parent.Path));
    }

    [Fact]
    public void DuplicateCreateFile_NeverUnlinksOrReplacesExistingPrivateFile()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var original = snapshot.CreateFile("table.jsonl"))
        {
            original.Write("sentinel"u8);
        }

        Assert.Throws<InvalidOperationException>(() => snapshot.CreateFile("table.jsonl"));
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(output, "table.jsonl")));
    }

    [Fact]
    public async Task Manifest_CannotBeOverwrittenAfterSuccessfulPublication()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        await snapshot.WriteManifestAsync("first"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.WriteManifestAsync("second"u8.ToArray()).AsTask());

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
    }

    [Fact]
    public async Task Manifest_RefusesPublicationUntilEveryOwnedTableStreamIsDisposed()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        var table = snapshot.CreateFile("open.jsonl");
        table.Write("partial"u8);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.WriteManifestAsync("manifest"u8.ToArray()).AsTask());
        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));

        table.Flush();
        table.Dispose();
        await snapshot.WriteManifestAsync("manifest"u8.ToArray());
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
    }

    [Fact]
    public void Dispose_ClosesOwnedOpenStreamsAndRemovesTheirSensitiveContents()
    {
        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        var table = snapshot.CreateFile("open.jsonl");
        table.WriteByte(1);

        snapshot.Dispose();

        Assert.False(table.CanWrite);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal(output, snapshot.CleanupResiduePath);
    }

    [Fact]
    public void IdentityAwareCleanup_RemovesRenamedOwnedAliasButPreservesUnknownReplacement()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var original = Path.Combine(output, "owned.jsonl");
        var alias = Path.Combine(output, "owned-alias.jsonl");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }

        File.Move(original, alias);
        File.WriteAllText(original, "unknown-replacement-secret");

        var failure = Assert.ThrowsAny<Exception>(snapshot.Dispose);

        Assert.Equal("unknown-replacement-secret", File.ReadAllText(original));
        Assert.False(File.Exists(alias));
        Assert.Null(snapshot.CleanupResiduePath);
        AssertCleanupCodes(failure, "unknown-entry-residue");
        Assert.DoesNotContain("unknown-replacement-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("owned.jsonl", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityAwareCleanup_RemovesEveryInRootHardLinkForOwnedIdentity()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var original = Path.Combine(output, "owned.jsonl");
        var firstAlias = Path.Combine(output, "first-alias.jsonl");
        var secondAlias = Path.Combine(output, "second-alias.jsonl");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }

        File.Move(original, firstAlias);
        CreateHardLink(firstAlias, secondAlias);

        snapshot.Dispose();

        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal(output, snapshot.CleanupResiduePath);
    }

    [Fact]
    public void IdentityAwareCleanup_NonAsciiUnknownEntryDoesNotStrandOwnedDataAndDisposeCanRetry()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var owned = Path.Combine(output, "owned.jsonl");
        var unknown = Path.Combine(output, "café");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }
        File.WriteAllText(unknown, "unknown-secret");

        var firstFailure = Assert.ThrowsAny<Exception>(snapshot.Dispose);

        Assert.False(File.Exists(owned));
        Assert.Equal("unknown-secret", File.ReadAllText(unknown));
        Assert.Null(snapshot.CleanupResiduePath);
        AssertCleanupCodes(firstFailure, "unknown-entry-residue");
        Assert.DoesNotContain("unknown-secret", firstFailure.ToString(), StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => snapshot.CreateFile("late.jsonl"));

        File.Delete(unknown);
        snapshot.Dispose();

        Assert.Equal(output, snapshot.CleanupResiduePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        snapshot.Dispose();
    }

    [Fact]
    public void IdentityAwareCleanup_RemovesOwnedMaximumLengthAsciiName()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var original = Path.Combine(output, "owned.jsonl");
        var maximumName = new string('a', 255);
        var alias = Path.Combine(output, maximumName);
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }
        File.Move(original, alias);

        snapshot.Dispose();

        Assert.Equal(output, snapshot.CleanupResiduePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void IdentityAwareCleanup_RemovesUnicodeOwnedAlias()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var original = Path.Combine(output, "owned.jsonl");
        var alias = Path.Combine(output, "café-owned-alias.jsonl");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }
        File.Move(original, alias);

        snapshot.Dispose();

        Assert.False(File.Exists(alias));
        Assert.Equal(output, snapshot.CleanupResiduePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void IdentityAwareCleanup_ReportsExternalHardLinkWithoutDeletingIt()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var owned = Path.Combine(output, "owned.jsonl");
        var external = Path.Combine(parent.Path, "external-owned-alias.jsonl");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("external-hard-link-secret"u8);
        }
        CreateHardLink(owned, external);
        File.Delete(owned);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));

        var failure = Assert.ThrowsAny<Exception>(snapshot.Dispose);

        Assert.Equal("external-hard-link-secret", File.ReadAllText(external));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal(output, snapshot.CleanupResiduePath);
        AssertCleanupCodes(failure, "external-hard-link-residue");
        Assert.DoesNotContain("external-hard-link-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("external-owned-alias.jsonl", failure.ToString(), StringComparison.Ordinal);

        File.Delete(external);
        snapshot.Dispose();

        Assert.Equal(output, snapshot.CleanupResiduePath);
    }

    [Fact]
    public void IdentityAwareCleanup_LeavesSymlinkReplacementUntouched()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var owned = Path.Combine(output, "owned.jsonl");
        var target = Path.Combine(parent.Path, "outside-target");
        File.WriteAllText(target, "outside-secret");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }

        File.Delete(owned);
        File.CreateSymbolicLink(owned, target);

        var failure = Assert.ThrowsAny<Exception>(snapshot.Dispose);

        Assert.NotNull(File.ResolveLinkTarget(owned, returnFinalTarget: false));
        Assert.Equal("outside-secret", File.ReadAllText(target));
        Assert.Null(snapshot.CleanupResiduePath);
        AssertCleanupCodes(failure, "unknown-entry-residue");
        Assert.DoesNotContain("outside-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityAwareCleanup_LeavesFifoReplacementUntouchedWithoutBlocking()
    {
        if (!IsSupportedPosix()) return;

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var owned = Path.Combine(output, "owned.jsonl");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("owned.jsonl"))
        {
            file.Write("owned-secret"u8);
        }

        File.Delete(owned);
        CreateFifo(owned);

        var failure = Assert.ThrowsAny<Exception>(snapshot.Dispose);

        using var root = TransferV3Posix.OpenDirectory(output);
        Assert.True(TransferV3Posix.EntryExistsNoFollow(root, "owned.jsonl"));
        Assert.Null(snapshot.CleanupResiduePath);
        AssertCleanupCodes(failure, "unknown-entry-residue");
        Assert.DoesNotContain("owned.jsonl", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_NeverDeletesOrdinaryDirectoryThatReplacedOwnedRoot()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var moved = Path.Combine(parent.Path, "moved-owned-root");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("private.jsonl"))
        {
            file.Write("secret"u8);
        }

        Directory.Move(output, moved);
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "sentinel");
        File.WriteAllText(sentinel, "replacement");

        snapshot.Dispose();

        Assert.Equal("replacement", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(moved, "private.jsonl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(moved));
        Assert.Null(snapshot.CleanupResiduePath);
    }

    [Fact]
    public void Dispose_NeverFollowsSymlinkThatReplacedOwnedRoot()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var moved = Path.Combine(parent.Path, "moved-owned-root");
        var replacement = Path.Combine(parent.Path, "replacement-target");
        Directory.CreateDirectory(replacement);
        var sentinel = Path.Combine(replacement, "sentinel");
        File.WriteAllText(sentinel, "replacement");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        using (var file = snapshot.CreateFile("private.jsonl"))
        {
            file.Write("secret"u8);
        }

        Directory.Move(output, moved);
        Directory.CreateSymbolicLink(output, replacement);

        snapshot.Dispose();

        Assert.Equal("replacement", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(moved, "private.jsonl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(moved));
        Assert.Null(snapshot.CleanupResiduePath);
    }

    [Fact]
    public async Task PublicationFailsClosedWhenVisibleRootIdentityWasReplaced()
    {
        if (!IsSupportedPosix())
        {
            return;
        }

        using var parent = new TemporaryDirectory();
        var output = Path.Combine(parent.Path, "snapshot");
        var moved = Path.Combine(parent.Path, "moved-owned-root");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(output);
        Directory.Move(output, moved);
        Directory.CreateDirectory(output);

        await Assert.ThrowsAsync<IOException>(() =>
            snapshot.WriteManifestAsync("manifest"u8.ToArray()).AsTask());

        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(moved, "manifest.json")));
    }

    private const UnixFileMode PermissionBits =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private static bool IsSupportedPosix() => TransferV3Posix.IsSupported;

    private static void CreateHardLink(string existingPath, string linkPath)
    {
        if (CreateHardLinkNative(existingPath, linkPath) != 0)
        {
            throw new IOException(
                $"Could not create the synthetic hard-link fixture (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkNative(string existingPath, string linkPath);

    private static void CreateFifo(string path)
    {
        if (CreateFifoNative(path, 0x180) != 0)
        {
            throw new IOException(
                $"Could not create the synthetic FIFO fixture (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateFifoNative(string path, uint mode);

    private static void AssertCleanupCodes(Exception failure, params string[] expected)
    {
        var codes = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            failure.Data["TransferV3CleanupCodes"]);
        Assert.Equal(expected, codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relativePath);
    }

    private static void AssertPrivateStagingResiduesAreEmptyAndPrivate(string parentPath)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(parentPath)
                     .Where(path => Path.GetFileName(path).StartsWith(
                         ".nzbdav-transfer-v3-",
                         StringComparison.Ordinal)))
        {
            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.EnumerateFileSystemEntries(path));
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(path) & PermissionBits);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                $".nzbdav-transfer-v3-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class HostileDataException(string message) : IOException(message)
    {
        public override System.Collections.IDictionary Data =>
            throw new InvalidOperationException("hostile-data-secret");
    }
}

// Legacy lifecycle tests use this test-only adapter so production has no weak
// CreateFile/WriteManifestAsync bypass around the verified output receipts.
internal static class TransferV3SnapshotDirectoryLegacyTestExtensions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        TransferV3SnapshotDirectory,
        State> States = new();

    internal static Stream CreateFile(
        this TransferV3SnapshotDirectory snapshot,
        string fileName)
    {
        var state = States.GetValue(
            snapshot,
            owner => new State(owner.CreateDataOutputFactory([fileName])));
        var output = state.Outputs.CreateAsync(fileName, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new CompletingOutputStream(output);
    }

    internal static ValueTask WriteManifestAsync(
        this TransferV3SnapshotDirectory snapshot,
        ReadOnlyMemory<byte> manifest,
        CancellationToken cancellationToken = default)
    {
        _ = States.GetValue(
            snapshot,
            owner => new State(owner.CreateDataOutputFactory([])));
        return snapshot.PublishManifestAsync(manifest, cancellationToken);
    }

    private sealed record State(ITransferV3TableOutputFactory Outputs);

    private sealed class CompletingOutputStream(ITransferV3TableOutput output) : Stream
    {
        private Stream Inner => output.Stream;

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;
        public override long Position
        {
            get => Inner.Position;
            set => Inner.Position = value;
        }

        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            Inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            Inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => Inner.Write(buffer);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            Exception? primary = null;
            try
            {
                output.CompleteDurablyAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                primary = exception;
            }

            try
            {
                output.DisposeAsync().GetAwaiter().GetResult();
            }
            catch when (primary is not null)
            {
                // The exact durable-close primary remains authoritative in the
                // old lifecycle tests; production exporters attach their own
                // sanitized cleanup evidence.
            }

            if (primary is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(primary)
                    .Throw();
            }
        }
    }
}

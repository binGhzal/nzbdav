using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

#pragma warning disable CA1416 // Unix modes are used only under the verified POSIX gate.

public sealed class TransferV3Phase4StagingParentTests
{
    private const uint DirectoryType = 0x4000;
    private const uint OwnerReadWriteExecute = 0x1c0;

    [Theory]
    [InlineData(0x1c0u)] // 0700
    [InlineData(0x1e8u)] // 0750
    [InlineData(0x1edu)] // 0755
    public void OpenOwned_AcceptsTrustedOwnerModes(uint permissionBits)
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        File.SetUnixFileMode(fixture.Path, (UnixFileMode)permissionBits);

        using var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        using var duplicate = parent.DuplicateHandle();

        Assert.Equal(TransferV3Posix.GetIdentity(duplicate), parent.Identity);
        Assert.True(TransferV3Posix.DescriptorHasCloseOnExec(duplicate));
        Assert.InRange(parent.GetAvailableBytes(), 0, long.MaxValue);

        var handleField = Assert.Single(
            typeof(TransferV3Phase4StagingParent)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SafeFileHandle));
        var retained = Assert.IsType<SafeFileHandle>(handleField.GetValue(parent));
        Assert.True(TransferV3Posix.DescriptorHasCloseOnExec(retained));
    }

    [Fact]
    public void TrustValidators_RejectTypeOwnerPermissionAndRetainedDrift()
    {
        var effectiveUserId = 1234u;
        var opened = Stat(
            new TransferV3FileIdentity(11, 22),
            DirectoryType | OwnerReadWriteExecute,
            effectiveUserId);
        TransferV3Phase4StagingParent.ValidateOwnedStat(opened, effectiveUserId);

        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.ValidateOwnedStat(
                opened with { Mode = 0x8000 | OwnerReadWriteExecute },
                effectiveUserId));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.ValidateOwnedStat(
                opened with { OwnerUid = effectiveUserId + 1 },
                effectiveUserId));
        foreach (var mode in new[]
                 {
                     DirectoryType | 0x0c0u,
                     DirectoryType | 0x140u,
                     DirectoryType | 0x180u,
                     DirectoryType | OwnerReadWriteExecute | 0x10u,
                     DirectoryType | OwnerReadWriteExecute | 0x02u,
                 })
        {
            AssertCode("phase4-posix", () =>
                TransferV3Phase4StagingParent.ValidateOwnedStat(
                    opened with { Mode = mode },
                    effectiveUserId));
        }

        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.ValidateRetainedStat(
                opened,
                opened with
                {
                    Fingerprint = opened.Fingerprint with { Inode = 23 },
                },
                effectiveUserId));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.ValidateRetainedStat(
                opened,
                opened with { OwnerUid = effectiveUserId + 1 },
                effectiveUserId));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.ValidateRetainedStat(
                opened,
                opened with { Mode = DirectoryType | 0x1e8u },
                effectiveUserId));

        TransferV3Phase4StagingParent.ValidateRetainedStat(
            opened,
            opened with
            {
                Fingerprint = opened.Fingerprint with
                {
                    Size = 999,
                    ModificationSeconds = 111,
                    ChangeSeconds = 222,
                },
                LinkCount = opened.LinkCount + 1,
            },
            effectiveUserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:\\not-posix")]
    [InlineData("/")]
    [InlineData("/tmp/nul\0component")]
    [InlineData("/tmp/./component")]
    [InlineData("/tmp/../component")]
    public void OpenOwned_RejectsInvalidLexicalPathsBeforeNativeWork(string? path)
    {
        AssertCode("phase4-argument", () =>
            TransferV3Phase4StagingParent.OpenOwned(path!));
    }

    [Fact]
    public void OpenOwned_MapsMissingFileSymlinkAndUntrustedDirectoryToPosix()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var file = Path.Combine(fixture.Path, "file");
        var target = Path.Combine(fixture.Path, "target");
        var symlink = Path.Combine(fixture.Path, "symlink");
        var untrusted = Path.Combine(fixture.Path, "untrusted");
        File.WriteAllText(file, "not-a-directory");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(symlink, target);
        Directory.CreateDirectory(untrusted);
        File.SetUnixFileMode(untrusted, (UnixFileMode)0x1ff); // 0777

        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(
                Path.Combine(fixture.Path, "missing")));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(file));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(symlink));
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(untrusted));

        File.SetUnixFileMode(untrusted, (UnixFileMode)0x1c0);
    }

    [Fact]
    public void RetainedOperations_FailClosedOnLiveModeDriftAndRecoverAfterExactRestore()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        using var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        try
        {
            File.SetUnixFileMode(fixture.Path, (UnixFileMode)0x1ff);
            AssertCode("phase4-posix", () => parent.GetAvailableBytes());
            AssertCode("phase4-posix", () => parent.DuplicateHandle());
        }
        finally
        {
            File.SetUnixFileMode(fixture.Path, (UnixFileMode)0x1c0);
        }

        using var duplicate = parent.DuplicateHandle();
        Assert.Equal(parent.Identity, TransferV3Posix.GetIdentity(duplicate));
    }

    [Fact]
    public void RetainedOperations_StayPinnedToTheOpenedDirectoryAfterPathReplacement()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var openedPath = Path.Combine(fixture.Path, "opened");
        var displacedPath = Path.Combine(fixture.Path, "displaced");
        Directory.CreateDirectory(openedPath);
        File.SetUnixFileMode(openedPath, (UnixFileMode)0x1c0);
        using var parent = TransferV3Phase4StagingParent.OpenOwned(openedPath);
        var openedIdentity = parent.Identity;

        Directory.Move(openedPath, displacedPath);
        Directory.CreateDirectory(openedPath);
        File.SetUnixFileMode(openedPath, (UnixFileMode)0x1c0);
        using var replacement = TransferV3Posix.OpenDirectory(openedPath);
        Assert.NotEqual(openedIdentity, TransferV3Posix.GetIdentity(replacement));

        using var duplicate = parent.DuplicateHandle();
        Assert.Equal(openedIdentity, TransferV3Posix.GetIdentity(duplicate));
        Assert.InRange(parent.GetAvailableBytes(), 0, long.MaxValue);
    }

    [Fact]
    public void Duplicate_OutlivesParentAndIdentityRemainsStableAfterDispose()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        var identity = parent.Identity;
        using var duplicate = parent.DuplicateHandle();

        parent.Dispose();
        parent.Dispose();

        Assert.Equal(identity, parent.Identity);
        Assert.Equal(identity, TransferV3Posix.GetIdentity(duplicate));
        AssertCode("phase4-argument", () => parent.DuplicateHandle());
        AssertCode("phase4-argument", () => parent.GetAvailableBytes());
    }

    [Fact]
    public async Task DuplicateCapacityAndDisposeStress_ProducesOnlySuccessOrFixedDisposedFailures()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        var identity = parent.Identity;
        var failures = new ConcurrentBag<Exception>();
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 100; index++)
            {
                try
                {
                    if (((worker + index) & 1) == 0)
                    {
                        using var duplicate = parent.DuplicateHandle();
                        Assert.Equal(identity, TransferV3Posix.GetIdentity(duplicate));
                    }
                    else
                    {
                        Assert.InRange(parent.GetAvailableBytes(), 0, long.MaxValue);
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
        })).ToArray();
        var disposer = Task.Run(() =>
        {
            start.Wait();
            parent.Dispose();
        });

        start.Set();
        await Task.WhenAll(workers.Append(disposer));

        Assert.All(failures, failure => Assert.Equal(
            "phase4-argument",
            Assert.IsType<TransferV3Phase4Exception>(failure).Code));
        Assert.Equal(identity, parent.Identity);
    }

    [Fact]
    public void SourceContract_RetainsNoPathAndUsesOneLockAcrossNativeOperationsAndDispose()
    {
        Assert.DoesNotContain(
            typeof(TransferV3Phase4StagingParent)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(string));

        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/Phase4/TransferV3Phase4StagingParent.cs"));
        Assert.Contains("DescriptorHasCloseOnExec", Method(source, "OpenOwned"), StringComparison.Ordinal);
        AssertOrdered(
            Method(source, "DuplicateHandle"),
            "lock (_gate)",
            "ThrowIfDisposed()",
            "ValidateRetainedStat(",
            "GetFileStat(_handle)",
            "TransferV3Posix.DuplicateHandle(_handle)",
            "DescriptorHasCloseOnExec(duplicate)",
            "duplicate = null",
            "return result");
        AssertOrdered(
            Method(source, "GetAvailableBytes"),
            "lock (_gate)",
            "ThrowIfDisposed()",
            "ValidateRetainedStat(",
            "GetFileStat(_handle)",
            "TransferV3Posix.GetAvailableBytes(_handle)");
        AssertOrdered(
            Method(source, "Dispose"),
            "lock (_gate)",
            "if (_disposed)",
            "_disposed = true",
            "_handle.Dispose()");
    }

    private static TransferV3FileStat Stat(
        TransferV3FileIdentity identity,
        uint mode,
        uint ownerUid) =>
        new(
            new TransferV3FileFingerprint(
                identity.Device,
                identity.Inode,
                Size: 1,
                ModificationSeconds: 2,
                ModificationNanoseconds: 3,
                ChangeSeconds: 4,
                ChangeNanoseconds: 5),
            mode,
            LinkCount: 2,
            OwnerUid: ownerUid);

    private static void AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }

    private static bool IsSupportedOrAssertFailClosed()
    {
        if (TransferV3Posix.IsSupported) return true;
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(
                "/nzbdav-transfer-v3-phase4-unsupported-parent-probe"));
        return false;
    }

    private static string Method(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"^\s*(?:internal|public)\s+[^\r\n]*\b{Regex.Escape(name)}\s*\(",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, name);
        var next = Regex.Match(
            source[(match.Index + match.Length)..],
            @"^\s*(?:internal|public)\s+[^\r\n]*\(",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var end = next.Success
            ? match.Index + match.Length + next.Index
            : source.Length;
        return source[match.Index..end];
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, fragment);
            previous = current;
        }
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                "nzbdav-transfer-v3-phase4-parent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.SetUnixFileMode(Path, (UnixFileMode)0x1c0);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            File.SetUnixFileMode(Path, (UnixFileMode)0x1c0);
            Directory.Delete(Path, recursive: true);
        }
    }
}

#pragma warning restore CA1416

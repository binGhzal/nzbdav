using NzbWebDAV.Database.Transfer;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // Every Unix mode call is guarded by the verified POSIX ABI gate.

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SealedSnapshotStageAdversarialTests
{
    private const string QueueBlobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(28)]
    public async Task CreateAsync_CancellationAtFixedCopyBoundariesPreservesTokenAndCleans(
        int targetOrdinal)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, ordinal) =>
            {
                if (point == TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied
                    && ordinal == targetOrdinal)
                {
                    cancellation.Cancel();
                }
            });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3SealedSnapshotStage.CreateAsync(
                fixture.Verified,
                fixture.StageParent,
                hooks,
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StageParent));
        fixture.Verified.VerifyUnchanged();
    }

    [Fact]
    public async Task CreateAsync_CallerCancellationPreservesTheExactPrimaryException()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException(
            "authoritative caller cancellation",
            innerException: null,
            cancellation.Token);
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, ordinal) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied
                    || ordinal != 0)
                {
                    return;
                }
                cancellation.Cancel();
                throw primary;
            });

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3SealedSnapshotStage.CreateAsync(
                fixture.Verified,
                fixture.StageParent,
                hooks,
                cancellation.Token));

        Assert.Same(primary, observed);
        Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
    }

    [Fact]
    public async Task CreateAsync_NonCancellationPrimaryIsNotMaskedByCleanupEvidence()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        var primary = new IOException("authoritative injected write failure");
        string? privateRoot = null;
        string? unknown = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, ordinal) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied
                    || ordinal != 0)
                {
                    return;
                }
                unknown = Path.Combine(
                    Assert.IsType<string>(privateRoot),
                    "injected-unknown-residue");
                File.WriteAllText(unknown, "preserve");
                throw primary;
            });

        try
        {
            var observed = await Assert.ThrowsAsync<IOException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Same(primary, observed);
            Assert.Contains(
                "owned-root-unlink-failed",
                Assert.IsAssignableFrom<IEnumerable<string>>(
                    observed.Data["TransferV3CleanupCodes"]));
            Assert.Equal("preserve", await File.ReadAllTextAsync(
                Assert.IsType<string>(unknown)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
        }
    }

    [Theory]
    [InlineData((int)TransferV3SealedSnapshotStageFaultPoint.AfterBlobFileClosed)]
    [InlineData((int)TransferV3SealedSnapshotStageFaultPoint.AfterFileSealed)]
    [InlineData((int)TransferV3SealedSnapshotStageFaultPoint.AfterDirectorySealed)]
    [InlineData((int)TransferV3SealedSnapshotStageFaultPoint.AfterRootSealed)]
    [InlineData((int)TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification)]
    public async Task CreateAsync_CancellationAcrossReconstructionAndPartialSealingCleans(
        int targetValue)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var target = (TransferV3SealedSnapshotStageFaultPoint)targetValue;
        string? privateRoot = null;
        var fired = false;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, _) =>
            {
                if (!fired && point == target)
                {
                    fired = true;
                    cancellation.Cancel();
                }
            });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3SealedSnapshotStage.CreateAsync(
                fixture.Verified,
                fixture.StageParent,
                hooks,
                cancellation.Token));

        Assert.True(fired);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StageParent));
    }

    [Theory]
    [InlineData("file", "file-binding")]
    [InlineData("directory", "directory-changed")]
    [InlineData("root", "root-binding")]
    public async Task CreateAsync_SpecialPermissionBitAtLastHookIsRejectedAndCleans(
        string targetKind,
        string expectedCode)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, _) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification)
                    return;
                var root = Assert.IsType<string>(privateRoot);
                var target = targetKind switch
                {
                    "file" => Path.Combine(
                        root,
                        fixture.Verified.Manifest.Tables[0].File),
                    "directory" => Path.Combine(root, "blobs"),
                    "root" => root,
                    _ => throw new InvalidOperationException(targetKind),
                };
                var mode = targetKind == "file"
                    ? (UnixFileMode)0x900 // 04400
                    : (UnixFileMode)0x340; // 01500
                File.SetUnixFileMode(target, mode);
            });

        try
        {
            var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Equal(expectedCode, failure.Code);
            Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_SealedFileStatMutationAtLastHookIsRejectedAndCleans()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, _) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification)
                    return;
                var table = Path.Combine(
                    Assert.IsType<string>(privateRoot),
                    fixture.Verified.Manifest.Tables[0].File);
                File.SetUnixFileMode(
                    table,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(table, UnixFileMode.UserRead);
            });

        try
        {
            var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Equal("file-changed", failure.Code);
            Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_RootReplacementAtLastHookIsRejectedWithoutDeletingReplacement()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        string? displaced = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, _) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification)
                    return;
                var visible = Assert.IsType<string>(privateRoot);
                displaced = visible + "-displaced";
                Directory.Move(visible, displaced);
                Directory.CreateDirectory(visible);
                File.SetUnixFileMode(visible, UnixFileMode.UserRead
                                              | UnixFileMode.UserWrite
                                              | UnixFileMode.UserExecute);
            });

        try
        {
            var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Equal("root-binding", failure.Code);
            Assert.Contains("owned-root-replaced", failure.CleanupCodes);
            Assert.True(Directory.Exists(Assert.IsType<string>(privateRoot)));
            Assert.True(Directory.Exists(Assert.IsType<string>(displaced)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
            DeleteTreeNoThrow(displaced);
        }
    }

    [Fact]
    public async Task CreateAsync_ComponentReplacementAtLastHookIsRejectedAndPreserved()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        string? replacementSentinel = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, _) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification)
                    return;
                var blobDirectory = Path.Combine(
                    Assert.IsType<string>(privateRoot),
                    "blobs");
                var owned = Path.Combine(blobDirectory, "aa");
                var displaced = Path.Combine(blobDirectory, "ee");
                File.SetUnixFileMode(
                    blobDirectory,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
                Directory.Move(owned, displaced);
                Directory.CreateDirectory(owned);
                File.SetUnixFileMode(
                    owned,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
                replacementSentinel = Path.Combine(owned, "replacement-sentinel");
                File.WriteAllText(replacementSentinel, "must-survive");
            });

        try
        {
            var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Equal("directory-changed", failure.Code);
            Assert.Contains("owned-root-unlink-failed", failure.CleanupCodes);
            Assert.Equal(
                "must-survive",
                await File.ReadAllTextAsync(Assert.IsType<string>(replacementSentinel)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_IdentityProvenHardLinkAliasIsRejectedAndFullyCleaned()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var tableName = fixture.Verified.Manifest.Tables[0].File;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: value => privateRoot = value,
            AfterFaultPoint: (point, ordinal) =>
            {
                if (point != TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied
                    || ordinal != 0)
                {
                    return;
                }
                var root = Assert.IsType<string>(privateRoot);
                CreateHardLink(
                    Path.Combine(root, tableName),
                    Path.Combine(root, "identity-proven-alias"));
            });

        try
        {
            var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
                TransferV3SealedSnapshotStage.CreateAsync(
                    fixture.Verified,
                    fixture.StageParent,
                    hooks));

            Assert.Equal("file-binding", failure.Code);
            Assert.Empty(failure.CleanupCodes);
            Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        }
        finally
        {
            DeleteTreeNoThrow(privateRoot);
        }
    }

    [Fact]
    public async Task TypedReadRejectsSealedComponentModeMutationAndDisposalStillCleans()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: value => privateRoot = value));
        var firstShard = Path.Combine(
            Assert.IsType<string>(privateRoot),
            "blobs",
            "aa");
        File.SetUnixFileMode(
            firstShard,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(() =>
            stage.OpenBlobRead(Guid.ParseExact(QueueBlobId, "D")));
        Assert.Equal("directory-changed", failure.Code);

        stage.Dispose();
        Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
    }

    [Fact]
    public async Task Dispose_ReportsUnknownResidueWithoutDeletingIt()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: value => privateRoot = value));
        var root = Assert.IsType<string>(privateRoot);
        var unknown = Path.Combine(root, "unknown-residue");
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(unknown, "must-survive");

        try
        {
            var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(stage.Dispose);
            Assert.Equal("cleanup", failure.Code);
            Assert.Contains("owned-root-unlink-failed", failure.CleanupCodes);
            Assert.Equal("must-survive", await File.ReadAllTextAsync(unknown));
        }
        finally
        {
            DeleteTreeNoThrow(root);
        }
    }

    [Fact]
    public async Task Dispose_PreservesUnknownReplacementAndRemovesIdentityProvenAlias()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: value => privateRoot = value));
        var root = Assert.IsType<string>(privateRoot);
        var name = fixture.Verified.Manifest.Tables[0].File;
        var original = Path.Combine(root, name);
        var displaced = Path.Combine(root, "displaced-owned-file");
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(original, displaced);
        await File.WriteAllTextAsync(original, "replacement");

        try
        {
            var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(stage.Dispose);
            Assert.Equal("cleanup", failure.Code);
            Assert.Contains("owned-file-replaced", failure.CleanupCodes);
            Assert.Equal("replacement", await File.ReadAllTextAsync(original));
            Assert.False(File.Exists(displaced));
        }
        finally
        {
            DeleteTreeNoThrow(root);
        }
    }

    [Fact]
    public async Task Dispose_ReportsExternalHardLinkResidueAfterOwnedTreeIsRemoved()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: value => privateRoot = value));
        var root = Assert.IsType<string>(privateRoot);
        var externalAlias = Path.Combine(fixture.StageParent, "external-owned-alias");
        CreateHardLink(
            Path.Combine(root, fixture.Verified.Manifest.Tables[0].File),
            externalAlias);

        try
        {
            var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(stage.Dispose);
            Assert.Equal("cleanup", failure.Code);
            Assert.Contains("external-hard-link-residue", failure.CleanupCodes);
            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(externalAlias));
        }
        finally
        {
            if (File.Exists(externalAlias)) File.Delete(externalAlias);
            DeleteTreeNoThrow(root);
        }
    }

    [Fact]
    public async Task Dispose_ReportsPossibleExternalHardLinkWhenOwnedNameWasRemoved()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: value => privateRoot = value));
        var root = Assert.IsType<string>(privateRoot);
        var owned = Path.Combine(root, fixture.Verified.Manifest.Tables[0].File);
        var externalAlias = Path.Combine(fixture.StageParent, "external-removed-name-alias");
        CreateHardLink(owned, externalAlias);
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Delete(owned);

        try
        {
            var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(stage.Dispose);
            Assert.Equal("cleanup", failure.Code);
            Assert.Contains("possible-external-hard-link-residue", failure.CleanupCodes);
            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(externalAlias));
        }
        finally
        {
            if (File.Exists(externalAlias)) File.Delete(externalAlias);
            DeleteTreeNoThrow(root);
        }
    }

    [Fact]
    public async Task CreateAsync_DoesNotRetainOneDescriptorPerBlob()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync(
                additionalBlobCount: 128);
        var descriptorDirectory = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
        var before = Directory.EnumerateFileSystemEntries(descriptorDirectory).Count();

        using var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent);
        var after = Directory.EnumerateFileSystemEntries(descriptorDirectory).Count();

        Assert.True(
            after <= before + 12,
            $"The sealed stage retained {after - before} descriptors for 131 blobs.");
        Assert.Equal(131, fixture.Blobs.Count);
    }

    [Fact]
    public async Task TypedReads_HaveABoundedLeaseLimitAndReleaseCapacityOnDispose()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent);
        var reads = new List<Stream>();
        try
        {
            for (var ordinal = 0; ordinal < 64; ordinal++)
                reads.Add(stage.OpenTableRead(0));

            var failure = Assert.Throws<TransferV3SealedSnapshotStageException>(() =>
                stage.OpenTableRead(0));
            Assert.Equal("read-limit", failure.Code);

            reads[0].Dispose();
            reads.RemoveAt(0);
            reads.Add(stage.OpenTableRead(0));
        }
        finally
        {
            stage.Dispose();
            foreach (var read in reads)
                Assert.Throws<ObjectDisposedException>(() => read.ReadByte());
        }
    }

    [Fact]
    public async Task ResidueAudit_ReportsRestartCandidatesWithoutOpeningOrDeletingTheirContents()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture =
            await TransferV3SealedSnapshotStageTests.StageFixture.CreateAsync();
        var candidate = Path.Combine(
            fixture.StageParent,
            ".nzbdav-transfer-v3-stage-" + new string('a', 32));
        Directory.CreateDirectory(candidate);
        File.SetUnixFileMode(
            candidate,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sentinel = Path.Combine(candidate, "unreviewed-sensitive-entry");
        await File.WriteAllTextAsync(sentinel, "do-not-open-or-delete");
        var unknown = Path.Combine(
            fixture.StageParent,
            ".nzbdav-transfer-v3-stage-" + new string('b', 32));
        await File.WriteAllTextAsync(unknown, "not-a-directory");
        var ignored = Path.Combine(
            fixture.StageParent,
            ".nzbdav-transfer-v3-stage-NOT-A-VALID-NONCE");
        await File.WriteAllTextAsync(ignored, "ignore");

        var audit = TransferV3SealedSnapshotStage.AuditResidues(fixture.StageParent);

        Assert.Equal(1, audit.CandidateDirectories);
        Assert.Equal(1, audit.UnknownPrefixedEntries);
        Assert.Equal(0, audit.UnreadableEntries);
        Assert.Equal("do-not-open-or-delete", await File.ReadAllTextAsync(sentinel));
        Assert.Equal("not-a-directory", await File.ReadAllTextAsync(unknown));
        Assert.Equal("ignore", await File.ReadAllTextAsync(ignored));
    }

    private static void DeleteTreeNoThrow(string? value)
    {
        if (value is null || !Directory.Exists(value)) return;
        try
        {
            File.SetUnixFileMode(
                value,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var directory in Directory.EnumerateDirectories(
                         value,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
            Directory.Delete(value, recursive: true);
        }
        catch
        {
            // The assertions retain the authoritative failure.
        }
    }

    private static void CreateHardLink(string existing, string alias)
    {
        if (CreateHardLinkNative(existing, alias) != 0)
        {
            throw new IOException(
                $"link(2) failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkNative(string existing, string alias);
}

#pragma warning restore CA1416

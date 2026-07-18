using System.Collections.Immutable;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class TransferV3SnapshotReaderTests
{
    private const string EmptyDigest =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public async Task OpenAsync_AcceptsExactPrivateSnapshotAndReturnsTypedOpaqueBoundary()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = SnapshotFixture.Create();

        using var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract,
            hooks: null,
            CancellationToken.None);

        Assert.Equal(
            fixture.ManifestBytes,
            TransferV3ManifestCodec.Serialize(snapshot.Manifest, fixture.Contract));
        Assert.Equal(
            TransferV3ManifestCodec.ComputeSha256(fixture.ManifestBytes),
            snapshot.ManifestSha256);
        Assert.Equal(fixture.ManifestBytes, snapshot.GetCanonicalManifestCopy());

        using var firstTable = snapshot.OpenTableRead(0);
        using var blobs = snapshot.OpenBlobBundleRead();
        Assert.Equal(fixture.TablePayload(0), await ReadAllAsync(firstTable));
        Assert.Equal(fixture.BlobPayload, await ReadAllAsync(blobs));

        var publicSurface = typeof(TransferV3PinnedSnapshot)
            .GetMethods(System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.DeclaringType == typeof(TransferV3PinnedSnapshot))
            .Select(method => method.Name)
            .ToArray();
        Assert.DoesNotContain(publicSurface, name =>
            name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FileName", StringComparison.OrdinalIgnoreCase)
            || name is "OpenRead");
    }

    [Theory]
    [InlineData("relative/snapshot")]
    [InlineData("/")]
    public async Task OpenAsync_RejectsNonAbsoluteOrRootPathBeforeOpeningSnapshot(
        string path)
    {
        if (!TransferV3Posix.IsSupported) return;

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                path,
                TransferV3SourceContract.LoadEmbedded()));

        Assert.Equal("path", failure.Code);
        if (path.Length > 1)
        {
            Assert.DoesNotContain(path, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsDotAndDotDotComponentsWithoutNormalizingThemAway()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = SnapshotFixture.Create();
        foreach (var path in new[]
                 {
                     fixture.RootPath + "/./child",
                     fixture.RootPath + "/../snapshot",
                 })
        {
            var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
                TransferV3SnapshotReader.OpenAsync(path, fixture.Contract));
            Assert.Equal("path", failure.Code);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsSymlinkedIntermediatePathComponent()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = SnapshotFixture.Create();
        var parent = Path.GetDirectoryName(fixture.RootPath)!;
        var alias = Path.Combine(parent, $"reader-parent-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(alias, parent);
        try
        {
            var aliasedRoot = Path.Combine(alias, Path.GetFileName(fixture.RootPath));
            var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
                TransferV3SnapshotReader.OpenAsync(aliasedRoot, fixture.Contract));
            Assert.Equal("open", failure.Code);
            Assert.DoesNotContain(aliasedRoot, failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public async Task OpenAsync_PreCanceledTokenIsPreservedExactly()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = SnapshotFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks: null,
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task OpenAsync_CancellationAfterRootOpenPreservesTokenAndClosesDescriptors()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var fixture = SnapshotFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point == TransferV3SnapshotReaderFaultPoint.AfterRootOpened)
            {
                cancellation.Cancel();
            }
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks,
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        using var root = TransferV3Posix.OpenDirectory(fixture.RootPath);
        Assert.Equal(29, TransferV3Posix.EnumerateDirectoryEntries(root).Count());
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        File.Delete(fixture.TablePath(0));

        await AssertReadFailureAsync(fixture, "entry-set");
    }

    [Fact]
    public async Task OpenAsync_RejectsEveryMissingOrRenamedFixedEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        for (var ordinal = 0; ordinal < TransferV3PinnedSnapshot.FixedFileCount; ordinal++)
        {
            using (var missing = SnapshotFixture.Create())
            {
                File.Delete(Path.Combine(
                    missing.RootPath,
                    FixedFileName(missing.Contract, ordinal)));
                await AssertReadFailureAsync(missing, "entry-set");
            }

            using (var renamed = SnapshotFixture.Create())
            {
                var path = Path.Combine(
                    renamed.RootPath,
                    FixedFileName(renamed.Contract, ordinal));
                File.Move(path, path + ".renamed");
                await AssertReadFailureAsync(renamed, "entry-set");
            }
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsExtraEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        fixture.WritePrivate("extra.jsonl", "extra"u8.ToArray());

        await AssertReadFailureAsync(fixture, "entry-set");
    }

    [Fact]
    public async Task OpenAsync_RejectsInvalidUtf8DirectoryEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        using var root = TransferV3Posix.OpenDirectory(fixture.RootPath);
        if (!TryCreateInvalidUtf8Entry(root)) return;
        try
        {
            await AssertReadFailureAsync(fixture, "entry-set");
        }
        finally
        {
            RemoveInvalidUtf8Entry(root);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsRootModeThatIsNotExactly0700()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        File.SetUnixFileMode(fixture.RootPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await AssertReadFailureAsync(fixture, "root-stat");
    }

    [Fact]
    public async Task OpenAsync_RejectsFileModeThatIsNotExactly0600()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        File.SetUnixFileMode(fixture.TablePath(0), UnixFileMode.UserRead);

        await AssertReadFailureAsync(fixture, "file-stat");
    }

    [Fact]
    public async Task OpenAsync_RejectsSymlinkAtExpectedName()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var path = fixture.TablePath(0);
        File.Delete(path);
        File.CreateSymbolicLink(path, fixture.TablePath(1));

        await AssertReadFailureAsync(fixture, "open");
    }

    [Fact]
    public async Task OpenAsync_RejectsFifoAtExpectedNameWithoutBlocking()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var path = fixture.TablePath(0);
        File.Delete(path);
        Assert.Equal(0, MkFifo(path, 0x180));

        await AssertReadFailureAsync(fixture, "open");
    }

    [Fact]
    public async Task OpenAsync_RejectsDirectoryAndUnixSocketAtExpectedName()
    {
        if (!TransferV3Posix.IsSupported) return;
        using (var directory = SnapshotFixture.Create())
        {
            var path = directory.TablePath(0);
            File.Delete(path);
            Directory.CreateDirectory(path);
            await AssertReadFailureAsync(directory, "open");
        }

        using (var socketFixture = SnapshotFixture.Create(useShortPath: true))
        {
            var path = socketFixture.TablePath(0);
            File.Delete(path);
            using var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            await AssertReadFailureAsync(socketFixture, "open");
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsHardLinkedExpectedFilesAndExternalHardLinks()
    {
        if (!TransferV3Posix.IsSupported) return;

        using (var fixture = SnapshotFixture.Create())
        {
            File.Delete(fixture.TablePath(1));
            CreateHardLink(fixture.TablePath(0), fixture.TablePath(1));
            await AssertReadFailureAsync(fixture, "file-stat");
        }

        using (var fixture = SnapshotFixture.Create())
        {
            CreateHardLink(
                fixture.TablePath(0),
                Path.Combine(Path.GetDirectoryName(fixture.RootPath)!, "external-link"));
            await AssertReadFailureAsync(fixture, "file-stat");
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsEmptyOversizedAndNonCanonicalManifest()
    {
        if (!TransferV3Posix.IsSupported) return;

        using (var fixture = SnapshotFixture.Create())
        {
            fixture.ReplacePrivate("manifest.json", []);
            await AssertReadFailureAsync(fixture, "manifest-size");
        }

        using (var fixture = SnapshotFixture.Create())
        {
            fixture.ReplacePrivate(
                "manifest.json",
                new byte[TransferV3ManifestCodec.MaxManifestBytes + 1]);
            await AssertReadFailureAsync(fixture, "manifest-size");
        }

        using (var fixture = SnapshotFixture.Create())
        {
            fixture.ReplacePrivate(
                "manifest.json",
                [.. fixture.ManifestBytes, (byte)'\n']);
            await AssertReadFailureAsync(fixture, "manifest");
        }
    }

    [Fact]
    public async Task OpenAsync_FreshVisibleBindingRejectsRootReplacement()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var replaced = false;
        var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point != TransferV3SnapshotReaderFaultPoint.AfterRootOpened) return;
            Directory.Move(fixture.RootPath, fixture.RootPath + ".retained");
            Directory.CreateDirectory(fixture.RootPath);
            File.SetUnixFileMode(
                fixture.RootPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            replaced = true;
        });

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks));

        Assert.True(replaced);
        Assert.Contains(failure.Code, new[] { "root-binding", "root-changed" });
    }

    [Fact]
    public async Task OpenAsync_FreshBindingIgnoresUnrelatedSiblingActivityInParent()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var sibling = Path.Combine(
            Path.GetDirectoryName(fixture.RootPath)!,
            $"reader-unrelated-sibling-{Guid.NewGuid():N}");
        var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point == TransferV3SnapshotReaderFaultPoint.AfterRootOpened)
            {
                File.WriteAllBytes(sibling, "unrelated"u8.ToArray());
            }
        });
        try
        {
            using var snapshot = await TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks);
            Assert.Equal(29, TransferV3PinnedSnapshot.FixedFileCount);
        }
        finally
        {
            File.Delete(sibling);
        }
    }

    [Fact]
    public async Task OpenAsync_FinalReceiptPassRejectsSameLengthMutationAfterManifestRead()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var mutated = false;
        var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point != TransferV3SnapshotReaderFaultPoint.AfterManifestRead) return;
            var bytes = File.ReadAllBytes(fixture.TablePath(0));
            bytes[0] ^= 0xff;
            File.WriteAllBytes(fixture.TablePath(0), bytes);
            File.SetUnixFileMode(
                fixture.TablePath(0),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            mutated = true;
        });

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks));

        Assert.True(mutated);
        Assert.Equal("file-changed", failure.Code);
    }

    [Fact]
    public async Task OpenAsync_HookFailureIsSanitizedAndNeverLeaksCallerPathOrSecret()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        const string secret = "private-reader-hook-secret";
        var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point == TransferV3SnapshotReaderFaultPoint.AfterInitialEnumeration)
            {
                throw new IOException(secret);
            }
        });

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
            TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks));

        Assert.Equal("open", failure.Code);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, failure.ToString(), StringComparison.Ordinal);
        Assert.All(failure.CleanupCodes, code =>
            Assert.DoesNotContain(secret, code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedStreams_UseIndependentOffsetExplicitReads()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        using var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);
        using var first = snapshot.OpenTableRead(0);
        using var second = snapshot.OpenTableRead(0);
        var expected = fixture.TablePayload(0);

        var firstPrefix = new byte[5];
        var secondPrefix = new byte[5];
        Assert.Equal(5, await first.ReadAsync(firstPrefix));
        Assert.Equal(5, await second.ReadAsync(secondPrefix));
        Assert.Equal(expected.AsSpan(0, 5).ToArray(), firstPrefix);
        Assert.Equal(firstPrefix, secondPrefix);

        first.Seek(3, SeekOrigin.Begin);
        var firstMiddle = new byte[4];
        var secondNext = new byte[4];
        Assert.Equal(4, await first.ReadAsync(firstMiddle));
        Assert.Equal(4, await second.ReadAsync(secondNext));
        Assert.Equal(expected.AsSpan(3, 4).ToArray(), firstMiddle);
        Assert.Equal(expected.AsSpan(5, 4).ToArray(), secondNext);
    }

    [Fact]
    public async Task RecordVerifiedReceipt_RequiresEveryExactFixedFileBeforeFinalVerification()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        using var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);

        var incomplete = Assert.Throws<TransferV3SnapshotReadException>(
            () => snapshot.VerifyUnchanged());
        Assert.Equal("verification-incomplete", incomplete.Code);

        var wrongDigest = new byte[SHA256.HashSizeInBytes];
        var wrong = Assert.Throws<TransferV3SnapshotReadException>(() =>
            snapshot.RecordVerifiedReceipt(
                0,
                fixture.TablePayload(0).Length,
                wrongDigest));
        Assert.Equal("verified-receipt", wrong.Code);

        await RecordAllReceiptsAsync(snapshot);
        snapshot.VerifyUnchanged();

        var duplicate = Assert.Throws<TransferV3SnapshotReadException>(() =>
            snapshot.RecordVerifiedReceipt(
                0,
                fixture.TablePayload(0).Length,
                SHA256.HashData(fixture.TablePayload(0))));
        Assert.Equal("verified-receipt", duplicate.Code);
    }

    [Fact]
    public async Task SetVerificationMetrics_RequiresAllReceiptsOpenStateAndIsSingleUse()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);
        var metrics = CreateVerificationMetrics();

        Assert.Null(snapshot.Metrics);

        var incomplete = Assert.Throws<TransferV3SnapshotReadException>(
            () => snapshot.SetVerificationMetrics(metrics));
        Assert.Equal("verification-incomplete", incomplete.Code);

        await RecordAllReceiptsAsync(snapshot);
        snapshot.SetVerificationMetrics(metrics);
        Assert.Same(metrics, snapshot.Metrics);

        var duplicate = Assert.Throws<TransferV3SnapshotReadException>(
            () => snapshot.SetVerificationMetrics(CreateVerificationMetrics()));
        Assert.Equal("verification-metrics", duplicate.Code);

        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => snapshot.SetVerificationMetrics(CreateVerificationMetrics()));
    }

    [Fact]
    public async Task Promotion_RequiresReceiptsMetricsAndFinalVerificationAndIsSingleUse()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        using var pinned = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);

        Assert.Throws<TransferV3SnapshotReadException>(() =>
            TransferV3VerifiedSnapshot.Promote(pinned));

        await RecordAllReceiptsAsync(pinned);
        pinned.SetVerificationMetrics(CreateVerificationMetrics());
        Assert.Throws<TransferV3SnapshotReadException>(() =>
            TransferV3VerifiedSnapshot.Promote(pinned));

        pinned.VerifyUnchanged();
        using var verified = TransferV3VerifiedSnapshot.Promote(pinned);
        Assert.NotNull(verified.Metrics);
        Assert.Equal("verification-transferred", Assert.Throws<TransferV3SnapshotReadException>(
            () => _ = pinned.Metrics).Code);
        Assert.Equal("verification-transferred", Assert.Throws<TransferV3SnapshotReadException>(
            () => pinned.OpenTableRead(0)).Code);
        Assert.Equal("verification-transferred", Assert.Throws<TransferV3SnapshotReadException>(
            () => pinned.VerifyUnchanged()).Code);
        Assert.Equal("verification-transferred", Assert.Throws<TransferV3SnapshotReadException>(
            () => TransferV3VerifiedSnapshot.Promote(pinned)).Code);

        pinned.Dispose();
        using var table = verified.OpenTableRead(0);
        Assert.True(table.Length > 0);
    }

    [Fact]
    public async Task ReaderHookCancellationAlwaysReturnsTheExactCallerToken()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();

        using (var openCancellation = new CancellationTokenSource())
        {
            var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
            {
                if (point != TransferV3SnapshotReaderFaultPoint.AfterRootOpened) return;
                openCancellation.Cancel();
                throw new OperationCanceledException();
            });
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TransferV3SnapshotReader.OpenAsync(
                    fixture.RootPath,
                    fixture.Contract,
                    hooks,
                    openCancellation.Token));
            Assert.Equal(openCancellation.Token, failure.CancellationToken);
        }

        using var verifyCancellation = new CancellationTokenSource();
        var verifyHooks = new TransferV3SnapshotReaderHooks((point, _) =>
        {
            if (point != TransferV3SnapshotReaderFaultPoint.BeforeVerifyUnchanged) return;
            verifyCancellation.Cancel();
            throw new OperationCanceledException();
        });
        using var pinned = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract,
            verifyHooks);
        await RecordAllReceiptsAsync(pinned);
        pinned.SetVerificationMetrics(CreateVerificationMetrics());

        var verifyFailure = Assert.ThrowsAny<OperationCanceledException>(() =>
            pinned.VerifyUnchanged(verifyCancellation.Token));
        Assert.Equal(verifyCancellation.Token, verifyFailure.CancellationToken);
    }

    [Fact]
    public async Task VerifyUnchanged_RejectsContentPathEntryModeAndRootMutations()
    {
        if (!TransferV3Posix.IsSupported) return;

        await AssertFinalMutationRejectedAsync(fixture =>
        {
            var bytes = File.ReadAllBytes(fixture.TablePath(0));
            bytes[^1] ^= 0xff;
            fixture.ReplacePrivate(SnapshotFixture.TableName(fixture.Contract, 0), bytes);
        });
        await AssertFinalMutationRejectedAsync(fixture =>
        {
            var path = fixture.TablePath(0);
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            fixture.WritePrivate(SnapshotFixture.TableName(fixture.Contract, 0), bytes);
        });
        await AssertFinalMutationRejectedAsync(fixture =>
            fixture.WritePrivate("late-extra", "late"u8.ToArray()));
        await AssertFinalMutationRejectedAsync(fixture =>
            File.SetUnixFileMode(fixture.TablePath(0), UnixFileMode.UserRead));
        await AssertFinalMutationRejectedAsync(fixture =>
        {
            Directory.Move(fixture.RootPath, fixture.RootPath + ".old");
            Directory.CreateDirectory(fixture.RootPath);
            File.SetUnixFileMode(
                fixture.RootPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        });
    }

    [Fact]
    public async Task VerifyUnchanged_FinalHookMutationIsDetectedAndCancellationIsExact()
    {
        if (!TransferV3Posix.IsSupported) return;

        using (var fixture = SnapshotFixture.Create())
        {
            var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
            {
                if (point == TransferV3SnapshotReaderFaultPoint.BeforeFinalVerifyUnchanged)
                {
                    fixture.WritePrivate("late-extra", "late"u8.ToArray());
                }
            });
            using var snapshot = await TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks);
            await RecordAllReceiptsAsync(snapshot);
            Assert.Throws<TransferV3SnapshotReadException>(() => snapshot.VerifyUnchanged());
        }

        using (var fixture = SnapshotFixture.Create())
        using (var cancellation = new CancellationTokenSource())
        {
            var hooks = new TransferV3SnapshotReaderHooks((point, _) =>
            {
                if (point == TransferV3SnapshotReaderFaultPoint.BeforeVerifyUnchanged)
                {
                    cancellation.Cancel();
                }
            });
            using var snapshot = await TransferV3SnapshotReader.OpenAsync(
                fixture.RootPath,
                fixture.Contract,
                hooks);
            await RecordAllReceiptsAsync(snapshot);
            var failure = Assert.ThrowsAny<OperationCanceledException>(() =>
                snapshot.VerifyUnchanged(cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
    }

    [Fact]
    public async Task Dispose_ClosesEveryRetainedHandleAndClearsSensitiveBuffers()
    {
        if (!TransferV3Posix.IsSupported) return;
        using var fixture = SnapshotFixture.Create();
        var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);
        var retainedField = typeof(TransferV3PinnedSnapshot).GetField(
            "_retained",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        var manifestField = typeof(TransferV3PinnedSnapshot).GetField(
            "_canonicalManifest",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(retainedField);
        Assert.NotNull(manifestField);
        var retained = Assert.IsType<System.Collections.Immutable.ImmutableArray<TransferV3RetainedSnapshotFile>>(
            retainedField.GetValue(snapshot));
        var manifestBuffer = Assert.IsType<byte[]>(manifestField.GetValue(snapshot));
        var initialDigestField = typeof(TransferV3RetainedSnapshotFile).GetField(
            "_initialRawSha256",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(initialDigestField);
        var initialDigests = retained
            .Select(file => Assert.IsType<byte[]>(initialDigestField.GetValue(file)))
            .ToArray();
        Assert.Equal(29, retained.Length);
        Assert.Equal(
            29,
            retained
                .Select(file => TransferV3Posix.GetIdentity(file.Handle))
                .Distinct()
                .Count());

        snapshot.Dispose();

        Assert.All(retained, file => Assert.True(file.Handle.IsClosed));
        Assert.All(initialDigests, digest =>
            Assert.All(digest, value => Assert.Equal(0, value)));
        Assert.All(manifestBuffer, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => snapshot.OpenTableRead(0));
        Assert.Throws<ObjectDisposedException>(() => snapshot.GetCanonicalManifestCopy());
    }

    [Fact]
    public void ReaderSource_UsesRandomAccessAndHasNoGenericPathOrFileStreamSurface()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SnapshotReader.cs"));

        Assert.Contains("RandomAccess.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Open", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAll", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Enumerate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgre", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
    }

    private static async Task AssertReadFailureAsync(
        SnapshotFixture fixture,
        string expectedCode)
    {
        var failure = await Assert.ThrowsAsync<TransferV3SnapshotReadException>(() =>
            TransferV3SnapshotReader.OpenAsync(fixture.RootPath, fixture.Contract));
        Assert.Equal(expectedCode, failure.Code);
        Assert.DoesNotContain(fixture.RootPath, failure.ToString(), StringComparison.Ordinal);
    }

    private static async Task RecordAllReceiptsAsync(
        TransferV3PinnedSnapshot snapshot)
    {
        for (var ordinal = 0; ordinal < TransferV3PinnedSnapshot.TableFileCount; ordinal++)
        {
            using var stream = snapshot.OpenTableRead(ordinal);
            var bytes = await ReadAllAsync(stream);
            snapshot.RecordVerifiedReceipt(ordinal, bytes.Length, SHA256.HashData(bytes));
        }

        using (var stream = snapshot.OpenBlobBundleRead())
        {
            var bytes = await ReadAllAsync(stream);
            snapshot.RecordVerifiedReceipt(
                TransferV3PinnedSnapshot.BlobFileOrdinal,
                bytes.Length,
                SHA256.HashData(bytes));
        }

        var manifest = snapshot.GetCanonicalManifestCopy();
        snapshot.RecordVerifiedReceipt(
            TransferV3PinnedSnapshot.ManifestFileOrdinal,
            manifest.Length,
            SHA256.HashData(manifest));
        CryptographicOperations.ZeroMemory(manifest);
    }

    private static TransferV3SnapshotVerificationMetrics CreateVerificationMetrics() =>
        new(
            ImmutableArray<TransferV3VerifiedTableMetrics>.Empty,
            new TransferV3VerifiedBlobMetrics(
                0,
                0,
                0,
                0,
                new TransferV3BufferMetrics(0, 0, 0, 0, 0)),
            new TransferV3BlobReferenceFactCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    private static async Task AssertFinalMutationRejectedAsync(
        Action<SnapshotFixture> mutate)
    {
        using var fixture = SnapshotFixture.Create();
        using var snapshot = await TransferV3SnapshotReader.OpenAsync(
            fixture.RootPath,
            fixture.Contract);
        await RecordAllReceiptsAsync(snapshot);
        mutate(fixture);
        Assert.Throws<TransferV3SnapshotReadException>(() => snapshot.VerifyUnchanged());
    }

    private static string FixedFileName(
        TransferV3SourceContract contract,
        int ordinal) =>
        ordinal switch
        {
            < TransferV3PinnedSnapshot.TableFileCount =>
                SnapshotFixture.TableName(contract, ordinal),
            TransferV3PinnedSnapshot.BlobFileOrdinal => "Blobs.jsonl",
            TransferV3PinnedSnapshot.ManifestFileOrdinal => "manifest.json",
            _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
        };

    private static string RepositoryPath(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    private static void CreateHardLink(string existingPath, string linkPath)
    {
        if (CreateHardLinkNative(existingPath, linkPath) != 0)
        {
            throw new IOException($"link failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkNative(string existingPath, string linkPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MkFifo(string path, uint mode);

    private static bool TryCreateInvalidUtf8Entry(
        Microsoft.Win32.SafeHandles.SafeFileHandle root)
    {
        var name = Marshal.AllocHGlobal(2);
        try
        {
            Marshal.WriteByte(name, 0, 0xff);
            Marshal.WriteByte(name, 1, 0);
            var flags = OperatingSystem.IsMacOS() ? 0x0a01 : 0x00c1;
            var descriptor = OpenAtRaw(
                TransferV3Posix.Descriptor(root),
                name,
                flags,
                TransferV3Posix.PrivateFileMode);
            if (descriptor < 0)
            {
                if (OperatingSystem.IsMacOS()
                    && Marshal.GetLastPInvokeError() == 92) // EILSEQ on APFS.
                {
                    return false;
                }
                throw new IOException(
                    $"Could not create invalid-UTF8 fixture (errno {Marshal.GetLastPInvokeError()}).");
            }
            _ = CloseNative(descriptor);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void RemoveInvalidUtf8Entry(
        Microsoft.Win32.SafeHandles.SafeFileHandle root)
    {
        var name = Marshal.AllocHGlobal(2);
        try
        {
            Marshal.WriteByte(name, 0, 0xff);
            Marshal.WriteByte(name, 1, 0);
            if (UnlinkAtRaw(TransferV3Posix.Descriptor(root), name, 0) != 0)
            {
                throw new IOException(
                    $"Could not remove invalid-UTF8 fixture (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtRaw(int directory, IntPtr path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAtRaw(int directory, IntPtr path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int descriptor);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class SnapshotFixture : IDisposable
    {
        private readonly string _workspace;
        private readonly byte[][] _tablePayloads;

        private SnapshotFixture(
            string workspace,
            string rootPath,
            TransferV3SourceContract contract,
            TransferV3Manifest manifest,
            byte[] manifestBytes,
            byte[][] tablePayloads,
            byte[] blobPayload)
        {
            _workspace = workspace;
            RootPath = rootPath;
            Contract = contract;
            Manifest = manifest;
            ManifestBytes = manifestBytes;
            _tablePayloads = tablePayloads;
            BlobPayload = blobPayload;
        }

        internal string RootPath { get; }
        internal TransferV3SourceContract Contract { get; }
        internal TransferV3Manifest Manifest { get; }
        internal byte[] ManifestBytes { get; }
        internal byte[] BlobPayload { get; }

        internal byte[] TablePayload(int ordinal) => _tablePayloads[ordinal];

        internal string TablePath(int ordinal) =>
            Path.Combine(RootPath, TableName(Contract, ordinal));

        internal void WritePrivate(string name, byte[] bytes) =>
            WritePrivateFile(Path.Combine(RootPath, name), bytes);

        internal void ReplacePrivate(string name, byte[] bytes)
        {
            var path = Path.Combine(RootPath, name);
            File.Delete(path);
            WritePrivateFile(path, bytes);
        }

        internal static SnapshotFixture Create(bool useShortPath = false)
        {
            var workspace = useShortPath
                ? Path.Combine("/tmp", $"nv3-{Guid.NewGuid():N}")
                : Path.Combine(
                    Environment.CurrentDirectory,
                    $".nzbdav-transfer-v3-reader-{Guid.NewGuid():N}");
            var root = Path.Combine(workspace, "snapshot");
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var contract = TransferV3SourceContract.LoadEmbedded();
            var tablePayloads = contract.Tables
                .Select((_, index) => Encoding.UTF8.GetBytes($"table-payload-{index:000}"))
                .ToArray();
            for (var index = 0; index < tablePayloads.Length; index++)
            {
                WritePrivateFile(
                    Path.Combine(root, TableName(contract, index)),
                    tablePayloads[index]);
            }

            var blobPayload = "blob-bundle-payload"u8.ToArray();
            WritePrivateFile(Path.Combine(root, "Blobs.jsonl"), blobPayload);

            var manifest = EmptyManifest(contract);
            var manifestBytes = TransferV3ManifestCodec.Serialize(manifest, contract);
            WritePrivateFile(Path.Combine(root, "manifest.json"), manifestBytes);
            return new SnapshotFixture(
                workspace,
                root,
                contract,
                manifest,
                manifestBytes,
                tablePayloads,
                blobPayload);
        }

        internal static string TableName(TransferV3SourceContract contract, int ordinal) =>
            $"table-{ordinal + 1:000}-{contract.Tables[ordinal].Name}.jsonl";

        private static TransferV3Manifest EmptyManifest(TransferV3SourceContract contract)
        {
            var tables = contract.Tables.Select((table, index) =>
                new TransferV3ManifestTable(
                    table.Name,
                    TableName(contract, index),
                    Batches: 0,
                    Rows: 0,
                    DecodedBytes: 0,
                    Sha256: EmptyDigest));
            var derived = contract.DerivedTables.Select(table =>
                new TransferV3ManifestDerivedTable(
                    table.Name,
                    Rows: 0,
                    LogicalSha256: EmptyDigest));
            var informational = contract.Tables
                .SelectMany(table => table.References)
                .Where(reference => reference.Policy is
                    TransferV3ReferencePolicy.InformationalDigest or
                    TransferV3ReferencePolicy.PolymorphicInformationalDigest)
                .Select(reference => new TransferV3ManifestInformationalReference(
                    reference.Name,
                    UnresolvedCount: 0,
                    UnresolvedSha256: EmptyDigest));
            return new TransferV3Manifest(
                FormatVersion: 3,
                SourceProvider: contract.Provider,
                SourceContractSha256: contract.ComputeSha256(),
                SourceSchemaSha256: contract.SourceSchemaSha256,
                MigrationContractSha256: contract.MigrationSourceContractSha256,
                SourceTimeZoneId: "UTC",
                Limits: new TransferV3ManifestLimits(
                    MaxFieldBytes: 1_048_576,
                    MaxBatchRows: 256,
                    MaxBatchBytes: 4_194_304),
                Tables: tables,
                DerivedTables: derived,
                InformationalReferences: informational,
                Blobs: new TransferV3ManifestBlobs(
                    Name: "Blobs",
                    File: "Blobs.jsonl",
                    Batches: 0,
                    Rows: 0,
                    DecodedBytes: 0,
                    Sha256: EmptyDigest,
                    Count: 0,
                    TotalBytes: 0,
                    InventorySha256: EmptyDigest));
        }

        private static void WritePrivateFile(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose()
        {
            try
            {
                foreach (var path in Directory.EnumerateDirectories(
                             _workspace,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    File.SetUnixFileMode(
                        path,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute);
                }
                Directory.Delete(_workspace, recursive: true);
            }
            catch
            {
                // Synthetic fixture cleanup is best effort.
            }
        }
    }
}

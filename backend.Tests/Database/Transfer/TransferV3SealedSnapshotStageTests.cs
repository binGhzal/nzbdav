using System.Reflection;
using NzbWebDAV.Database.Transfer;

#pragma warning disable CA1416 // Every Unix mode call is guarded by the verified POSIX ABI gate.

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SealedSnapshotStageTests
{
    private const string QueueBlobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string EmptyBlobId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OrphanBlobId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string DavItemId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    [Fact]
    public async Task CreateAsync_ReconstructsExactBytePreservingLayoutAndSealsEveryEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture = await StageFixture.CreateAsync();
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: path => privateRoot = path);

        using var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            hooks);

        var root = Assert.IsType<string>(privateRoot);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));

        var fixedNames = fixture.Verified.Manifest.Tables.Select(table => table.File)
            .Append(fixture.Verified.Manifest.Blobs.File)
            .Append("manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal) { "blobs" };
        var expectedBlobFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, bytes) in fixture.Blobs)
        {
            var value = Guid.ParseExact(id, "D");
            var normalized = value.ToString("N");
            var first = normalized[..2];
            var second = normalized.Substring(2, 2);
            expectedDirectories.Add(Path.Combine("blobs", first));
            expectedDirectories.Add(Path.Combine("blobs", first, second));
            expectedBlobFiles.Add(
                Path.Combine("blobs", first, second, value.ToString("D")),
                bytes);
        }

        var actualDirectories = Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToHashSet(StringComparer.Ordinal);
        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            expectedDirectories.Order(StringComparer.Ordinal),
            actualDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(
            fixedNames.Concat(expectedBlobFiles.Keys).Order(StringComparer.Ordinal),
            actualFiles.Order(StringComparer.Ordinal));

        foreach (var directory in actualDirectories)
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(root, directory)));
        }
        foreach (var file in actualFiles)
        {
            Assert.Equal(
                UnixFileMode.UserRead,
                File.GetUnixFileMode(Path.Combine(root, file)));
        }
        foreach (var name in fixedNames)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(fixture.SnapshotRoot, name)),
                await File.ReadAllBytesAsync(Path.Combine(root, name)));
        }
        foreach (var (relativePath, bytes) in expectedBlobFiles)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, relativePath)));
        }
    }

    [Fact]
    public async Task TypedReadsRemainIndependentOfSourceAndAreRevokedByStageDisposal()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture = await StageFixture.CreateAsync();
        var expectedManifest = await File.ReadAllBytesAsync(
            Path.Combine(fixture.SnapshotRoot, "manifest.json"));
        var expectedTable = await File.ReadAllBytesAsync(Path.Combine(
            fixture.SnapshotRoot,
            fixture.Verified.Manifest.Tables[0].File));
        var expectedBundle = await File.ReadAllBytesAsync(Path.Combine(
            fixture.SnapshotRoot,
            fixture.Verified.Manifest.Blobs.File));
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent);
        fixture.DisposeVerifiedSnapshot();

        Assert.Equal(expectedManifest, stage.GetCanonicalManifestCopy());
        Assert.Equal(27, stage.Manifest.Tables.Length);
        using (var table = stage.OpenTableRead(0))
        {
            Assert.False(table.CanWrite);
            Assert.Equal(expectedTable, await ReadAllAsync(table));
            Assert.Throws<NotSupportedException>(() => table.WriteByte(1));
        }
        using (var bundle = stage.OpenBlobBundleRead())
        {
            Assert.False(bundle.CanWrite);
            Assert.Equal(expectedBundle, await ReadAllAsync(bundle));
        }
        foreach (var (id, bytes) in fixture.Blobs)
        {
            using var blob = stage.OpenBlobRead(Guid.ParseExact(id, "D"));
            Assert.False(blob.CanWrite);
            Assert.Equal(bytes, await ReadAllAsync(blob));
        }

        var leaked = stage.OpenBlobRead(Guid.ParseExact(QueueBlobId, "D"));
        stage.Dispose();
        Assert.Throws<ObjectDisposedException>(() => leaked.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stage.OpenTableRead(0));
    }

    [Fact]
    public async Task CreateAsync_RetriesNonceCollisionWithoutOpeningOrChangingExistingEntry()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture = await StageFixture.CreateAsync();
        var firstNonce = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var secondNonce = Enumerable.Repeat((byte)0x22, 16).ToArray();
        var collidingName = ".nzbdav-transfer-v3-stage-" + Convert.ToHexStringLower(firstNonce);
        var collision = Path.Combine(fixture.StageParent, collidingName);
        Directory.CreateDirectory(collision);
        var sentinel = Path.Combine(collision, "must-survive");
        await File.WriteAllTextAsync(sentinel, "existing");
        string? privateRoot = null;
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: path => privateRoot = path,
            RootNonceForAttempt: attempt => attempt == 0
                ? firstNonce.ToArray()
                : secondNonce.ToArray());

        using (var stage = await TransferV3SealedSnapshotStage.CreateAsync(
                   fixture.Verified,
                   fixture.StageParent,
                   hooks))
        {
            Assert.NotEqual(collision, privateRoot);
            Assert.True(Directory.Exists(Assert.IsType<string>(privateRoot)));
            Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
        }

        Assert.True(Directory.Exists(collision));
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task CreateAsync_SourceMutationBeforeFinalVerificationRejectsAndCleansPrivateRoot()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture = await StageFixture.CreateAsync();
        string? privateRoot = null;
        var mutated = false;
        var sourceFile = Path.Combine(
            fixture.SnapshotRoot,
            fixture.Verified.Manifest.Tables[0].File);
        var hooks = new TransferV3SealedSnapshotStageHooks(
            ObservePrivateRoot: path => privateRoot = path,
            AfterFaultPoint: (point, ordinal) =>
            {
                if (mutated
                    || point != TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied
                    || ordinal != 0)
                {
                    return;
                }
                mutated = true;
                var bytes = File.ReadAllBytes(sourceFile);
                bytes[^1] ^= 0x01;
                File.WriteAllBytes(sourceFile, bytes);
                File.SetUnixFileMode(sourceFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });

        var failure = await Assert.ThrowsAsync<TransferV3SealedSnapshotStageException>(() =>
            TransferV3SealedSnapshotStage.CreateAsync(
                fixture.Verified,
                fixture.StageParent,
                hooks));

        Assert.True(mutated);
        Assert.Equal("source-changed", failure.Code);
        Assert.False(Directory.Exists(Assert.IsType<string>(privateRoot)));
        Assert.DoesNotContain(fixture.SnapshotRoot, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sourceFile, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_RemovesOnlyOwnedStageAndPreservesTrustedParentSentinel()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var fixture = await StageFixture.CreateAsync();
        var sentinel = Path.Combine(fixture.StageParent, "sentinel");
        await File.WriteAllTextAsync(sentinel, "preserve");
        string? privateRoot = null;
        var stage = await TransferV3SealedSnapshotStage.CreateAsync(
            fixture.Verified,
            fixture.StageParent,
            new TransferV3SealedSnapshotStageHooks(
                ObservePrivateRoot: path => privateRoot = path));
        var root = Assert.IsType<string>(privateRoot);
        Assert.True(Directory.Exists(root));

        stage.Dispose();
        stage.Dispose();

        Assert.False(Directory.Exists(root));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(["sentinel"], Directory.EnumerateFileSystemEntries(fixture.StageParent)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StageSurface_IsTypedOpaqueAndHasNoTargetDatabaseOrRuntimeBlobPublication()
    {
        var type = typeof(TransferV3SealedSnapshotStage);
        Assert.DoesNotContain(type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(string)
                && method.Name.StartsWith("Open", StringComparison.Ordinal)));

        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SealedSnapshotStage.cs"));
        Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgre", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DatabaseTransferService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RootPath", source, StringComparison.Ordinal);
        Assert.Contains("OpenReadOnlyRegularFileAt", source, StringComparison.Ordinal);
        Assert.Contains("TryUnlinkOwnedDirectory", source, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ReadAllAsync(Stream source)
    {
        await using var output = new MemoryStream();
        await source.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task<TransferV3SqliteExportSession> OpenSessionAsync(
        TransferV3ValidationSource source)
    {
        using var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        return await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(),
                new TransferV3SourceProvenance(
                    database.Identity,
                    blobs.Identity,
                    TimeZoneInfo.Utc.Id));
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

    internal sealed class StageFixture : IAsyncDisposable
    {
        private readonly TransferV3ValidationSource _source;
        private bool _verifiedDisposed;

        private StageFixture(
            TransferV3ValidationSource source,
            string snapshotRoot,
            string stageParent,
            TransferV3VerifiedSnapshot verified,
            IReadOnlyDictionary<string, byte[]> blobs)
        {
            _source = source;
            SnapshotRoot = snapshotRoot;
            StageParent = stageParent;
            Verified = verified;
            Blobs = blobs;
        }

        internal string SnapshotRoot { get; }
        internal string StageParent { get; }
        internal TransferV3VerifiedSnapshot Verified { get; }
        internal IReadOnlyDictionary<string, byte[]> Blobs { get; }

        internal static async Task<StageFixture> CreateAsync(int additionalBlobCount = 0)
        {
            var source = await TransferV3ValidationSource.CreateAsync();
            try
            {
                var shortBlob = "short-nzb"u8.ToArray();
                var emptyBlob = Array.Empty<byte>();
                var orphanBlob = new byte[64 * 1024 + 17];
                for (var index = 0; index < orphanBlob.Length; index++)
                    orphanBlob[index] = (byte)(index % 251);
                await source.InsertValidQueueItemAsync(QueueBlobId);
                await source.WriteBlobAsync(QueueBlobId, shortBlob);
                await source.InsertUsenetDavItemAsync(DavItemId, 201, EmptyBlobId);
                await source.WriteBlobAsync(EmptyBlobId, emptyBlob);
                await source.WriteBlobAsync(OrphanBlobId, orphanBlob);

                var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [QueueBlobId] = shortBlob,
                    [EmptyBlobId] = emptyBlob,
                    [OrphanBlobId] = orphanBlob,
                };
                for (var ordinal = 0; ordinal < additionalBlobCount; ordinal++)
                {
                    var id = new Guid(
                        ordinal + 1,
                        0x1234,
                        0x5678,
                        0x90,
                        0xab,
                        0xcd,
                        0xef,
                        0x11,
                        0x22,
                        0x33,
                        0x44).ToString("D");
                    var content = BitConverter.GetBytes(ordinal);
                    await source.WriteBlobAsync(id, content);
                    blobs.Add(id, content);
                }

                var snapshot = Path.Combine(source.ValidationWorkspaceRoot, "stage-source");
                await using (var session = await OpenSessionAsync(source))
                {
                    await new TransferV3SnapshotExporter().ExportAsync(
                        session,
                        snapshot,
                        new TransferV3Limits(64 * 1024, 2, 4 * 1024 * 1024));
                }
                var verified = await new TransferV3SnapshotVerifier().VerifyAsync(snapshot);
                var parent = Path.Combine(source.ValidationWorkspaceRoot, "sealed-stages");
                Directory.CreateDirectory(parent);
                File.SetUnixFileMode(
                    parent,
                    (UnixFileMode)TransferV3Posix.PrivateDirectoryMode);
                return new StageFixture(
                    source,
                    snapshot,
                    parent,
                    verified,
                    blobs);
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }
        }

        internal void DisposeVerifiedSnapshot()
        {
            if (_verifiedDisposed) return;
            _verifiedDisposed = true;
            Verified.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            DisposeVerifiedSnapshot();
            await _source.DisposeAsync();
        }
    }
}

#pragma warning restore CA1416

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3SnapshotReaderFaultPoint
{
    AfterRootOpened,
    AfterInitialEnumeration,
    AfterExpectedFileOpened,
    AfterManifestRead,
    BeforeVerifyUnchanged,
    BeforeFinalVerifyUnchanged,
}

internal sealed record TransferV3SnapshotReaderHooks(
    Action<TransferV3SnapshotReaderFaultPoint, int?>? AfterFaultPoint = null);

internal static class TransferV3SnapshotReader
{
    internal static async Task<TransferV3PinnedSnapshot> OpenAsync(
        string snapshotRoot,
        TransferV3SourceContract reviewedContract,
        TransferV3SnapshotReaderHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        if (!TransferV3Posix.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Transfer-v3 snapshot reads require a verified POSIX ABI.");
        }

        ArgumentNullException.ThrowIfNull(reviewedContract);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ValidatePath(snapshotRoot);
        var expectedNames = BuildExpectedNames(reviewedContract);
        SafeFileHandle? parent = null;
        SafeFileHandle? root = null;
        var retained = new List<TransferV3RetainedSnapshotFile>(expectedNames.Length);
        byte[]? canonicalManifest = null;
        Exception? primary = null;
        var cleanupCodes = new List<string>();
        try
        {
            parent = TransferV3Posix.OpenDirectory(path.ParentPath);
            var parentStat = TransferV3Posix.GetFileStat(parent);
            root = TransferV3Posix.OpenDirectoryAt(parent, path.RootName);
            var rootStat = TransferV3Posix.GetFileStat(root);
            ValidatePrivateRoot(rootStat);
            InvokeHook(hooks, TransferV3SnapshotReaderFaultPoint.AfterRootOpened, null);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateExactEntries(root, expectedNames);
            InvokeHook(hooks, TransferV3SnapshotReaderFaultPoint.AfterInitialEnumeration, null);
            cancellationToken.ThrowIfCancellationRequested();

            var identities = new HashSet<TransferV3FileIdentity>();
            for (var ordinal = 0; ordinal < expectedNames.Length; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafeFileHandle? handle = null;
                try
                {
                    handle = TransferV3Posix.OpenReadOnlyRegularFileAt(
                        root,
                        expectedNames[ordinal]);
                    var stat = TransferV3Posix.GetFileStat(handle);
                    ValidatePrivateFile(stat);
                    if (!identities.Add(stat.Fingerprint.Identity))
                    {
                        throw Failure("file-identity");
                    }

                    retained.Add(new TransferV3RetainedSnapshotFile(
                        ordinal,
                        expectedNames[ordinal],
                        handle,
                        stat));
                    handle = null;
                    InvokeHook(
                        hooks,
                        TransferV3SnapshotReaderFaultPoint.AfterExpectedFileOpened,
                        ordinal);
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            ValidateVisibleRoot(
                path,
                parent,
                parentStat,
                root,
                rootStat,
                expectedNames);

            for (var ordinal = 0; ordinal < retained.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = retained[ordinal];
                var captureBytes = ordinal == TransferV3PinnedSnapshot.ManifestFileOrdinal;
                var evidence = await ReadAndHashAsync(
                        root,
                        file,
                        captureBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                file.SetInitialReceipt(evidence.Stat, evidence.RawSha256);
                if (captureBytes)
                {
                    canonicalManifest = evidence.Bytes;
                }
                else
                {
                    evidence.ClearBytes();
                }
            }

            if (canonicalManifest is null)
            {
                throw Failure("manifest-read");
            }
            if (canonicalManifest.Length is <= 0 or > TransferV3ManifestCodec.MaxManifestBytes)
            {
                throw Failure("manifest-size");
            }

            TransferV3Manifest manifest;
            try
            {
                manifest = TransferV3ManifestCodec.Parse(
                    canonicalManifest,
                    reviewedContract);
            }
            catch
            {
                throw Failure("manifest");
            }
            var manifestSha256 = TransferV3ManifestCodec.ComputeSha256(canonicalManifest);
            InvokeHook(hooks, TransferV3SnapshotReaderFaultPoint.AfterManifestRead, null);
            cancellationToken.ThrowIfCancellationRequested();

            if (hooks?.AfterFaultPoint is not null)
            {
                await VerifyInitialReceiptsAsync(
                        path,
                        parent,
                        parentStat,
                        root,
                        rootStat,
                        expectedNames,
                        retained,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                ValidateVisibleRoot(
                    path,
                    parent,
                    parentStat,
                    root,
                    rootStat,
                    expectedNames);
                ValidateRetainedStatsAndPaths(root, retained);
            }

            var result = new TransferV3PinnedSnapshot(
                path.RootPath,
                path.ParentPath,
                path.RootName,
                parent,
                parentStat,
                root,
                rootStat,
                expectedNames,
                retained.ToImmutableArray(),
                manifest,
                canonicalManifest,
                manifestSha256,
                hooks);
            parent = null;
            root = null;
            retained.Clear();
            canonicalManifest = null;
            return result;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            primary = new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3SnapshotReadException exception)
        {
            primary = exception;
        }
        catch
        {
            primary = Failure("open");
        }
        finally
        {
            if (primary is not null)
            {
                if (canonicalManifest is not null)
                {
                    CryptographicOperations.ZeroMemory(canonicalManifest);
                }
                foreach (var file in retained)
                {
                    file.ClearReceipts();
                    TryDispose(file.Handle, cleanupCodes, "file-close-failed");
                }
                TryDispose(root, cleanupCodes, "root-close-failed");
                TryDispose(parent, cleanupCodes, "parent-close-failed");
            }
        }

        TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary!, cleanupCodes);
        throw new InvalidOperationException("Unreachable Transfer-v3 reader failure path.");
    }

    private static TransferV3SnapshotPath ValidatePath(string snapshotRoot)
    {
        if (string.IsNullOrWhiteSpace(snapshotRoot)
            || !Path.IsPathFullyQualified(snapshotRoot)
            || !string.Equals(
                Path.GetPathRoot(snapshotRoot),
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal))
        {
            throw Failure("path");
        }

        var withoutTrailing = Path.TrimEndingDirectorySeparator(snapshotRoot);
        if (withoutTrailing.Length == 0
            || string.Equals(
                withoutTrailing,
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal))
        {
            throw Failure("path");
        }

        var components = withoutTrailing.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0
            || components.Any(component => component is "." or ".."))
        {
            throw Failure("path");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(withoutTrailing));
        }
        catch
        {
            throw Failure("path");
        }
        var parent = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(parent)
            || string.IsNullOrEmpty(name)
            || name is "." or "..")
        {
            throw Failure("path");
        }

        return new TransferV3SnapshotPath(full, parent, name);
    }

    private static ImmutableArray<string> BuildExpectedNames(
        TransferV3SourceContract contract)
    {
        if (contract.FormatVersion != 3 || contract.Tables.Count != 27)
        {
            throw Failure("contract");
        }

        var names = ImmutableArray.CreateBuilder<string>(29);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < contract.Tables.Count; ordinal++)
        {
            var name = string.Create(
                CultureInfo.InvariantCulture,
                $"table-{ordinal + 1:000}-{contract.Tables[ordinal].Name}.jsonl");
            AddExpectedName(name, names, unique);
        }
        AddExpectedName("Blobs.jsonl", names, unique);
        AddExpectedName("manifest.json", names, unique);
        if (names.Count != TransferV3PinnedSnapshot.FixedFileCount)
        {
            throw Failure("contract");
        }
        return names.MoveToImmutable();
    }

    private static void AddExpectedName(
        string name,
        ImmutableArray<string>.Builder names,
        HashSet<string> unique)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.Contains('/')
            || name.Contains('\0')
            || !unique.Add(name))
        {
            throw Failure("contract");
        }
        names.Add(name);
    }

    private static void ValidatePrivateRoot(TransferV3FileStat stat)
    {
        const uint privateDirectory = 0x4000 | TransferV3Posix.PrivateDirectoryMode;
        if (stat.Mode != privateDirectory || stat.LinkCount == 0)
        {
            throw Failure("root-stat");
        }
    }

    private static void ValidatePrivateFile(TransferV3FileStat stat)
    {
        const uint privateFile = 0x8000 | TransferV3Posix.PrivateFileMode;
        if (stat.Mode != privateFile
            || stat.LinkCount != 1
            || stat.Fingerprint.Size < 0)
        {
            throw Failure("file-stat");
        }
    }

    private static void ValidateExactEntries(
        SafeFileHandle root,
        ImmutableArray<string> expectedNames)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(root))
            {
                if (entry.Name is null || !actual.Add(entry.Name))
                {
                    throw Failure("entry-set");
                }
            }
        }
        catch (TransferV3SnapshotReadException)
        {
            throw;
        }
        catch
        {
            throw Failure("entry-set");
        }

        if (actual.Count != expectedNames.Length
            || expectedNames.Any(name => !actual.Contains(name)))
        {
            throw Failure("entry-set");
        }
    }

    private static void ValidateVisibleRoot(
        TransferV3SnapshotPath path,
        SafeFileHandle retainedParent,
        TransferV3FileStat expectedParent,
        SafeFileHandle retainedRoot,
        TransferV3FileStat expectedRoot,
        ImmutableArray<string> expectedNames)
    {
        var retainedParentStat = TransferV3Posix.GetFileStat(retainedParent);
        var retainedRootStat = TransferV3Posix.GetFileStat(retainedRoot);
        if (!MatchesRetainedParent(retainedParentStat, expectedParent)
            || retainedRootStat != expectedRoot)
        {
            throw Failure("root-changed");
        }
        ValidatePrivateRoot(retainedRootStat);
        if (!TransferV3Posix.EntryMatches(
                retainedParent,
                path.RootName,
                expectedRoot.Fingerprint.Identity))
        {
            throw Failure("root-binding");
        }

        try
        {
            using var visibleParent = TransferV3Posix.OpenDirectory(path.ParentPath);
            using var visibleRoot = TransferV3Posix.OpenDirectoryAt(
                visibleParent,
                path.RootName);
            if (!MatchesRetainedParent(
                    TransferV3Posix.GetFileStat(visibleParent),
                    expectedParent)
                || TransferV3Posix.GetFileStat(visibleRoot) != expectedRoot)
            {
                throw Failure("root-binding");
            }
            ValidateExactEntries(visibleRoot, expectedNames);
        }
        catch (TransferV3SnapshotReadException)
        {
            throw;
        }
        catch
        {
            throw Failure("root-binding");
        }
    }

    private static bool MatchesRetainedParent(
        TransferV3FileStat actual,
        TransferV3FileStat expected) =>
        actual.Fingerprint.Identity == expected.Fingerprint.Identity
        && actual.Mode == expected.Mode;

    private static void ValidateRetainedStatsAndPaths(
        SafeFileHandle root,
        IReadOnlyList<TransferV3RetainedSnapshotFile> retained)
    {
        foreach (var file in retained)
        {
            var retainedStat = TransferV3Posix.GetFileStat(file.Handle);
            ValidatePrivateFile(retainedStat);
            if (retainedStat != file.InitialStat)
            {
                throw Failure("file-changed");
            }
            using var visible = TransferV3Posix.OpenReadOnlyRegularFileAt(
                root,
                file.ExpectedName);
            var visibleStat = TransferV3Posix.GetFileStat(visible);
            ValidatePrivateFile(visibleStat);
            if (visibleStat != retainedStat)
            {
                throw Failure("file-binding");
            }
        }
    }

    private static async Task VerifyInitialReceiptsAsync(
        TransferV3SnapshotPath path,
        SafeFileHandle parent,
        TransferV3FileStat parentStat,
        SafeFileHandle root,
        TransferV3FileStat rootStat,
        ImmutableArray<string> expectedNames,
        IReadOnlyList<TransferV3RetainedSnapshotFile> retained,
        CancellationToken cancellationToken)
    {
        ValidateVisibleRoot(
            path,
            parent,
            parentStat,
            root,
            rootStat,
            expectedNames);
        foreach (var file in retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await ReadAndHashAsync(
                    root,
                    file,
                    captureBytes: false,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (evidence.Stat != file.InitialStat
                    || !file.MatchesInitialReceipt(
                        evidence.Stat,
                        evidence.RawSha256))
                {
                    throw Failure("file-changed");
                }
            }
            finally
            {
                evidence.Clear();
            }
        }
        ValidateVisibleRoot(
            path,
            parent,
            parentStat,
            root,
            rootStat,
            expectedNames);
    }

    internal static async ValueTask<TransferV3SnapshotFileEvidence> ReadAndHashAsync(
        SafeFileHandle root,
        TransferV3RetainedSnapshotFile file,
        bool captureBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = TransferV3Posix.GetFileStat(file.Handle);
        ValidatePrivateFile(before);
        if (before != file.InitialStat)
        {
            throw Failure("file-changed");
        }
        if (captureBytes
            && before.Fingerprint.Size > TransferV3ManifestCodec.MaxManifestBytes)
        {
            throw Failure("manifest-size");
        }

        byte[]? captured = captureBytes
            ? new byte[checked((int)before.Fingerprint.Size)]
            : null;
        var buffer = captureBytes
            ? captured!
            : new byte[(int)Math.Min(1024 * 1024L, Math.Max(1L, before.Fingerprint.Size))];
        byte[]? digest = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < before.Fingerprint.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = checked((int)Math.Min(
                    buffer.Length,
                    before.Fingerprint.Size - offset));
                var destination = buffer.AsMemory(
                    captureBytes ? checked((int)offset) : 0,
                    request);
                var read = await RandomAccess.ReadAsync(
                        file.Handle,
                        destination,
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw Failure("file-truncated");
                }
                hash.AppendData(destination.Span[..read]);
                offset = checked(offset + read);
            }

            var eofProbe = new byte[1];
            try
            {
                if (await RandomAccess.ReadAsync(
                        file.Handle,
                        eofProbe,
                        before.Fingerprint.Size,
                        cancellationToken)
                    .ConfigureAwait(false) != 0)
                {
                    throw Failure("file-trailing");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(eofProbe);
            }

            digest = hash.GetHashAndReset();
            var after = TransferV3Posix.GetFileStat(file.Handle);
            ValidatePrivateFile(after);
            if (after != before)
            {
                throw Failure("file-changed");
            }

            using (var visible = TransferV3Posix.OpenReadOnlyRegularFileAt(
                       root,
                       file.ExpectedName))
            {
                var visibleStat = TransferV3Posix.GetFileStat(visible);
                ValidatePrivateFile(visibleStat);
                if (visibleStat != after)
                {
                    throw Failure("file-binding");
                }
            }

            var result = new TransferV3SnapshotFileEvidence(
                after,
                digest,
                captured ?? Array.Empty<byte>());
            digest = null;
            captured = null;
            return result;
        }
        finally
        {
            if (!captureBytes)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
            if (captured is not null)
            {
                CryptographicOperations.ZeroMemory(captured);
            }
        }
    }

    internal static void ValidateSnapshotUnchanged(
        string rootPath,
        string parentPath,
        string rootName,
        SafeFileHandle parent,
        TransferV3FileStat parentStat,
        SafeFileHandle root,
        TransferV3FileStat rootStat,
        ImmutableArray<string> expectedNames,
        IReadOnlyList<TransferV3RetainedSnapshotFile> retained,
        CancellationToken cancellationToken)
    {
        var path = new TransferV3SnapshotPath(rootPath, parentPath, rootName);
        ValidateVisibleRoot(
            path,
            parent,
            parentStat,
            root,
            rootStat,
            expectedNames);
        foreach (var file in retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = ReadAndHashAsync(
                    root,
                    file,
                    captureBytes: false,
                    cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            try
            {
                if (evidence.Stat != file.InitialStat
                    || !file.MatchesFinalReceipt(
                        evidence.Stat,
                        evidence.RawSha256))
                {
                    throw Failure("verified-receipt");
                }
            }
            finally
            {
                evidence.Clear();
            }
        }
        ValidateVisibleRoot(
            path,
            parent,
            parentStat,
            root,
            rootStat,
            expectedNames);
    }

    internal static TransferV3SnapshotReadException Failure(string code) => new(code);

    private static void InvokeHook(
        TransferV3SnapshotReaderHooks? hooks,
        TransferV3SnapshotReaderFaultPoint point,
        int? ordinal)
    {
        hooks?.AfterFaultPoint?.Invoke(point, ordinal);
    }

    private static void TryDispose(
        IDisposable? value,
        List<string> cleanupCodes,
        string code)
    {
        if (value is null) return;
        try
        {
            value.Dispose();
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(cleanupCodes, code);
        }
    }
}

internal sealed class TransferV3PinnedSnapshot : IDisposable
{
    internal const int TableFileCount = 27;
    internal const int BlobFileOrdinal = 27;
    internal const int ManifestFileOrdinal = 28;
    internal const int FixedFileCount = 29;

    private readonly string _rootPath;
    private readonly string _parentPath;
    private readonly string _rootName;
    private readonly SafeFileHandle _parent;
    private readonly TransferV3FileStat _parentStat;
    private readonly SafeFileHandle _root;
    private readonly TransferV3FileStat _rootStat;
    private readonly ImmutableArray<string> _expectedNames;
    private readonly ImmutableArray<TransferV3RetainedSnapshotFile> _retained;
    private readonly byte[] _canonicalManifest;
    private readonly TransferV3SnapshotReaderHooks? _hooks;
    private readonly object _gate = new();
    private readonly object _verifiedAccessKey = new();
    private readonly TransferV3Manifest _manifest;
    private readonly string _manifestSha256;
    private TransferV3SnapshotVerificationMetrics? _metrics;
    private bool _finalVerificationCompleted;
    private bool _verifiedOwnershipClaimed;
    private bool _disposed;

    internal TransferV3PinnedSnapshot(
        string rootPath,
        string parentPath,
        string rootName,
        SafeFileHandle parent,
        TransferV3FileStat parentStat,
        SafeFileHandle root,
        TransferV3FileStat rootStat,
        ImmutableArray<string> expectedNames,
        ImmutableArray<TransferV3RetainedSnapshotFile> retained,
        TransferV3Manifest manifest,
        byte[] canonicalManifest,
        string manifestSha256,
        TransferV3SnapshotReaderHooks? hooks)
    {
        _rootPath = rootPath;
        _parentPath = parentPath;
        _rootName = rootName;
        _parent = parent;
        _parentStat = parentStat;
        _root = root;
        _rootStat = rootStat;
        _expectedNames = expectedNames;
        _retained = retained;
        _manifest = manifest;
        _canonicalManifest = canonicalManifest;
        _manifestSha256 = manifestSha256;
        _hooks = hooks;
    }

    internal TransferV3Manifest Manifest => GetManifest(accessKey: null);

    internal string ManifestSha256 => GetManifestSha256(accessKey: null);

    internal TransferV3SnapshotVerificationMetrics? Metrics
    {
        get
        {
            lock (_gate)
            {
                EnsureAccess(accessKey: null);
                return _metrics;
            }
        }
    }

    internal Stream OpenTableRead(int ordinal)
    {
        if ((uint)ordinal >= TableFileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return OpenTypedRead(ordinal, accessKey: null);
    }

    internal Stream OpenBlobBundleRead() => OpenTypedRead(BlobFileOrdinal, accessKey: null);

    internal byte[] GetCanonicalManifestCopy()
    {
        lock (_gate)
        {
            EnsureAccess(accessKey: null);
            return (byte[])_canonicalManifest.Clone();
        }
    }

    internal void RecordVerifiedReceipt(
        int fixedFileOrdinal,
        long size,
        ReadOnlySpan<byte> rawSha256)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey: null);
            if ((uint)fixedFileOrdinal >= FixedFileCount)
            {
                throw new ArgumentOutOfRangeException(nameof(fixedFileOrdinal));
            }
            if (rawSha256.Length != SHA256.HashSizeInBytes || size < 0)
            {
                throw TransferV3SnapshotReader.Failure("verified-receipt");
            }

            var file = _retained[fixedFileOrdinal];
            if (!file.TrySetVerifiedReceipt(size, rawSha256))
            {
                throw TransferV3SnapshotReader.Failure("verified-receipt");
            }
        }
    }

    internal void SetVerificationMetrics(TransferV3SnapshotVerificationMetrics metrics)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey: null);
            if (_retained.Any(file => !file.HasVerifiedReceipt))
            {
                throw TransferV3SnapshotReader.Failure("verification-incomplete");
            }
            if (metrics is null || _metrics is not null)
            {
                throw TransferV3SnapshotReader.Failure("verification-metrics");
            }
            _metrics = metrics;
        }
    }

    internal void VerifyUnchanged(CancellationToken cancellationToken = default) =>
        VerifyUnchangedCore(accessKey: null, cancellationToken);

    private void VerifyUnchangedCore(
        object? accessKey,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            cancellationToken.ThrowIfCancellationRequested();
            if (_retained.Any(file => !file.HasVerifiedReceipt))
            {
                throw TransferV3SnapshotReader.Failure("verification-incomplete");
            }

            _finalVerificationCompleted = false;
            try
            {
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotReaderFaultPoint.BeforeVerifyUnchanged,
                    null);
                cancellationToken.ThrowIfCancellationRequested();
                TransferV3SnapshotReader.ValidateSnapshotUnchanged(
                    _rootPath,
                    _parentPath,
                    _rootName,
                    _parent,
                    _parentStat,
                    _root,
                    _rootStat,
                    _expectedNames,
                    _retained,
                    cancellationToken);
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotReaderFaultPoint.BeforeFinalVerifyUnchanged,
                    null);
                cancellationToken.ThrowIfCancellationRequested();
                TransferV3SnapshotReader.ValidateSnapshotUnchanged(
                    _rootPath,
                    _parentPath,
                    _rootName,
                    _parent,
                    _parentStat,
                    _root,
                    _rootStat,
                    _expectedNames,
                    _retained,
                    cancellationToken);
                _finalVerificationCompleted = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TransferV3SnapshotReadException)
            {
                throw;
            }
            catch
            {
                throw TransferV3SnapshotReader.Failure("verify");
            }
        }
    }

    internal object ClaimVerifiedOwnership()
    {
        lock (_gate)
        {
            EnsureAccess(accessKey: null);
            if (_metrics is null
                || !_finalVerificationCompleted
                || _verifiedOwnershipClaimed)
            {
                throw TransferV3SnapshotReader.Failure("verification-promotion");
            }
            _verifiedOwnershipClaimed = true;
            return _verifiedAccessKey;
        }
    }

    internal TransferV3Manifest GetVerifiedManifest(object accessKey) =>
        GetManifest(accessKey);

    internal string GetVerifiedManifestSha256(object accessKey) =>
        GetManifestSha256(accessKey);

    internal TransferV3SnapshotVerificationMetrics GetVerifiedMetrics(object accessKey)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            return _metrics
                ?? throw TransferV3SnapshotReader.Failure("verification-promotion");
        }
    }

    internal Stream OpenVerifiedTableRead(int ordinal, object accessKey)
    {
        if ((uint)ordinal >= TableFileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return OpenTypedRead(ordinal, accessKey);
    }

    internal Stream OpenVerifiedBlobBundleRead(object accessKey) =>
        OpenTypedRead(BlobFileOrdinal, accessKey);

    internal byte[] GetVerifiedCanonicalManifestCopy(object accessKey)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            return (byte[])_canonicalManifest.Clone();
        }
    }

    internal void VerifyVerifiedUnchanged(
        object accessKey,
        CancellationToken cancellationToken = default) =>
        VerifyUnchangedCore(accessKey, cancellationToken);

    internal void DisposeVerified(object accessKey) => DisposeCore(accessKey);

    private TransferV3Manifest GetManifest(object? accessKey)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            return _manifest;
        }
    }

    private string GetManifestSha256(object? accessKey)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            return _manifestSha256;
        }
    }

    private Stream OpenTypedRead(int ordinal, object? accessKey)
    {
        lock (_gate)
        {
            EnsureAccess(accessKey);
            try
            {
                var file = _retained[ordinal];
                var current = TransferV3Posix.GetFileStat(file.Handle);
                if (current != file.InitialStat)
                {
                    throw TransferV3SnapshotReader.Failure("file-changed");
                }
                var duplicate = TransferV3Posix.DuplicateHandle(file.Handle);
                try
                {
                    return new TransferV3RandomAccessReadStream(
                        duplicate,
                        file.InitialStat.Fingerprint.Size);
                }
                catch
                {
                    duplicate.Dispose();
                    throw;
                }
            }
            catch (TransferV3SnapshotReadException)
            {
                throw;
            }
            catch
            {
                throw TransferV3SnapshotReader.Failure("typed-read");
            }
        }
    }

    private void EnsureOpen()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TransferV3PinnedSnapshot));
        }
    }

    private void EnsureAccess(object? accessKey)
    {
        EnsureOpen();
        if (_verifiedOwnershipClaimed
            && !ReferenceEquals(accessKey, _verifiedAccessKey))
        {
            throw TransferV3SnapshotReader.Failure("verification-transferred");
        }
        if (!_verifiedOwnershipClaimed && accessKey is not null)
        {
            throw TransferV3SnapshotReader.Failure("verification-promotion");
        }
    }

    public void Dispose() => DisposeCore(accessKey: null);

    private void DisposeCore(object? accessKey)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_verifiedOwnershipClaimed
                && !ReferenceEquals(accessKey, _verifiedAccessKey))
            {
                return;
            }
            _disposed = true;
            CryptographicOperations.ZeroMemory(_canonicalManifest);
            foreach (var file in _retained)
            {
                file.ClearReceipts();
                file.Handle.Dispose();
            }
            _root.Dispose();
            _parent.Dispose();
        }
    }
}

internal sealed class TransferV3VerifiedSnapshot : IDisposable
{
    private readonly TransferV3PinnedSnapshot _snapshot;
    private readonly object _accessKey;

    private TransferV3VerifiedSnapshot(
        TransferV3PinnedSnapshot snapshot,
        object accessKey)
    {
        _snapshot = snapshot;
        _accessKey = accessKey;
    }

    internal static TransferV3VerifiedSnapshot Promote(
        TransferV3PinnedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var accessKey = snapshot.ClaimVerifiedOwnership();
        return new TransferV3VerifiedSnapshot(snapshot, accessKey);
    }

    internal TransferV3Manifest Manifest => _snapshot.GetVerifiedManifest(_accessKey);

    internal string ManifestSha256 => _snapshot.GetVerifiedManifestSha256(_accessKey);

    internal TransferV3SnapshotVerificationMetrics Metrics =>
        _snapshot.GetVerifiedMetrics(_accessKey);

    internal Stream OpenTableRead(int ordinal) =>
        _snapshot.OpenVerifiedTableRead(ordinal, _accessKey);

    internal Stream OpenBlobBundleRead() =>
        _snapshot.OpenVerifiedBlobBundleRead(_accessKey);

    internal byte[] GetCanonicalManifestCopy() =>
        _snapshot.GetVerifiedCanonicalManifestCopy(_accessKey);

    internal void VerifyUnchanged(CancellationToken cancellationToken = default) =>
        _snapshot.VerifyVerifiedUnchanged(_accessKey, cancellationToken);

    public void Dispose() => _snapshot.DisposeVerified(_accessKey);
}

internal sealed class TransferV3RetainedSnapshotFile(
    int ordinal,
    string expectedName,
    SafeFileHandle handle,
    TransferV3FileStat initialStat)
{
    private byte[] _initialRawSha256 = [];
    private byte[]? _verifiedRawSha256;
    private long? _verifiedSize;

    internal int Ordinal { get; } = ordinal;
    internal string ExpectedName { get; } = expectedName;
    internal SafeFileHandle Handle { get; } = handle;
    internal TransferV3FileStat InitialStat { get; } = initialStat;
    internal bool HasVerifiedReceipt => _verifiedRawSha256 is not null;

    internal void SetInitialReceipt(TransferV3FileStat stat, byte[] rawSha256)
    {
        if (_initialRawSha256.Length != 0 || stat != InitialStat)
        {
            CryptographicOperations.ZeroMemory(rawSha256);
            throw TransferV3SnapshotReader.Failure("initial-receipt");
        }
        _initialRawSha256 = rawSha256;
    }

    internal bool MatchesInitialReceipt(
        TransferV3FileStat stat,
        ReadOnlySpan<byte> rawSha256) =>
        stat == InitialStat
        && _initialRawSha256.Length == SHA256.HashSizeInBytes
        && CryptographicOperations.FixedTimeEquals(
            rawSha256,
            _initialRawSha256);

    internal bool MatchesFinalReceipt(
        TransferV3FileStat stat,
        ReadOnlySpan<byte> rawSha256) =>
        MatchesInitialReceipt(stat, rawSha256)
        && _verifiedRawSha256 is not null
        && _verifiedSize == stat.Fingerprint.Size
        && CryptographicOperations.FixedTimeEquals(
            rawSha256,
            _verifiedRawSha256);

    internal bool TrySetVerifiedReceipt(long size, ReadOnlySpan<byte> rawSha256)
    {
        if (_verifiedRawSha256 is not null
            || size != InitialStat.Fingerprint.Size
            || !MatchesInitialReceipt(InitialStat, rawSha256))
        {
            return false;
        }
        _verifiedSize = size;
        _verifiedRawSha256 = rawSha256.ToArray();
        return true;
    }

    internal void ClearReceipts()
    {
        CryptographicOperations.ZeroMemory(_initialRawSha256);
        if (_verifiedRawSha256 is not null)
        {
            CryptographicOperations.ZeroMemory(_verifiedRawSha256);
        }
    }
}

internal sealed class TransferV3RandomAccessReadStream(
    SafeFileHandle handle,
    long length) : Stream
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            EnsureOpen();
            return length;
        }
    }

    public override long Position
    {
        get
        {
            _gate.Wait();
            try
            {
                EnsureOpen();
                return _position;
            }
            finally
            {
                _gate.Release();
            }
        }
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _gate.Wait();
            try
            {
                EnsureOpen();
                _position = value;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public override void Flush()
    {
        EnsureOpen();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        _gate.Wait();
        try
        {
            EnsureOpen();
            var request = BoundRead(buffer.Length);
            if (request == 0) return 0;
            var read = RandomAccess.Read(handle, buffer[..request], _position);
            _position = checked(_position + read);
            return read;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            var request = BoundRead(buffer.Length);
            if (request == 0) return 0;
            var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer[..request],
                    _position,
                    cancellationToken)
                .ConfigureAwait(false);
            _position = checked(_position + read);
            return read;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _gate.Wait();
        try
        {
            EnsureOpen();
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0) throw new IOException("Cannot seek before the stream origin.");
            _position = next;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Wait();
            try
            {
                if (!_disposed)
                {
                    _disposed = true;
                    handle.Dispose();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        base.Dispose(disposing);
    }

    private int BoundRead(int requested)
    {
        if (requested <= 0 || _position >= length) return 0;
        return checked((int)Math.Min(requested, length - _position));
    }

    private void EnsureOpen()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TransferV3RandomAccessReadStream));
        }
    }
}

internal sealed class TransferV3SnapshotFileEvidence(
    TransferV3FileStat stat,
    byte[] rawSha256,
    byte[] bytes)
{
    internal TransferV3FileStat Stat { get; } = stat;
    internal byte[] RawSha256 { get; } = rawSha256;
    internal byte[] Bytes { get; private set; } = bytes;

    internal void ClearBytes()
    {
        if (Bytes.Length > 0)
        {
            CryptographicOperations.ZeroMemory(Bytes);
            Bytes = [];
        }
    }

    internal void Clear()
    {
        CryptographicOperations.ZeroMemory(RawSha256);
        ClearBytes();
    }
}

internal readonly struct TransferV3SnapshotPath
{
    internal TransferV3SnapshotPath(
        string rootPath,
        string parentPath,
        string rootName)
    {
        RootPath = rootPath;
        ParentPath = parentPath;
        RootName = rootName;
    }

    internal string RootPath { get; }
    internal string ParentPath { get; }
    internal string RootName { get; }
}

internal sealed class TransferV3SnapshotReadException : IOException
{
    internal TransferV3SnapshotReadException(string code)
        : base($"Transfer-v3 snapshot read failed ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }

    internal ImmutableArray<string> CleanupCodes
    {
        get
        {
            try
            {
                return Data["TransferV3CleanupCodes"] is IEnumerable<string> codes
                    ? [.. codes]
                    : [];
            }
            catch
            {
                return [];
            }
        }
    }
}

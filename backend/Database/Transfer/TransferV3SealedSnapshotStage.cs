using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3SealedSnapshotStageFaultPoint
{
    AfterFixedFileCopied,
    AfterBlobFileClosed,
    AfterFileSealed,
    AfterDirectorySealed,
    AfterRootSealed,
    AfterFinalVerification,
}

internal sealed record TransferV3SealedSnapshotStageHooks(
    Action<string>? ObservePrivateRoot = null,
    Action<TransferV3SealedSnapshotStageFaultPoint, int?>? AfterFaultPoint = null,
    Func<int, byte[]>? RootNonceForAttempt = null);

internal sealed class TransferV3SealedSnapshotStageException : IOException
{
    internal TransferV3SealedSnapshotStageException(string code)
        : base($"Transfer-v3 sealed stage failed ({code}).")
    {
        Code = code;
    }

    internal string Code { get; }

    internal IReadOnlyList<string> CleanupCodes
    {
        get
        {
            try
            {
                return Data["TransferV3CleanupCodes"] is IEnumerable<string> codes
                    ? codes.ToArray()
                    : [];
            }
            catch
            {
                return [];
            }
        }
    }
}

internal sealed record TransferV3SealedSnapshotStageResidueAudit(
    int CandidateDirectories,
    int UnknownPrefixedEntries,
    int UnreadableEntries);

internal sealed class TransferV3SealedSnapshotStage : IDisposable
{
    private const string ManifestFileName = "manifest.json";
    private const string BlobDirectoryName = "blobs";
    private const string PrivateNamePrefix = ".nzbdav-transfer-v3-stage-";
    private const int RootNonceBytes = 16;
    private const int RootCreateAttempts = 8;
    private const int CopyBufferBytes = 64 * 1024;
    private const int MaximumIssuedReads = 64;
    private const int BlobTableOrdinal = 27;
    private const int ManifestOrdinal = 28;
    private const int BlobDescriptorBytes = sizeof(long) + SHA256.HashSizeInBytes;
    private const uint PermissionMask = 0x1ff;
    private const uint PermissionAndSpecialBitsMask = 0xfff;
    private const uint FileTypeMask = 0xf000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryType = 0x4000;
    private const uint SealedFileMode = 0x100; // 0400
    private const uint SealedDirectoryMode = 0x140; // 0500

    private readonly object _gate = new();
    private readonly SafeFileHandle _parentDirectory;
    private readonly SafeFileHandle _rootDirectory;
    private readonly string _rootName;
    private readonly TransferV3FileIdentity _rootIdentity;
    private readonly TransferV3Manifest _manifest;
    private readonly List<OwnedFile> _ownedFiles = [];
    private readonly List<OwnedDirectory> _ownedDirectories = [];
    private readonly Dictionary<string, OwnedDirectory> _directoriesByKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, OwnedFile> _blobFiles = [];
    private readonly HashSet<TrackedReadStream> _issuedReads = [];
    private readonly OwnedFile?[] _fixedFiles;
    private byte[] _canonicalManifest;
    private TransferV3FileStat? _sealedRootStat;
    private bool _disposed;
    private bool _cleanupStarted;

    private sealed class OwnedDirectory(
        string[] components,
        TransferV3FileIdentity identity)
    {
        internal string[] Components { get; } = components;
        internal TransferV3FileIdentity Identity { get; } = identity;
        internal TransferV3FileStat? SealedStat { get; set; }
        internal string Name => Components[^1];
        internal int Depth => Components.Length;
    }

    private sealed class OwnedFile(
        string[] parentComponents,
        string name,
        TransferV3FileIdentity identity)
    {
        internal string[] ParentComponents { get; } = parentComponents;
        internal string Name { get; } = name;
        internal TransferV3FileIdentity Identity { get; } = identity;
        internal long Length { get; private set; } = -1;
        internal byte[] RawSha256 { get; private set; } = [];
        internal TransferV3FileStat? SealedStat { get; set; }

        internal void SetReceipt(long length, byte[] rawSha256)
        {
            if (Length >= 0 || RawSha256.Length != 0
                || length < 0 || rawSha256.Length != SHA256.HashSizeInBytes)
            {
                CryptographicOperations.ZeroMemory(rawSha256);
                throw Failure("file-receipt");
            }
            Length = length;
            RawSha256 = rawSha256;
        }

        internal void ClearReceipt()
        {
            CryptographicOperations.ZeroMemory(RawSha256);
            RawSha256 = [];
        }
    }

    private sealed class BlobOutput(
        OwnedFile file,
        SafeFileHandle parent,
        TransferV3DurableFileStream stream)
    {
        private bool _closed;

        internal OwnedFile File { get; } = file;
        internal SafeFileHandle Parent { get; } = parent;
        internal TransferV3DurableFileStream Stream { get; } = stream;

        internal void CloseDurably()
        {
            if (_closed) return;
            _closed = true;
            Stream.Dispose();
        }

        internal void AbortNoThrow()
        {
            try
            {
                CloseDurably();
            }
            catch
            {
                // The parser's authoritative failure must remain primary.
            }
            try
            {
                Parent.Dispose();
            }
            catch
            {
                // Construction cleanup will report any retained residue.
            }
        }
    }

    private TransferV3SealedSnapshotStage(
        SafeFileHandle parentDirectory,
        SafeFileHandle rootDirectory,
        string rootName,
        TransferV3FileIdentity rootIdentity,
        TransferV3Manifest manifest,
        byte[] canonicalManifest)
    {
        _parentDirectory = parentDirectory;
        _rootDirectory = rootDirectory;
        _rootName = rootName;
        _rootIdentity = rootIdentity;
        _manifest = manifest;
        _canonicalManifest = canonicalManifest;
        _fixedFiles = new OwnedFile?[manifest.Tables.Length + 2];
    }

    internal TransferV3Manifest Manifest
    {
        get
        {
            lock (_gate)
            {
                EnsureOpen();
                return _manifest;
            }
        }
    }

    internal static async Task<TransferV3SealedSnapshotStage> CreateAsync(
        TransferV3VerifiedSnapshot source,
        string trustedParent,
        TransferV3SealedSnapshotStageHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedParent);
        if (!TransferV3Posix.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Transfer-v3 sealed stages require a verified POSIX ABI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle? parent = null;
        SafeFileHandle? root = null;
        TransferV3SealedSnapshotStage? stage = null;
        byte[]? canonicalManifest = null;
        var createdWithoutOwner = false;
        Exception? primary = null;
        try
        {
            VerifySourceUnchanged(source, cancellationToken);
            var manifest = source.Manifest;
            if (manifest.Tables.Length != BlobTableOrdinal)
                throw Failure("manifest-shape");
            canonicalManifest = source.GetCanonicalManifestCopy();

            var parentLocation = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(trustedParent));
            parent = TransferV3Posix.OpenDirectory(parentLocation);
            ValidateTrustedParent(TransferV3Posix.GetFileStat(parent));

            for (var attempt = 0; attempt < RootCreateAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? nonce = null;
                string candidate;
                try
                {
                    nonce = hooks?.RootNonceForAttempt?.Invoke(attempt)
                        ?? RandomNumberGenerator.GetBytes(RootNonceBytes);
                    if (nonce.Length != RootNonceBytes)
                        throw Failure("root-nonce");
                    candidate = PrivateNamePrefix + Convert.ToHexStringLower(nonce);
                }
                finally
                {
                    if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
                }

                if (TransferV3Posix.EntryExistsNoFollow(parent, candidate))
                    continue;
                var candidateCreated = false;
                try
                {
                    TransferV3Posix.CreateDirectoryAt(
                        parent,
                        candidate,
                        out candidateCreated);
                    createdWithoutOwner = candidateCreated;
                }
                catch (IOException) when (
                    !candidateCreated
                    && TransferV3Posix.EntryExistsNoFollow(parent, candidate))
                {
                    continue;
                }
                catch
                {
                    createdWithoutOwner = candidateCreated;
                    throw;
                }

                root = TransferV3Posix.OpenDirectoryAt(parent, candidate);
                var identity = TransferV3Posix.GetIdentity(root);
                stage = new TransferV3SealedSnapshotStage(
                    parent,
                    root,
                    candidate,
                    identity,
                    manifest,
                    canonicalManifest);
                parent = null;
                root = null;
                canonicalManifest = null;
                createdWithoutOwner = false;

                TransferV3Posix.SetMode(
                    stage._rootDirectory,
                    TransferV3Posix.PrivateDirectoryMode);
                TransferV3Posix.Sync(stage._rootDirectory);
                TransferV3Posix.Sync(stage._parentDirectory);
                hooks?.ObservePrivateRoot?.Invoke(
                    Path.Combine(parentLocation, candidate));
                await stage.BuildAsync(source, hooks, cancellationToken)
                    .ConfigureAwait(false);
                return stage;
            }

            throw Failure("root-collision");
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.CancellationToken == cancellationToken)
        {
            primary = exception;
        }
        catch (TransferV3SnapshotReadException)
        {
            primary = Failure("source-changed");
        }
        catch (TransferV3SealedSnapshotStageException exception)
        {
            primary = exception;
        }
        catch (Exception exception)
        {
            primary = exception;
        }

        var cleanupCodes = new List<string>();
        if (stage is not null)
        {
            cleanupCodes.AddRange(stage.AbortConstruction());
        }
        else
        {
            if (createdWithoutOwner)
                TransferV3Posix.AddCleanupCode(cleanupCodes, "untracked-root-residue");
            try
            {
                root?.Dispose();
            }
            catch
            {
                TransferV3Posix.AddCleanupCode(cleanupCodes, "root-close-failed");
            }
            try
            {
                parent?.Dispose();
            }
            catch
            {
                TransferV3Posix.AddCleanupCode(cleanupCodes, "parent-close-failed");
            }
            if (canonicalManifest is not null)
                CryptographicOperations.ZeroMemory(canonicalManifest);
        }

        TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary!, cleanupCodes);
        throw new InvalidOperationException("Unreachable sealed-stage creation path.");
    }

    internal static TransferV3SealedSnapshotStageResidueAudit AuditResidues(
        string trustedParent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedParent);
        if (!TransferV3Posix.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Transfer-v3 sealed-stage residue audit requires a verified POSIX ABI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parentLocation = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(trustedParent));
        using var parent = TransferV3Posix.OpenDirectory(parentLocation);
        ValidateTrustedParent(TransferV3Posix.GetFileStat(parent));
        var candidates = 0;
        var unknown = 0;
        var unreadable = 0;
        foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is null)
            {
                unreadable = checked(unreadable + 1);
                continue;
            }
            if (!IsPrivateStageName(entry.Name)) continue;

            SafeFileHandle? directory = null;
            try
            {
                directory = TransferV3Posix.OpenDirectoryAt(parent, entry.Name);
                var stat = TransferV3Posix.GetFileStat(directory);
                var mode = stat.Mode & PermissionMask;
                if ((stat.Mode & FileTypeMask) == DirectoryType
                    && mode is TransferV3Posix.PrivateDirectoryMode
                        or SealedDirectoryMode)
                {
                    candidates = checked(candidates + 1);
                }
                else
                {
                    unknown = checked(unknown + 1);
                }
            }
            catch (IOException)
            {
                unknown = checked(unknown + 1);
            }
            finally
            {
                directory?.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new TransferV3SealedSnapshotStageResidueAudit(
            candidates,
            unknown,
            unreadable);
    }

    internal Stream OpenTableRead(int ordinal)
    {
        lock (_gate)
        {
            EnsureOpen();
            if ((uint)ordinal >= (uint)_manifest.Tables.Length)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            return IssueRead(_fixedFiles[ordinal]
                ?? throw Failure("stage-incomplete"));
        }
    }

    internal Stream OpenBlobBundleRead()
    {
        lock (_gate)
        {
            EnsureOpen();
            return IssueRead(_fixedFiles[BlobTableOrdinal]
                ?? throw Failure("stage-incomplete"));
        }
    }

    internal Stream OpenBlobRead(Guid id)
    {
        lock (_gate)
        {
            EnsureOpen();
            if (!_blobFiles.TryGetValue(id, out var file))
                throw Failure("blob-not-found");
            return IssueRead(file);
        }
    }

    internal byte[] GetCanonicalManifestCopy()
    {
        lock (_gate)
        {
            EnsureOpen();
            return _canonicalManifest.ToArray();
        }
    }

    public void Dispose()
    {
        TrackedReadStream[] issued;
        var cleanupCodes = new List<string>();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            issued = [.. _issuedReads];
            _issuedReads.Clear();
        }
        foreach (var stream in issued)
        {
            TryCleanup(cleanupCodes, "issued-read-close-failed", stream.Revoke);
        }

        cleanupCodes.AddRange(CleanupOwnedEntries());
        if (cleanupCodes.Count == 0) return;
        TransferV3Posix.ThrowPrimaryWithCleanupCodes(
            Failure("cleanup"),
            cleanupCodes);
    }

    private async Task BuildAsync(
        TransferV3VerifiedSnapshot source,
        TransferV3SealedSnapshotStageHooks? hooks,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < _manifest.Tables.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = source.OpenTableRead(ordinal);
            var file = await CopyFixedFileAsync(
                    input,
                    _manifest.Tables[ordinal].File,
                    cancellationToken)
                .ConfigureAwait(false);
            _fixedFiles[ordinal] = file;
            hooks?.AfterFaultPoint?.Invoke(
                TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied,
                ordinal);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var input = source.OpenBlobBundleRead())
        {
            _fixedFiles[BlobTableOrdinal] = await CopyFixedFileAsync(
                    input,
                    _manifest.Blobs.File,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        hooks?.AfterFaultPoint?.Invoke(
            TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied,
            BlobTableOrdinal);

        using (var input = new MemoryStream(_canonicalManifest, writable: false))
        {
            _fixedFiles[ManifestOrdinal] = await CopyFixedFileAsync(
                    input,
                    ManifestFileName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        hooks?.AfterFaultPoint?.Invoke(
            TransferV3SealedSnapshotStageFaultPoint.AfterFixedFileCopied,
            ManifestOrdinal);

        CreateOwnedDirectory([BlobDirectoryName]);
        var limits = new TransferV3Limits(
            _manifest.Limits.MaxFieldBytes,
            _manifest.Limits.MaxBatchRows,
            _manifest.Limits.MaxBatchBytes);
        using (var bundle = AcquireUntrackedRead(
                   _fixedFiles[BlobTableOrdinal]
                   ?? throw Failure("stage-incomplete")))
        using (var observer = new BlobReconstructionObserver(
                   this,
                   _manifest.Blobs,
                   limits,
                   hooks,
                   cancellationToken))
        {
            await TransferV3JsonlParser.ParseAsync(
                    bundle,
                    limits,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        VerifySourceUnchanged(source, cancellationToken);
        await SealAsync(hooks, cancellationToken).ConfigureAwait(false);
        await ValidateExactSealedTreeAsync(cancellationToken).ConfigureAwait(false);
        hooks?.AfterFaultPoint?.Invoke(
            TransferV3SealedSnapshotStageFaultPoint.AfterFinalVerification,
            null);
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateExactSealedTreeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<OwnedFile> CopyFixedFileAsync(
        Stream input,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = TransferV3Posix.CreateFileAt(
            _rootDirectory,
            name,
            out var identity);
        var file = new OwnedFile([], name, identity);
        _ownedFiles.Add(file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long length = 0;
        try
        {
            using (var output = new TransferV3DurableFileStream(handle))
            {
                handle = null!;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    length = checked(length + read);
                }
            }
            TransferV3Posix.Sync(_rootDirectory);
            var digest = hash.GetHashAndReset();
            file.SetReceipt(length, digest);
            await VerifyOwnedFileAsync(
                    file,
                    TransferV3Posix.PrivateFileMode,
                    cancellationToken)
                .ConfigureAwait(false);
            return file;
        }
        finally
        {
            handle?.Dispose();
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private OwnedDirectory CreateOwnedDirectory(string[] components)
    {
        var key = DirectoryKey(components);
        if (_directoriesByKey.TryGetValue(key, out var existing))
        {
            using var verification = AcquireDirectory(components);
            return existing;
        }

        var parentComponents = components[..^1];
        using var parent = AcquireDirectory(parentComponents);
        var name = components[^1];
        if (TransferV3Posix.EntryExistsNoFollow(parent, name))
            throw Failure("directory-collision");
        var created = false;
        try
        {
            TransferV3Posix.CreateDirectoryAt(parent, name, out created);
        }
        catch when (created)
        {
            throw Failure("untracked-directory-residue");
        }
        SafeFileHandle? child = null;
        try
        {
            child = TransferV3Posix.OpenDirectoryAt(parent, name);
            var identity = TransferV3Posix.GetIdentity(child);
            var directory = new OwnedDirectory(components.ToArray(), identity);
            _ownedDirectories.Add(directory);
            _directoriesByKey.Add(key, directory);
            TransferV3Posix.SetMode(child, TransferV3Posix.PrivateDirectoryMode);
            TransferV3Posix.Sync(child);
            TransferV3Posix.Sync(parent);
            return directory;
        }
        catch
        {
            if (child is null)
                throw Failure("untracked-directory-residue");
            throw;
        }
        finally
        {
            child?.Dispose();
        }
    }

    private BlobOutput BeginBlob(Guid id)
    {
        if (_blobFiles.ContainsKey(id)) throw Failure("blob-duplicate");
        var normalized = id.ToString("N");
        var first = normalized[..2];
        var second = normalized.Substring(2, 2);
        CreateOwnedDirectory([BlobDirectoryName, first]);
        var parentComponents = new[] { BlobDirectoryName, first, second };
        CreateOwnedDirectory(parentComponents);
        var parent = AcquireDirectory(parentComponents);
        SafeFileHandle? handle = null;
        try
        {
            var name = id.ToString("D");
            handle = TransferV3Posix.CreateFileAt(parent, name, out var identity);
            var file = new OwnedFile(parentComponents, name, identity);
            _ownedFiles.Add(file);
            var stream = new TransferV3DurableFileStream(handle);
            handle = null;
            return new BlobOutput(file, parent, stream);
        }
        catch
        {
            handle?.Dispose();
            parent.Dispose();
            throw;
        }
    }

    private void CompleteBlob(
        Guid id,
        BlobOutput output,
        long length,
        ReadOnlySpan<byte> rawSha256,
        TransferV3SealedSnapshotStageHooks? hooks,
        long ordinal)
    {
        output.CloseDurably();
        TransferV3Posix.Sync(output.Parent);
        var receipt = rawSha256.ToArray();
        output.File.SetReceipt(length, receipt);
        VerifyOwnedFile(output.File, TransferV3Posix.PrivateFileMode);
        if (!_blobFiles.TryAdd(id, output.File))
            throw Failure("blob-duplicate");
        output.Parent.Dispose();
        hooks?.AfterFaultPoint?.Invoke(
            TransferV3SealedSnapshotStageFaultPoint.AfterBlobFileClosed,
            ordinal <= int.MaxValue ? checked((int)ordinal) : null);
    }

    private async Task SealAsync(
        TransferV3SealedSnapshotStageHooks? hooks,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < _ownedFiles.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = _ownedFiles[ordinal];
            await VerifyOwnedFileAsync(
                    file,
                    TransferV3Posix.PrivateFileMode,
                    cancellationToken)
                .ConfigureAwait(false);
            using var parent = AcquireDirectory(file.ParentComponents);
            using var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
            ValidateOwnedFileStat(
                file,
                TransferV3Posix.GetFileStat(handle),
                TransferV3Posix.PrivateFileMode);
            TransferV3Posix.SetMode(handle, SealedFileMode);
            TransferV3Posix.Sync(handle);
            TransferV3Posix.Sync(parent);
            await VerifyOwnedFileAsync(file, SealedFileMode, cancellationToken)
                .ConfigureAwait(false);
            hooks?.AfterFaultPoint?.Invoke(
                TransferV3SealedSnapshotStageFaultPoint.AfterFileSealed,
                ordinal);
        }

        var directories = _ownedDirectories
            .OrderByDescending(directory => directory.Depth)
            .ThenBy(directory => DirectoryKey(directory.Components), StringComparer.Ordinal)
            .ToArray();
        for (var ordinal = 0; ordinal < directories.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories[ordinal];
            using var parent = AcquireDirectory(directory.Components[..^1]);
            using var handle = TransferV3Posix.OpenDirectoryAt(parent, directory.Name);
            ValidateOwnedDirectoryStat(
                directory,
                TransferV3Posix.GetFileStat(handle),
                TransferV3Posix.PrivateDirectoryMode);
            TransferV3Posix.SetMode(handle, SealedDirectoryMode);
            TransferV3Posix.Sync(handle);
            TransferV3Posix.Sync(parent);
            var sealedStat = TransferV3Posix.GetFileStat(handle);
            ValidateOwnedDirectoryStat(directory, sealedStat, SealedDirectoryMode);
            directory.SealedStat = sealedStat;
            hooks?.AfterFaultPoint?.Invoke(
                TransferV3SealedSnapshotStageFaultPoint.AfterDirectorySealed,
                ordinal);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TransferV3Posix.GetIdentity(_rootDirectory) != _rootIdentity)
            throw Failure("root-changed");
        TransferV3Posix.SetMode(_rootDirectory, SealedDirectoryMode);
        TransferV3Posix.Sync(_rootDirectory);
        TransferV3Posix.Sync(_parentDirectory);
        _sealedRootStat = TransferV3Posix.GetFileStat(_rootDirectory);
        ValidateRootStat(_sealedRootStat.Value, SealedDirectoryMode);
        hooks?.AfterFaultPoint?.Invoke(
            TransferV3SealedSnapshotStageFaultPoint.AfterRootSealed,
            null);
    }

    private async Task ValidateExactSealedTreeAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TransferV3Posix.EntryMatches(
                _parentDirectory,
                _rootName,
                _rootIdentity))
        {
            throw Failure("root-binding");
        }
        ValidateRootStat(TransferV3Posix.GetFileStat(_rootDirectory), SealedDirectoryMode);
        if (_sealedRootStat is null
            || TransferV3Posix.GetFileStat(_rootDirectory) != _sealedRootStat)
        {
            throw Failure("root-changed");
        }

        var allDirectoryComponents = new List<string[]> { Array.Empty<string>() };
        allDirectoryComponents.AddRange(_ownedDirectories.Select(value => value.Components));
        foreach (var components in allDirectoryComponents
                     .OrderBy(value => value.Length)
                     .ThenBy(DirectoryKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var directory = AcquireDirectory(components);
            var actual = TransferV3Posix.EnumerateDirectoryNames(directory)
                .ToHashSet(StringComparer.Ordinal);
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in _ownedDirectories)
            {
                if (ParentMatches(child.Components, components))
                    expected.Add(child.Name);
            }
            foreach (var file in _ownedFiles)
            {
                if (file.ParentComponents.SequenceEqual(components, StringComparer.Ordinal))
                    expected.Add(file.Name);
            }
            if (!actual.SetEquals(expected)) throw Failure("entry-set");
        }

        foreach (var file in _ownedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyOwnedFileAsync(file, SealedFileMode, cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (var directory in _ownedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = AcquireDirectory(directory.Components);
            var stat = TransferV3Posix.GetFileStat(handle);
            ValidateOwnedDirectoryStat(directory, stat, SealedDirectoryMode);
            if (directory.SealedStat is not null && stat != directory.SealedStat)
                throw Failure("directory-changed");
        }
    }

    private Stream IssueRead(OwnedFile file)
    {
        if (_issuedReads.Count >= MaximumIssuedReads)
            throw Failure("read-limit");
        SafeFileHandle? handle = null;
        try
        {
            using var parent = AcquireDirectory(file.ParentComponents);
            handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
            var stat = TransferV3Posix.GetFileStat(handle);
            ValidateOwnedFileStat(file, stat, SealedFileMode);
            if (file.SealedStat is null || stat != file.SealedStat)
                throw Failure("file-changed");
            var inner = new TransferV3RandomAccessReadStream(handle, file.Length);
            handle = null;
            var tracked = new TrackedReadStream(inner, this);
            _issuedReads.Add(tracked);
            return tracked;
        }
        catch (TransferV3SealedSnapshotStageException)
        {
            handle?.Dispose();
            throw;
        }
        catch
        {
            handle?.Dispose();
            throw Failure("stage-changed");
        }
    }

    private Stream AcquireUntrackedRead(OwnedFile file)
    {
        using var parent = AcquireDirectory(file.ParentComponents);
        var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
        try
        {
            ValidateOwnedFileStat(
                file,
                TransferV3Posix.GetFileStat(handle),
                TransferV3Posix.PrivateFileMode);
            var stream = new TransferV3RandomAccessReadStream(handle, file.Length);
            handle = null!;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private SafeFileHandle AcquireDirectory(string[] components)
    {
        SafeFileHandle? current = TransferV3Posix.DuplicateHandle(_rootDirectory);
        try
        {
            if (TransferV3Posix.GetIdentity(current) != _rootIdentity)
                throw Failure("root-changed");
            if (!_cleanupStarted && _sealedRootStat is not null
                && TransferV3Posix.GetFileStat(current) != _sealedRootStat)
            {
                throw Failure("root-changed");
            }
            if (components.Length == 0)
            {
                var result = current;
                current = null;
                return result;
            }

            var traversed = new List<string>(components.Length);
            foreach (var component in components)
            {
                traversed.Add(component);
                if (!_directoriesByKey.TryGetValue(
                        DirectoryKey([.. traversed]),
                        out var expected))
                {
                    throw Failure("directory-unowned");
                }
                var next = TransferV3Posix.OpenDirectoryAt(current, component);
                current.Dispose();
                current = next;
                var stat = TransferV3Posix.GetFileStat(current);
                if (stat.Fingerprint.Identity != expected.Identity
                    || !_cleanupStarted && expected.SealedStat is not null
                    && stat != expected.SealedStat)
                {
                    throw Failure("directory-changed");
                }
            }
            var owned = current;
            current = null;
            return owned;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private async Task VerifyOwnedFileAsync(
        OwnedFile file,
        uint expectedMode,
        CancellationToken cancellationToken)
    {
        using var parent = AcquireDirectory(file.ParentComponents);
        using var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
        var evidence = await HashFileAsync(handle, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwnedFileStat(file, evidence.Stat, expectedMode);
            if (!CryptographicOperations.FixedTimeEquals(
                    evidence.RawSha256,
                    file.RawSha256))
            {
                throw Failure("file-digest");
            }
            if (expectedMode == SealedFileMode)
            {
                if (file.SealedStat is not null && evidence.Stat != file.SealedStat)
                    throw Failure("file-changed");
                file.SealedStat ??= evidence.Stat;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(evidence.RawSha256);
        }
    }

    private void VerifyOwnedFile(OwnedFile file, uint expectedMode)
    {
        using var parent = AcquireDirectory(file.ParentComponents);
        using var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
        var evidence = HashFile(handle);
        try
        {
            ValidateOwnedFileStat(file, evidence.Stat, expectedMode);
            if (!CryptographicOperations.FixedTimeEquals(
                    evidence.RawSha256,
                    file.RawSha256))
            {
                throw Failure("file-digest");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(evidence.RawSha256);
        }
    }

    private static async Task<FileEvidence> HashFileAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        var before = TransferV3Posix.GetFileStat(handle);
        if (before.Fingerprint.Size < 0) throw Failure("file-stat");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long offset = 0;
        try
        {
            while (offset < before.Fingerprint.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = checked((int)Math.Min(
                    buffer.Length,
                    before.Fingerprint.Size - offset));
                var read = await RandomAccess.ReadAsync(
                        handle,
                        buffer.AsMemory(0, request),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw Failure("file-truncated");
                hash.AppendData(buffer.AsSpan(0, read));
                offset = checked(offset + read);
            }
            if (await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, 1),
                    offset,
                    cancellationToken)
                .ConfigureAwait(false) != 0)
            {
                throw Failure("file-trailing");
            }
            var after = TransferV3Posix.GetFileStat(handle);
            if (before != after) throw Failure("file-changed");
            return new FileEvidence(after, hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static FileEvidence HashFile(SafeFileHandle handle)
    {
        var before = TransferV3Posix.GetFileStat(handle);
        if (before.Fingerprint.Size < 0) throw Failure("file-stat");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        long offset = 0;
        try
        {
            while (offset < before.Fingerprint.Size)
            {
                var request = checked((int)Math.Min(
                    buffer.Length,
                    before.Fingerprint.Size - offset));
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, request), offset);
                if (read == 0) throw Failure("file-truncated");
                hash.AppendData(buffer.AsSpan(0, read));
                offset = checked(offset + read);
            }
            if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), offset) != 0)
                throw Failure("file-trailing");
            var after = TransferV3Posix.GetFileStat(handle);
            if (before != after) throw Failure("file-changed");
            return new FileEvidence(after, hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ValidateOwnedFileStat(
        OwnedFile file,
        TransferV3FileStat stat,
        uint expectedMode)
    {
        if (stat.Fingerprint.Identity != file.Identity
            || stat.Fingerprint.Size != file.Length
            || stat.LinkCount != 1
            || (stat.Mode & FileTypeMask) != RegularFileType
            || (stat.Mode & PermissionAndSpecialBitsMask) != expectedMode)
        {
            throw Failure("file-binding");
        }
    }

    private static void ValidateOwnedDirectoryStat(
        OwnedDirectory directory,
        TransferV3FileStat stat,
        uint expectedMode)
    {
        if (stat.Fingerprint.Identity != directory.Identity
            || (stat.Mode & FileTypeMask) != DirectoryType
            || (stat.Mode & PermissionAndSpecialBitsMask) != expectedMode)
        {
            throw Failure("directory-binding");
        }
    }

    private void ValidateRootStat(TransferV3FileStat stat, uint expectedMode)
    {
        if (stat.Fingerprint.Identity != _rootIdentity
            || (stat.Mode & FileTypeMask) != DirectoryType
            || (stat.Mode & PermissionAndSpecialBitsMask) != expectedMode)
        {
            throw Failure("root-binding");
        }
    }

    private static void ValidateTrustedParent(TransferV3FileStat stat)
    {
        var permissions = stat.Mode & PermissionMask;
        if ((stat.Mode & FileTypeMask) != DirectoryType
            || (permissions & 0x1c0) != 0x1c0
            || (permissions & 0x12) != 0)
        {
            throw Failure("parent-mode");
        }
    }

    private static void VerifySourceUnchanged(
        TransferV3VerifiedSnapshot source,
        CancellationToken cancellationToken)
    {
        try
        {
            source.VerifyUnchanged(cancellationToken);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested
            && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch (TransferV3SnapshotReadException)
        {
            throw;
        }
        catch
        {
            throw Failure("source-changed");
        }
    }

    private IReadOnlyList<string> AbortConstruction()
    {
        var codes = new List<string>();
        lock (_gate)
        {
            if (!_disposed) _disposed = true;
            foreach (var stream in _issuedReads)
            {
                TryCleanup(codes, "issued-read-close-failed", stream.Revoke);
            }
            _issuedReads.Clear();
        }
        codes.AddRange(CleanupOwnedEntries());
        return codes;
    }

    private List<string> CleanupOwnedEntries()
    {
        var codes = new List<string>();
        lock (_gate)
        {
            if (_cleanupStarted) return codes;
            _cleanupStarted = true;
        }
        var ownedFileIdentities = _ownedFiles
            .Select(file => file.Identity)
            .ToHashSet();

        TryCleanup(codes, "root-mode-restore-failed", () =>
        {
            if (TransferV3Posix.GetIdentity(_rootDirectory) != _rootIdentity)
                throw new IOException();
            TransferV3Posix.SetMode(
                _rootDirectory,
                TransferV3Posix.PrivateDirectoryMode);
            TransferV3Posix.Sync(_rootDirectory);
            TransferV3Posix.Sync(_parentDirectory);
        });

        foreach (var directory in _ownedDirectories
                     .OrderBy(value => value.Depth)
                     .ThenBy(value => DirectoryKey(value.Components), StringComparer.Ordinal))
        {
            TryCleanup(codes, "directory-mode-restore-failed", () =>
            {
                using var parent = AcquireDirectory(directory.Components[..^1]);
                using var handle = TransferV3Posix.OpenDirectoryAt(parent, directory.Name);
                if (TransferV3Posix.GetIdentity(handle) != directory.Identity)
                    throw new IOException();
                TransferV3Posix.SetMode(
                    handle,
                    TransferV3Posix.PrivateDirectoryMode);
                TransferV3Posix.Sync(handle);
                TransferV3Posix.Sync(parent);
            });
        }

        foreach (var file in _ownedFiles)
        {
            TryCleanup(codes, "file-mode-restore-failed", () =>
            {
                using var parent = AcquireDirectory(file.ParentComponents);
                using var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(parent, file.Name);
                if (TransferV3Posix.GetIdentity(handle) != file.Identity)
                    throw new IOException();
                TransferV3Posix.SetMode(handle, TransferV3Posix.PrivateFileMode);
                TransferV3Posix.Sync(handle);
                TransferV3Posix.Sync(parent);
            });
        }

        TryCleanup(codes, "hard-link-audit-failed", () =>
            AuditExternalHardLinks(codes));

        foreach (var file in _ownedFiles
                     .OrderByDescending(value => value.ParentComponents.Length))
        {
            TryCleanup(codes, "owned-file-unlink-failed", () =>
            {
                using var parent = AcquireDirectory(file.ParentComponents);
                var result = TransferV3Posix.TryUnlinkOwnedRegularFile(
                    parent,
                    file.Name,
                    file.Identity);
                if (result != TransferV3OwnedFileUnlinkResult.Removed)
                {
                    TransferV3Posix.AddCleanupCode(
                        codes,
                        result == TransferV3OwnedFileUnlinkResult.Missing
                            ? "owned-file-missing"
                            : "owned-file-replaced");
                    return;
                }
                TransferV3Posix.Sync(parent);
            });
        }

        foreach (var directory in _ownedDirectories
                     .OrderByDescending(value => value.Depth)
                     .ThenByDescending(value => DirectoryKey(value.Components), StringComparer.Ordinal))
        {
            TryCleanup(codes, "owned-directory-unlink-failed", () =>
            {
                using (var owned = AcquireDirectory(directory.Components))
                {
                    RemoveIdentityProvenAliases(
                        owned,
                        ownedFileIdentities,
                        codes);
                }
                using var parent = AcquireDirectory(directory.Components[..^1]);
                var result = TransferV3Posix.TryUnlinkOwnedDirectory(
                    parent,
                    directory.Name,
                    directory.Identity);
                if (result != TransferV3OwnedDirectoryUnlinkResult.Removed)
                {
                    TransferV3Posix.AddCleanupCode(
                        codes,
                        result == TransferV3OwnedDirectoryUnlinkResult.Missing
                            ? "owned-directory-missing"
                            : "owned-directory-replaced");
                    return;
                }
                TransferV3Posix.Sync(parent);
            });
        }

        TryCleanup(codes, "owned-root-unlink-failed", () =>
        {
            RemoveIdentityProvenAliases(
                _rootDirectory,
                ownedFileIdentities,
                codes);
            var result = TransferV3Posix.TryUnlinkOwnedDirectory(
                _parentDirectory,
                _rootName,
                _rootIdentity);
            if (result != TransferV3OwnedDirectoryUnlinkResult.Removed)
            {
                TransferV3Posix.AddCleanupCode(
                    codes,
                    result == TransferV3OwnedDirectoryUnlinkResult.Missing
                        ? "owned-root-missing"
                        : "owned-root-replaced");
                return;
            }
            TransferV3Posix.Sync(_parentDirectory);
        });

        TryCleanup(codes, "root-close-failed", _rootDirectory.Dispose);
        TryCleanup(codes, "parent-close-failed", _parentDirectory.Dispose);
        CryptographicOperations.ZeroMemory(_canonicalManifest);
        _canonicalManifest = [];
        foreach (var file in _ownedFiles) file.ClearReceipt();
        return codes;
    }

    private void AuditExternalHardLinks(List<string> cleanupCodes)
    {
        var identities = _ownedFiles
            .Select(file => file.Identity)
            .ToHashSet();
        var internalLinks = identities.ToDictionary(identity => identity, _ => 0UL);
        var observedLinks = new Dictionary<TransferV3FileIdentity, ulong>();
        var directories = new List<string[]> { Array.Empty<string>() };
        directories.AddRange(_ownedDirectories.Select(directory => directory.Components));

        foreach (var components in directories)
        {
            SafeFileHandle? directory = null;
            try
            {
                directory = AcquireDirectory(components);
                foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(directory))
                {
                    if (entry.Name is null) continue;
                    SafeFileHandle? candidate = null;
                    try
                    {
                        candidate = TransferV3Posix.OpenReadOnlyRegularFileAt(
                            directory,
                            entry.Name);
                        var stat = TransferV3Posix.GetFileStat(candidate);
                        var identity = stat.Fingerprint.Identity;
                        if (!identities.Contains(identity)) continue;
                        internalLinks[identity] = checked(internalLinks[identity] + 1);
                        if (observedLinks.TryGetValue(identity, out var previous)
                            && previous != stat.LinkCount)
                        {
                            TransferV3Posix.AddCleanupCode(
                                cleanupCodes,
                                "hard-link-audit-inconsistent");
                        }
                        observedLinks[identity] = stat.LinkCount;
                    }
                    catch (IOException)
                    {
                        // Directories and no-follow non-regular entries are not links
                        // to an owned regular-file identity.
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }
                }
            }
            catch
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "hard-link-audit-incomplete");
            }
            finally
            {
                directory?.Dispose();
            }
        }

        foreach (var (identity, linkCount) in observedLinks)
        {
            if (linkCount > internalLinks[identity])
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "external-hard-link-residue");
            }
            else if (linkCount < internalLinks[identity])
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "hard-link-audit-inconsistent");
            }
        }

        if (identities.Any(identity => !observedLinks.ContainsKey(identity)))
        {
            // Once every owned pathname for an identity has disappeared, no
            // descriptor-free audit can distinguish a fully unlinked inode
            // from one retained solely by an external hard link. Report the
            // conservative possibility instead of silently claiming cleanup.
            TransferV3Posix.AddCleanupCode(
                cleanupCodes,
                "possible-external-hard-link-residue");
        }
    }

    private static void RemoveIdentityProvenAliases(
        SafeFileHandle directory,
        IReadOnlySet<TransferV3FileIdentity> ownedIdentities,
        List<string> cleanupCodes)
    {
        var names = new List<string>();
        foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(directory))
        {
            if (entry.Name is null)
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "unknown-entry-residue");
            }
            else
            {
                names.Add(entry.Name);
            }
        }
        names.Sort(StringComparer.Ordinal);

        foreach (var name in names)
        {
            SafeFileHandle? candidate = null;
            try
            {
                candidate = TransferV3Posix.OpenReadOnlyRegularFileAt(directory, name);
                var identity = TransferV3Posix.GetIdentity(candidate);
                if (!ownedIdentities.Contains(identity))
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "unknown-entry-residue");
                    continue;
                }

                var result = TransferV3Posix.TryUnlinkOwnedRegularFile(
                    directory,
                    name,
                    identity);
                if (result == TransferV3OwnedFileUnlinkResult.UnknownEntry)
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "identity-proven-alias-replaced");
                }
                else if (result == TransferV3OwnedFileUnlinkResult.Removed)
                {
                    TransferV3Posix.Sync(directory);
                }
            }
            catch (IOException)
            {
                if (TransferV3Posix.EntryExistsNoFollow(directory, name))
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "unknown-entry-residue");
                }
            }
            finally
            {
                candidate?.Dispose();
            }
        }
    }

    private static void TryCleanup(List<string> codes, string code, Action action)
    {
        try
        {
            action();
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(codes, code);
        }
    }

    private void Untrack(TrackedReadStream stream)
    {
        lock (_gate) _issuedReads.Remove(stream);
    }

    private void EnsureOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TransferV3SealedSnapshotStage));
    }

    private static bool ParentMatches(string[] candidate, string[] parent) =>
        candidate.Length == parent.Length + 1
        && candidate.AsSpan(0, parent.Length).SequenceEqual(parent);

    private static string DirectoryKey(string[] components) =>
        string.Join('/', components);

    private static bool IsPrivateStageName(string name)
    {
        if (!name.StartsWith(PrivateNamePrefix, StringComparison.Ordinal)
            || name.Length != PrivateNamePrefix.Length + 2 * RootNonceBytes)
        {
            return false;
        }
        foreach (var value in name.AsSpan(PrivateNamePrefix.Length))
        {
            if (value is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static TransferV3SealedSnapshotStageException Failure(string code) =>
        new(code);

    private readonly record struct FileEvidence(
        TransferV3FileStat Stat,
        byte[] RawSha256);

    private sealed class TrackedReadStream(
        Stream inner,
        TransferV3SealedSnapshotStage owner) : Stream
    {
        private readonly object _gate = new();
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => !_disposed && inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                EnsureOpen();
                return inner.Length;
            }
        }
        public override long Position
        {
            get
            {
                EnsureOpen();
                return inner.Position;
            }
            set
            {
                EnsureOpen();
                inner.Position = value;
            }
        }
        public override void Flush()
        {
            EnsureOpen();
            inner.Flush();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureOpen();
            return inner.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            EnsureOpen();
            return inner.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            EnsureOpen();
            return inner.Seek(offset, origin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException();

        internal void Revoke() => DisposeCore(notifyOwner: false);

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCore(notifyOwner: true);
            base.Dispose(disposing);
        }

        private void DisposeCore(bool notifyOwner)
        {
            var changed = false;
            try
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        changed = true;
                        inner.Dispose();
                    }
                }
            }
            finally
            {
                if (changed && notifyOwner) owner.Untrack(this);
            }
        }

        private void EnsureOpen()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TrackedReadStream));
        }
    }

    private sealed class BlobReconstructionObserver : ITransferV3FrameObserver, IDisposable
    {
        private readonly TransferV3SealedSnapshotStage _owner;
        private readonly TransferV3ManifestBlobs _manifest;
        private readonly TransferV3Limits _limits;
        private readonly TransferV3SealedSnapshotStageHooks? _hooks;
        private readonly CancellationToken _cancellationToken;
        private readonly IncrementalHash _inventoryHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private BlobRow? _row;
        private bool _batchOpen;
        private bool _completed;
        private bool _disposed;

        internal BlobReconstructionObserver(
            TransferV3SealedSnapshotStage owner,
            TransferV3ManifestBlobs manifest,
            TransferV3Limits limits,
            TransferV3SealedSnapshotStageHooks? hooks,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _manifest = manifest;
            _limits = limits;
            _hooks = hooks;
            _cancellationToken = cancellationToken;
        }

        private long Rows { get; set; }
        private long ContentBytes { get; set; }
        private long DecodedBytes { get; set; }

        public void Observe(TransferV3Frame frame)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(frame.Table, "Blobs", StringComparison.Ordinal))
                throw Failure("blob-table");
            switch (frame)
            {
                case TransferV3TableHeaderFrame { Version: TransferV3FrameCodec.FormatVersion }:
                    return;
                case TransferV3TableHeaderFrame:
                    throw Failure("blob-version");
                case TransferV3BatchStartFrame:
                    if (_batchOpen || _row is not null) throw Failure("blob-sequence");
                    _batchOpen = true;
                    return;
                case TransferV3RowFrame:
                    throw Failure("blob-inline-row");
                case TransferV3ChunkedRowStartFrame start:
                    if (!_batchOpen || _row is not null) throw Failure("blob-sequence");
                    IReadOnlyList<TransferV3CursorComponent> cursor;
                    try
                    {
                        cursor = TransferV3CursorCodec.Decode(start.Cursor);
                    }
                    catch (FormatException)
                    {
                        throw Failure("blob-cursor");
                    }
                    if (cursor.Count != 1
                        || cursor[0].Type != TransferV3CursorComponentType.Uuid)
                    {
                        throw Failure("blob-cursor");
                    }
                    _row = new BlobRow(
                        _owner,
                        cursor[0].UuidValue,
                        start.Cursor,
                        start.Fields,
                        _limits,
                        _hooks,
                        Rows,
                        _cancellationToken);
                    return;
                case TransferV3FieldChunkFrame chunk:
                    (_row ?? throw Failure("blob-sequence"))
                        .Append(chunk.Field, chunk.Chunk, chunk.Data.Span);
                    return;
                case TransferV3ChunkedRowEndFrame end:
                    if (_row is null
                        || !string.Equals(_row.Cursor, end.Cursor, StringComparison.Ordinal))
                    {
                        throw Failure("blob-cursor");
                    }
                    var fact = _row.Complete(end.Fields, end.Bytes);
                    _row.Dispose();
                    _row = null;
                    try
                    {
                        _inventoryHash.AppendData(fact.NetworkUuid);
                        Span<byte> length = stackalloc byte[sizeof(long)];
                        BinaryPrimitives.WriteInt64BigEndian(length, fact.Length);
                        _inventoryHash.AppendData(length);
                        _inventoryHash.AppendData(fact.ContentSha256);
                        Rows = checked(Rows + 1);
                        ContentBytes = checked(ContentBytes + fact.Length);
                        DecodedBytes = checked(DecodedBytes + end.Bytes);
                    }
                    finally
                    {
                        fact.Clear();
                    }
                    return;
                case TransferV3BatchEndFrame:
                    return;
                default:
                    throw Failure("blob-sequence");
            }
        }

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_batchOpen || _row is not null) throw Failure("blob-sequence");
            _batchOpen = false;
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_batchOpen || _row is not null || _completed)
                throw Failure("blob-sequence");
            var actual = _inventoryHash.GetHashAndReset();
            byte[]? expected = null;
            try
            {
                expected = Convert.FromHexString(_manifest.InventorySha256);
                if (!string.Equals(_manifest.Name, "Blobs", StringComparison.Ordinal)
                    || !string.Equals(_manifest.File, "Blobs.jsonl", StringComparison.Ordinal)
                    || !string.Equals(tableEnd.Table, _manifest.Name, StringComparison.Ordinal)
                    || tableEnd.Batches != _manifest.Batches
                    || tableEnd.Rows != _manifest.Rows
                    || tableEnd.Bytes != _manifest.DecodedBytes
                    || !string.Equals(tableEnd.Sha256, _manifest.Sha256, StringComparison.Ordinal)
                    || Rows != _manifest.Count
                    || Rows != tableEnd.Rows
                    || ContentBytes != _manifest.TotalBytes
                    || DecodedBytes != tableEnd.Bytes
                    || !CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    throw Failure("blob-manifest");
                }
            }
            catch (FormatException)
            {
                throw Failure("blob-manifest");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
                if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            }
            _completed = true;
        }

        public void Abort(Exception failure)
        {
            try
            {
                _row?.AbortNoThrow();
            }
            finally
            {
                _row = null;
                _batchOpen = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _row?.AbortNoThrow();
            _inventoryHash.Dispose();
        }

        private sealed class BlobRow : IDisposable
        {
            private readonly TransferV3SealedSnapshotStage _owner;
            private readonly Guid _id;
            private readonly int _declaredFields;
            private readonly TransferV3Limits _limits;
            private readonly TransferV3SealedSnapshotStageHooks? _hooks;
            private readonly long _ordinal;
            private readonly CancellationToken _cancellationToken;
            private readonly IncrementalHash _contentHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private BlobOutput? _output;
            private byte[]? _expectedContentSha256;
            private long _length = -1;
            private long _contentBytes;
            private long _currentFieldBytes;
            private int _currentField = -1;
            private bool _descriptorSeen;
            private bool _completed;
            private bool _disposed;

            internal BlobRow(
                TransferV3SealedSnapshotStage owner,
                Guid id,
                string cursor,
                int declaredFields,
                TransferV3Limits limits,
                TransferV3SealedSnapshotStageHooks? hooks,
                long ordinal,
                CancellationToken cancellationToken)
            {
                _owner = owner;
                _id = id;
                Cursor = cursor;
                _declaredFields = declaredFields;
                _limits = limits;
                _hooks = hooks;
                _ordinal = ordinal;
                _cancellationToken = cancellationToken;
                if (declaredFields is < 2 or > 1024) throw Failure("blob-shape");
                _output = owner.BeginBlob(id);
            }

            internal string Cursor { get; }

            internal void Append(int field, int chunk, ReadOnlySpan<byte> data)
            {
                if (_completed) throw Failure("blob-sequence");
                _cancellationToken.ThrowIfCancellationRequested();
                if (field == 0)
                {
                    if (_descriptorSeen || chunk != 0 || data.Length != BlobDescriptorBytes)
                        throw Failure("blob-descriptor");
                    _length = BinaryPrimitives.ReadInt64BigEndian(data);
                    if (_length < 0) throw Failure("blob-descriptor");
                    _expectedContentSha256 = data[sizeof(long)..].ToArray();
                    var contentFields = Math.Max(
                        1L,
                        checked((_length + _limits.MaxFieldBytes - 1)
                                / _limits.MaxFieldBytes));
                    if (contentFields > 1023
                        || _declaredFields != checked((int)contentFields + 1))
                    {
                        throw Failure("blob-shape");
                    }
                    _descriptorSeen = true;
                    _currentField = 0;
                    return;
                }

                if (!_descriptorSeen || field is < 1 || field >= _declaredFields)
                    throw Failure("blob-shape");
                if (field != _currentField)
                {
                    ValidateFinishedContentField();
                    if (field != _currentField + 1 || chunk != 0)
                        throw Failure("blob-shape");
                    _currentField = field;
                    _currentFieldBytes = 0;
                }
                _currentFieldBytes = checked(_currentFieldBytes + data.Length);
                _contentBytes = checked(_contentBytes + data.Length);
                if (_contentBytes > _length
                    || _currentFieldBytes > _limits.MaxFieldBytes)
                {
                    throw Failure("blob-shape");
                }
                _contentHash.AppendData(data);
                _output!.Stream.Write(data);
            }

            internal BlobFact Complete(int fields, long decodedBytes)
            {
                if (_completed || !_descriptorSeen || fields != _declaredFields)
                    throw Failure("blob-shape");
                ValidateFinishedContentField();
                if (_currentField != _declaredFields - 1
                    || _contentBytes != _length
                    || decodedBytes != checked(BlobDescriptorBytes + _length)
                    || _expectedContentSha256 is null)
                {
                    throw Failure("blob-shape");
                }
                var actual = _contentHash.GetHashAndReset();
                byte[]? networkCopy = null;
                var transferred = false;
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            actual,
                            _expectedContentSha256))
                    {
                        throw Failure("blob-content");
                    }
                    _owner.CompleteBlob(
                        _id,
                        _output!,
                        _length,
                        actual,
                        _hooks,
                        _ordinal);
                    _output = null;
                    Span<byte> network = stackalloc byte[16];
                    try
                    {
                        if (!_id.TryWriteBytes(network, bigEndian: true, out var written)
                            || written != network.Length)
                        {
                            throw Failure("blob-cursor");
                        }
                        networkCopy = network.ToArray();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(network);
                    }
                    var fact = new BlobFact(networkCopy, _length, actual);
                    _completed = true;
                    transferred = true;
                    return fact;
                }
                finally
                {
                    if (!transferred)
                    {
                        CryptographicOperations.ZeroMemory(actual);
                        if (networkCopy is not null)
                            CryptographicOperations.ZeroMemory(networkCopy);
                    }
                }
            }

            internal void AbortNoThrow()
            {
                try
                {
                    Dispose();
                }
                catch
                {
                    // Preserve the parser failure.
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _output?.AbortNoThrow();
                _contentHash.Dispose();
                if (_expectedContentSha256 is not null)
                    CryptographicOperations.ZeroMemory(_expectedContentSha256);
            }

            private void ValidateFinishedContentField()
            {
                if (_currentField < 1) return;
                var contentOrdinal = _currentField - 1;
                var offset = checked((long)contentOrdinal * _limits.MaxFieldBytes);
                var expected = _length == 0
                    ? 0
                    : Math.Min(_limits.MaxFieldBytes, _length - offset);
                if (expected < 0 || _currentFieldBytes != expected)
                    throw Failure("blob-shape");
            }
        }

        private sealed class BlobFact(
            byte[] networkUuid,
            long length,
            byte[] contentSha256)
        {
            internal byte[] NetworkUuid { get; } = networkUuid;
            internal long Length { get; } = length;
            internal byte[] ContentSha256 { get; } = contentSha256;

            internal void Clear()
            {
                CryptographicOperations.ZeroMemory(NetworkUuid);
                CryptographicOperations.ZeroMemory(ContentSha256);
            }
        }
    }
}

using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3SnapshotDirectoryFaultPoint
{
    AfterDataDurablyClosed,
    AfterDataVerified,
    AfterFileDescriptorHashed,
    BeforeManifestTemporaryCreated,
    AfterManifestTemporaryCreated,
    BeforeManifestRename,
    AfterManifestPublished,
}

internal sealed record TransferV3SnapshotDirectoryHooks(
    Action<TransferV3SnapshotDirectoryFaultPoint>? AfterFaultPoint = null,
    Func<SafeFileHandle, SafeFileHandle>? DuplicateHandle = null);

internal enum TransferV3SnapshotDirectoryState
{
    Active,
    CleanupPending,
    ClosePending,
    Disposed,
}

internal sealed class TransferV3SnapshotDirectory : IDisposable
{
    private const string ManifestFileName = "manifest.json";
    internal const string EmptyResiduePathDataKey =
        "NZBDAV_TRANSFER_V3_EMPTY_RESIDUE_PATH";

    private readonly SafeFileHandle _parentDirectory;
    private readonly SafeFileHandle _rootDirectory;
    private readonly string _rootName;
    private readonly TransferV3FileIdentity _rootIdentity;
    private readonly TransferV3SnapshotDirectoryHooks? _hooks;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _trackingGate = new();
    private readonly List<OwnedFile> _ownedFiles = [];
    private readonly HashSet<string> _createdDataFileNames = new(StringComparer.Ordinal);
    private string[]? _expectedDataFileNames;
    private HashSet<string>? _expectedDataFileNameSet;
    private bool _dataOutputFactoryCreated;
    private bool _finalized;
    private TransferV3SnapshotDirectoryState _state;

    private sealed class OwnedFile(
        string expectedName,
        TransferV3FileIdentity identity,
        TransferV3DurableFileStream stream,
        SafeFileHandle retainedIdentity)
    {
        internal string ExpectedName { get; set; } = expectedName;

        internal TransferV3FileIdentity Identity { get; } = identity;

        internal TransferV3DurableFileStream Stream { get; } = stream;

        internal SafeFileHandle RetainedIdentity { get; } = retainedIdentity;

        internal DataFileReceipt? Receipt { get; set; }
    }

    // Raw-file digests are private publication evidence. This deliberately is
    // a plain private class (not a record) so no value-bearing default string
    // representation or API surface can leak the receipt.
    private sealed class DataFileReceipt(
        TransferV3FileIdentity createIdentity,
        TransferV3FileFingerprint finalFingerprint,
        long size,
        byte[] rawSha256)
    {
        internal TransferV3FileIdentity CreateIdentity { get; } = createIdentity;

        internal TransferV3FileFingerprint FinalFingerprint { get; } = finalFingerprint;

        internal long Size { get; } = size;

        internal byte[] RawSha256 { get; } = rawSha256;

        internal void Clear() => CryptographicOperations.ZeroMemory(RawSha256);
    }

    private readonly record struct FileEvidence(
        TransferV3FileStat Stat,
        long Size,
        byte[] RawSha256);

    private readonly record struct ReceiptedDataFile(
        OwnedFile File,
        DataFileReceipt Receipt);

    private sealed class DataOutputFactory(TransferV3SnapshotDirectory owner)
        : ITransferV3TableOutputFactory
    {
        public ValueTask<ITransferV3TableOutput> CreateAsync(
            string fileName,
            CancellationToken cancellationToken) =>
            owner.CreateDataOutputAsync(fileName, cancellationToken);
    }

    private sealed class DataOutput(
        TransferV3SnapshotDirectory owner,
        OwnedFile file) : ITransferV3TableOutput
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _completed;
        private bool _faulted;
        private bool _disposed;

        public Stream Stream => file.Stream;

        public async ValueTask CompleteDurablyAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_completed) return;
                if (_disposed || _faulted)
                {
                    throw new InvalidOperationException(
                        "The private snapshot data output cannot be completed in its current state.");
                }

                try
                {
                    await owner.CompleteDataOutputAsync(file, cancellationToken)
                        .ConfigureAwait(false);
                    _completed = true;
                }
                catch
                {
                    _faulted = true;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                file.Stream.Dispose();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private TransferV3SnapshotDirectory(
        string rootPath,
        string rootName,
        SafeFileHandle parentDirectory,
        SafeFileHandle rootDirectory,
        TransferV3FileIdentity rootIdentity,
        TransferV3SnapshotDirectoryHooks? hooks)
    {
        RootPath = rootPath;
        _rootName = rootName;
        _parentDirectory = parentDirectory;
        _rootDirectory = rootDirectory;
        _rootIdentity = rootIdentity;
        _hooks = hooks;
    }

    internal string RootPath { get; }

    // POSIX has no portable conditional rmdir-by-open-descriptor operation.
    // Unfinalized cleanup removes identity-proven contents through the pinned
    // root descriptor. This is set only when RootPath still names that owned
    // root and the pinned directory is actually empty. External hard-link
    // residue is reported separately through sanitized cleanup evidence.
    internal string? CleanupResiduePath { get; private set; }

    internal static TransferV3SnapshotDirectory CreateNew(
        string outputPath,
        TransferV3SnapshotDirectoryHooks? hooks = null)
    {
        if (!TransferV3Posix.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Transfer v3 snapshots require Linux x64/arm64 or macOS arm64 with a verified POSIX ABI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        var parentPath = Path.GetDirectoryName(rootPath);
        var rootName = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(parentPath)
            || string.IsNullOrEmpty(rootName)
            || rootName is "." or "..")
        {
            throw new IOException("The transfer snapshot output path is invalid.");
        }

        // Build the private directory under an unpredictable staging name. The
        // requested output name is not made visible until the directory has an
        // open descriptor, a captured identity, and its final private mode.
        // This also lets an existing output fail at a no-replace rename without
        // ever opening or changing that existing entry.
        var stagingName = $".nzbdav-transfer-v3-{Guid.NewGuid():N}.tmp";
        SafeFileHandle? parent = null;
        SafeFileHandle? root = null;
        TransferV3FileIdentity? identity = null;
        var stagingCreated = false;
        var finalVisible = false;
        try
        {
            parent = TransferV3Posix.OpenDirectory(parentPath);
            TransferV3Posix.CreateDirectoryAt(
                parent,
                stagingName,
                out stagingCreated);
            root = TransferV3Posix.OpenDirectoryAt(parent, stagingName);
            identity = TransferV3Posix.GetIdentity(root);
            TransferV3Posix.SetMode(root, TransferV3Posix.PrivateDirectoryMode);
            TransferV3Posix.Sync(root);
            TransferV3Posix.RenameNoReplaceAt(parent, stagingName, rootName);
            stagingCreated = false;
            finalVisible = true;
            if (!TransferV3Posix.EntryMatches(parent, rootName, identity.Value))
            {
                throw new IOException(
                    "The visible snapshot root changed identity during private directory publication.");
            }

            TransferV3Posix.Sync(parent);
            return new TransferV3SnapshotDirectory(
                rootPath,
                rootName,
                parent,
                root,
                identity.Value,
                hooks);
        }
        catch (Exception primary)
        {
            var cleanup = new List<Exception>();
            TryDispose(root, cleanup);
            string? residuePath = null;
            if (parent is not null && identity is not null)
            {
                var ownedName = finalVisible ? rootName : stagingName;
                if (TransferV3Posix.EntryMatches(parent, ownedName, identity.Value))
                {
                    residuePath = Path.Combine(parentPath, ownedName);
                }
            }
            else if (parent is not null && stagingCreated)
            {
                // The open/fstat step itself failed, so the possible empty
                // residue cannot be identity-proven. Report the create-new name
                // for operator audit, but never unlink it by name.
                residuePath = Path.Combine(parentPath, stagingName);
            }

            if (residuePath is not null)
            {
                try
                {
                    primary.Data[EmptyResiduePathDataKey] = residuePath;
                }
                catch (Exception exception)
                {
                    cleanup.Add(exception);
                }
            }

            TryDispose(parent, cleanup);
            TransferV3Posix.ThrowPrimaryAndCleanup(
                primary,
                cleanup,
                "Snapshot directory creation and cleanup both failed.");
            throw new InvalidOperationException("Unreachable snapshot creation path.");
        }
    }

    internal ITransferV3TableOutputFactory CreateDataOutputFactory(
        IReadOnlyList<string> expectedFileNames)
    {
        ArgumentNullException.ThrowIfNull(expectedFileNames);
        var frozen = expectedFileNames.ToArray();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileName in frozen)
        {
            ValidateFileName(fileName);
            if (!expected.Add(fileName))
            {
                throw new ArgumentException(
                    "The expected snapshot data-file list contains a duplicate name.",
                    nameof(expectedFileNames));
            }
        }

        _operationGate.Wait();
        try
        {
            EnsureWritableAndOwned();
            if (_dataOutputFactoryCreated)
            {
                throw new InvalidOperationException(
                    "A snapshot data-output factory has already been created.");
            }

            _expectedDataFileNames = frozen;
            _expectedDataFileNameSet = expected;
            _dataOutputFactoryCreated = true;
            return new DataOutputFactory(this);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<ITransferV3TableOutput> CreateDataOutputAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWritableAndOwned();
            if (_expectedDataFileNameSet is null
                || !_expectedDataFileNameSet.Contains(fileName))
            {
                throw new InvalidOperationException(
                    "The requested snapshot output is not in the frozen expected-file list.");
            }
            if (_createdDataFileNames.Contains(fileName))
            {
                throw new InvalidOperationException(
                    "Each expected snapshot data output may be created exactly once.");
            }

            var owned = CreateDurableFile(fileName);
            try
            {
                lock (_trackingGate)
                {
                    _ownedFiles.Add(owned);
                    _createdDataFileNames.Add(fileName);
                }
            }
            catch (Exception primary)
            {
                var cleanupCodes = new List<string>();
                CleanupOwnedFiles([owned], [owned], cleanupCodes);
                CloseOwnedFiles([owned], cleanupCodes);
                TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
            }

            return new DataOutput(this, owned);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask CompleteDataOutputAsync(
        OwnedFile file,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWritableAndOwned();
            lock (_trackingGate)
            {
                if (!_ownedFiles.Contains(file)
                    || !_createdDataFileNames.Contains(file.ExpectedName))
                {
                    throw new InvalidOperationException(
                        "The snapshot data output is not owned by this output factory.");
                }
                if (file.Receipt is not null) return;
            }

            file.Stream.Dispose();
            var durableClose = file.Stream.GetDurableCloseSnapshot();
            if (!durableClose.Completed)
            {
                throw new IOException(
                    "The snapshot data file did not complete its durable close.");
            }
            if (durableClose.Failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(durableClose.Failure)
                    .Throw();
            }
            _hooks?.AfterFaultPoint?.Invoke(
                TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWritableAndOwned();

            byte[]? unstoredRawSha256 = null;
            try
            {
                var evidence = await ReadAndHashOwnedFileAsync(
                        file.ExpectedName,
                        file.Identity,
                        expectedFingerprint: null,
                        expectedSize: null,
                        expectedRawSha256: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                unstoredRawSha256 = evidence.RawSha256;
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.AfterDataVerified);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWritableAndOwned();
                RecheckReceiptCandidate(file, evidence.Stat);

                var receipt = new DataFileReceipt(
                    file.Identity,
                    evidence.Stat.Fingerprint,
                    evidence.Size,
                    evidence.RawSha256);
                lock (_trackingGate)
                {
                    if (file.Receipt is not null)
                    {
                        throw new InvalidOperationException(
                            "The snapshot data-file receipt was already recorded.");
                    }
                    file.Receipt = receipt;
                    unstoredRawSha256 = null;
                }
            }
            finally
            {
                if (unstoredRawSha256 is not null)
                {
                    CryptographicOperations.ZeroMemory(unstoredRawSha256);
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void RecheckReceiptCandidate(
        OwnedFile file,
        TransferV3FileStat expectedStat)
    {
        var retainedStat = TransferV3Posix.GetFileStat(file.RetainedIdentity);
        ValidatePrivateDataFileStat(
            retainedStat,
            file.Identity,
            expectedStat.Fingerprint,
            expectedStat.Fingerprint.Size);
        using var current = TransferV3Posix.OpenReadOnlyRegularFileAt(
            _rootDirectory,
            file.ExpectedName);
        var currentStat = TransferV3Posix.GetFileStat(current);
        ValidatePrivateDataFileStat(
            currentStat,
            file.Identity,
            expectedStat.Fingerprint,
            expectedStat.Fingerprint.Size);
        if (retainedStat != expectedStat || currentStat != expectedStat)
        {
            throw new IOException(
                "A private snapshot data file changed before receipt storage.");
        }
    }

    internal async ValueTask PublishManifestAsync(
        ReadOnlyMemory<byte> manifest,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureWritableAndOwned();
            cancellationToken.ThrowIfCancellationRequested();
            var dataFiles = SnapshotReceiptedDataFiles();
            await VerifyExactDataFilesAsync(dataFiles, cancellationToken)
                .ConfigureAwait(false);
            EnsureWritableAndOwned();
            TransferV3Posix.Sync(_rootDirectory);
            EnsureWritableAndOwned();

            var temporaryName = $".{ManifestFileName}.{Guid.NewGuid():N}.tmp";
            OwnedFile? manifestFile = null;
            byte[]? manifestRawSha256 = null;
            // Production correctness does not depend on hooks: every individual
            // hash ends with a no-follow pathname reopen, and the ordinary gate
            // runs before manifest creation. Extra full-gate passes are needed
            // only after an explicitly configured callback because that callback
            // is itself a mutation-capable boundary. The remaining final-check-
            // to-rename race is covered by the quiescent private-directory model.
            var mutationHooksEnabled = _hooks?.AfterFaultPoint is not null;
            try
            {
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.BeforeManifestTemporaryCreated);
                cancellationToken.ThrowIfCancellationRequested();
                if (mutationHooksEnabled)
                {
                    await VerifyExactDataFilesAsync(dataFiles, cancellationToken)
                        .ConfigureAwait(false);
                    TransferV3Posix.Sync(_rootDirectory);
                    EnsureWritableAndOwned();
                }

                manifestRawSha256 = SHA256.HashData(manifest.Span);
                manifestFile = CreateDurableFile(temporaryName);
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.AfterManifestTemporaryCreated);
                Exception? writeFailure = null;
                Exception? closeFailure = null;
                try
                {
                    await manifestFile.Stream.WriteAsync(manifest, cancellationToken);
                    await manifestFile.Stream.FlushAsync(cancellationToken);
                    manifestFile.Stream.Flush(flushToDisk: true);
                }
                catch (Exception exception)
                {
                    writeFailure = exception;
                }

                try
                {
                    manifestFile.Stream.Dispose();
                }
                catch (Exception exception)
                {
                    closeFailure = exception;
                }

                ThrowIfFileCloseFailed(writeFailure, closeFailure);
                var manifestClose = manifestFile.Stream.GetDurableCloseSnapshot();
                if (!manifestClose.Completed)
                {
                    throw new IOException(
                        "The snapshot manifest temporary file did not complete its durable close.");
                }
                if (manifestClose.Failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(manifestClose.Failure)
                        .Throw();
                }
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWritableAndOwned();
                var temporaryManifestStat = await VerifyOwnedFileAsync(
                        temporaryName,
                        manifestFile.Identity,
                        expectedFingerprint: null,
                        manifest.Length,
                        manifestRawSha256,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateExactDirectoryEntries(
                    _expectedDataFileNameSet!,
                    temporaryName);
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.BeforeManifestRename);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWritableAndOwned();
                if (mutationHooksEnabled)
                {
                    await VerifyExactDataFilesAsync(
                            dataFiles,
                            cancellationToken,
                            temporaryName)
                        .ConfigureAwait(false);
                    _ = await VerifyOwnedFileAsync(
                            temporaryName,
                            manifestFile.Identity,
                            temporaryManifestStat.Fingerprint,
                            manifest.Length,
                            manifestRawSha256,
                            cancellationToken)
                        .ConfigureAwait(false);

                    // A later-file hash callback can mutate a file already
                    // visited by the hook-enabled pass. The final complete pass
                    // is deliberately hook-suppressed so verification cannot
                    // re-enter mutation-capable test callbacks.
                    await VerifyExactDataFilesAsync(
                            dataFiles,
                            cancellationToken,
                            temporaryName,
                            invokeFaultHooks: false)
                        .ConfigureAwait(false);
                    _ = await VerifyOwnedFileAsync(
                            temporaryName,
                            manifestFile.Identity,
                            temporaryManifestStat.Fingerprint,
                            manifest.Length,
                            manifestRawSha256,
                            cancellationToken,
                            invokeFaultHook: false)
                        .ConfigureAwait(false);
                }
                else
                {
                    ValidateExactDirectoryEntries(
                        _expectedDataFileNameSet!,
                        temporaryName);
                }
                TransferV3Posix.Sync(_rootDirectory);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureWritableAndOwned();
                cancellationToken.ThrowIfCancellationRequested();
                TransferV3Posix.RenameNoReplaceAt(
                    _rootDirectory,
                    temporaryName,
                    ManifestFileName);
                manifestFile.ExpectedName = ManifestFileName;

                // The publication boundary has begun. Directory durability and
                // visible-root verification are deliberately non-cancellable.
                TransferV3Posix.Sync(_rootDirectory);
                EnsureWritableAndOwned();
                ValidateExactDirectoryEntries(
                    _expectedDataFileNameSet!,
                    ManifestFileName);
                var publishedManifestStat = await VerifyOwnedFileAsync(
                        ManifestFileName,
                        manifestFile.Identity,
                        expectedFingerprint: null,
                        manifest.Length,
                        manifestRawSha256,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                EnsureWritableAndOwned();
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished);
                if (mutationHooksEnabled)
                {
                    EnsureWritableAndOwned();
                    await VerifyExactDataFilesAsync(
                            dataFiles,
                            CancellationToken.None,
                            ManifestFileName)
                        .ConfigureAwait(false);
                    _ = await VerifyOwnedFileAsync(
                            ManifestFileName,
                            manifestFile.Identity,
                            publishedManifestStat.Fingerprint,
                            manifest.Length,
                            manifestRawSha256,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    // This is the authoritative post-callback pass. Suppressing
                    // hooks prevents both cross-file and manifest-hash callbacks
                    // from mutating evidence that has already been accepted.
                    await VerifyExactDataFilesAsync(
                            dataFiles,
                            CancellationToken.None,
                            ManifestFileName,
                            invokeFaultHooks: false)
                        .ConfigureAwait(false);
                    _ = await VerifyOwnedFileAsync(
                            ManifestFileName,
                            manifestFile.Identity,
                            publishedManifestStat.Fingerprint,
                            manifest.Length,
                            manifestRawSha256,
                            CancellationToken.None,
                            invokeFaultHook: false)
                        .ConfigureAwait(false);
                    TransferV3Posix.Sync(_rootDirectory);
                    EnsureWritableAndOwned();
                }
                lock (_trackingGate)
                {
                    _ownedFiles.Add(manifestFile);
                }
                _finalized = true;
                manifestFile = null;
            }
            catch (Exception primary)
            {
                var cleanupCodes = new List<string>();
                if (manifestFile is not null)
                {
                    OwnedFile[] known;
                    lock (_trackingGate)
                    {
                        known = [.. _ownedFiles, manifestFile];
                    }
                    CleanupOwnedFiles([manifestFile], known, cleanupCodes);
                    CloseOwnedFiles([manifestFile], cleanupCodes);
                }
                else
                {
                    TrySyncRoot(cleanupCodes);
                }

                TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
            }
            finally
            {
                if (manifestRawSha256 is not null)
                {
                    CryptographicOperations.ZeroMemory(manifestRawSha256);
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private ReceiptedDataFile[] SnapshotReceiptedDataFiles()
    {
        lock (_trackingGate)
        {
            if (!_dataOutputFactoryCreated
                || _expectedDataFileNames is null
                || _expectedDataFileNameSet is null)
            {
                throw new InvalidOperationException(
                    "A frozen snapshot data-output factory is required before publication.");
            }
            if (_createdDataFileNames.Count != _expectedDataFileNames.Length
                || _ownedFiles.Count != _expectedDataFileNames.Length)
            {
                throw new InvalidOperationException(
                    "Every expected snapshot data output must be created exactly once before publication.");
            }

            var byName = new Dictionary<string, OwnedFile>(StringComparer.Ordinal);
            foreach (var file in _ownedFiles)
            {
                if (!byName.TryAdd(file.ExpectedName, file))
                {
                    throw new InvalidOperationException(
                        "Snapshot data-output tracking contains a duplicate name.");
                }
            }

            var result = new ReceiptedDataFile[_expectedDataFileNames.Length];
            for (var index = 0; index < _expectedDataFileNames.Length; index++)
            {
                var expectedName = _expectedDataFileNames[index];
                if (!_createdDataFileNames.Contains(expectedName)
                    || !byName.TryGetValue(expectedName, out var file)
                    || file.Receipt is not { } receipt)
                {
                    throw new InvalidOperationException(
                        "Every expected snapshot data output requires a verified private receipt before publication.");
                }

                result[index] = new ReceiptedDataFile(file, receipt);
            }
            return result;
        }
    }

    private async ValueTask VerifyExactDataFilesAsync(
        IReadOnlyList<ReceiptedDataFile> dataFiles,
        CancellationToken cancellationToken,
        string? expectedAdditionalName = null,
        bool invokeFaultHooks = true)
    {
        ValidateExactDirectoryEntries(
            _expectedDataFileNameSet!,
            expectedAdditionalName);
        foreach (var dataFile in dataFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWritableAndOwned();
            var receipt = dataFile.Receipt;
            _ = await VerifyOwnedFileAsync(
                    dataFile.File.ExpectedName,
                    receipt.CreateIdentity,
                    receipt.FinalFingerprint,
                    receipt.Size,
                    receipt.RawSha256,
                    cancellationToken,
                    invokeFaultHooks)
                .ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritableAndOwned();
        ValidateExactDirectoryEntries(
            _expectedDataFileNameSet!,
            expectedAdditionalName);
    }

    private async ValueTask<TransferV3FileStat> VerifyOwnedFileAsync(
        string fileName,
        TransferV3FileIdentity expectedIdentity,
        TransferV3FileFingerprint? expectedFingerprint,
        long? expectedSize,
        byte[] expectedRawSha256,
        CancellationToken cancellationToken,
        bool invokeFaultHook = true)
    {
        var evidence = await ReadAndHashOwnedFileAsync(
                fileName,
                expectedIdentity,
                expectedFingerprint,
                expectedSize,
                expectedRawSha256,
                cancellationToken,
                invokeFaultHook)
            .ConfigureAwait(false);
        try
        {
            return evidence.Stat;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(evidence.RawSha256);
        }
    }

    private void ValidateExactDirectoryEntries(
        IReadOnlySet<string> expectedDataFileNames,
        string? expectedTemporaryName = null)
    {
        var expectedCount = expectedDataFileNames.Count
                            + (expectedTemporaryName is null ? 0 : 1);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(_rootDirectory))
        {
            if (entry.Name is null || !actual.Add(entry.Name))
            {
                throw new IOException(
                    "The private snapshot directory contains an invalid entry set.");
            }
        }

        if (actual.Count != expectedCount
            || expectedDataFileNames.Any(name => !actual.Contains(name))
            || expectedTemporaryName is not null
               && !actual.Contains(expectedTemporaryName))
        {
            throw new IOException(
                "The private snapshot directory does not contain the exact expected file set.");
        }
    }

    private async ValueTask<FileEvidence> ReadAndHashOwnedFileAsync(
        string fileName,
        TransferV3FileIdentity expectedIdentity,
        TransferV3FileFingerprint? expectedFingerprint,
        long? expectedSize,
        byte[]? expectedRawSha256,
        CancellationToken cancellationToken,
        bool invokeFaultHook = true)
    {
        using var handle = TransferV3Posix.OpenReadOnlyRegularFileAt(
            _rootDirectory,
            fileName);
        var before = TransferV3Posix.GetFileStat(handle);
        ValidatePrivateDataFileStat(
            before,
            expectedIdentity,
            expectedFingerprint,
            expectedSize);

        var buffer = new byte[1024 * 1024];
        byte[] digest;
        long total = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                hash.AppendData(buffer.AsSpan(0, read));
            }

            var after = TransferV3Posix.GetFileStat(handle);
            ValidatePrivateDataFileStat(
                after,
                expectedIdentity,
                expectedFingerprint,
                expectedSize);
            if (after != before || total != before.Fingerprint.Size)
            {
                throw new IOException(
                    "A private snapshot data file changed while it was being verified.");
            }

            if (invokeFaultHook)
            {
                _hooks?.AfterFaultPoint?.Invoke(
                    TransferV3SnapshotDirectoryFaultPoint.AfterFileDescriptorHashed);
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Reopen the pathname no-follow after hashing. This binds the final
            // evidence to both the descriptor bytes and the directory entry,
            // closing a swap that occurs while the descriptor is being read.
            // An active same-UID swap after this final check remains inside the
            // documented quiescent-private-directory threat boundary.
            using (var current = TransferV3Posix.OpenReadOnlyRegularFileAt(
                       _rootDirectory,
                       fileName))
            {
                var currentStat = TransferV3Posix.GetFileStat(current);
                ValidatePrivateDataFileStat(
                    currentStat,
                    expectedIdentity,
                    after.Fingerprint,
                    after.Fingerprint.Size);
                if (currentStat != after)
                {
                    throw new IOException(
                        "A private snapshot pathname changed while its descriptor was being verified.");
                }
            }

            digest = hash.GetHashAndReset();
            if (expectedRawSha256 is not null
                && !CryptographicOperations.FixedTimeEquals(
                    digest,
                    expectedRawSha256))
            {
                CryptographicOperations.ZeroMemory(digest);
                throw new IOException(
                    "A private snapshot data file no longer matches its verified receipt.");
            }

            return new FileEvidence(after, total, digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ValidatePrivateDataFileStat(
        TransferV3FileStat stat,
        TransferV3FileIdentity expectedIdentity,
        TransferV3FileFingerprint? expectedFingerprint,
        long? expectedSize)
    {
        const uint regularPrivateFileMode = 0x8000 | TransferV3Posix.PrivateFileMode;
        if (stat.Fingerprint.Identity != expectedIdentity
            || stat.Mode != regularPrivateFileMode
            || stat.LinkCount != 1
            || stat.Fingerprint.Size < 0
            || expectedFingerprint is not null
               && stat.Fingerprint != expectedFingerprint.Value
            || expectedSize is not null
               && stat.Fingerprint.Size != expectedSize.Value)
        {
            throw new IOException(
                "A private snapshot data file failed identity, mode, link-count, or size verification.");
        }
    }

    public void Dispose()
    {
        _operationGate.Wait();
        var cleanupCodes = new List<string>();
        try
        {
            ClearRawReceipts();
            if (_state == TransferV3SnapshotDirectoryState.Disposed)
            {
                return;
            }

            OwnedFile[] files;
            lock (_trackingGate)
            {
                files = [.. _ownedFiles];
            }

            if (_state == TransferV3SnapshotDirectoryState.Active)
            {
                _state = _finalized
                    ? TransferV3SnapshotDirectoryState.ClosePending
                    : TransferV3SnapshotDirectoryState.CleanupPending;
            }

            if (_state == TransferV3SnapshotDirectoryState.CleanupPending)
            {
                CleanupResiduePath = null;
                CleanupOwnedFiles(files, files, cleanupCodes);
                try
                {
                    if (!TransferV3Posix.EnumerateDirectoryEntries(_rootDirectory).Any()
                        && TransferV3Posix.EntryMatches(
                            _parentDirectory,
                            _rootName,
                            _rootIdentity)
                        && VisibleRootPathMatchesOwnedRoot())
                    {
                        CleanupResiduePath = RootPath;
                    }
                }
                catch
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "snapshot-residue-check-failed");
                }

                if (cleanupCodes.Count == 0)
                {
                    _state = TransferV3SnapshotDirectoryState.ClosePending;
                }
            }

            if (_state == TransferV3SnapshotDirectoryState.ClosePending)
            {
                CloseOwnedFiles(files, cleanupCodes);
                TryDispose(_rootDirectory, cleanupCodes, "root-descriptor-close-failed");
                TryDispose(_parentDirectory, cleanupCodes, "parent-descriptor-close-failed");
                if (cleanupCodes.Count == 0)
                {
                    _state = TransferV3SnapshotDirectoryState.Disposed;
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (cleanupCodes.Count > 0)
        {
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(
                new IOException("The private snapshot cleanup was incomplete."),
                cleanupCodes);
        }
    }

    private void ClearRawReceipts()
    {
        lock (_trackingGate)
        {
            foreach (var file in _ownedFiles)
            {
                file.Receipt?.Clear();
            }
        }
    }

    private OwnedFile CreateDurableFile(string fileName)
    {
        var handle = TransferV3Posix.CreateFileAt(
            _rootDirectory,
            fileName,
            out var createdIdentity);
        SafeFileHandle? retained = null;
        try
        {
            retained = _hooks?.DuplicateHandle?.Invoke(handle)
                       ?? TransferV3Posix.DuplicateHandle(handle);
            if (TransferV3Posix.GetIdentity(retained) != createdIdentity)
            {
                throw new IOException(
                    "The retained owned-file descriptor changed identity during duplication.");
            }
            return new OwnedFile(
                fileName,
                createdIdentity,
                new TransferV3DurableFileStream(handle),
                retained);
        }
        catch (Exception primary)
        {
            var cleanupCodes = new List<string>();
            TryUnlinkOwnedName(fileName, createdIdentity, cleanupCodes);
            TryDispose(handle, cleanupCodes, "owned-stream-close-failed");
            TryDispose(retained, cleanupCodes, "owned-descriptor-close-failed");
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
            throw new InvalidOperationException("Unreachable private file creation cleanup path.");
        }
    }

    private void CleanupOwnedFiles(
        IReadOnlyCollection<OwnedFile> targets,
        IReadOnlyCollection<OwnedFile> knownFiles,
        List<string> cleanupCodes)
    {
        foreach (var target in targets)
        {
            TryDispose(target.Stream, cleanupCodes, "owned-stream-close-failed");
        }

        foreach (var target in targets.OrderBy(file => file.ExpectedName, StringComparer.Ordinal))
        {
            TryUnlinkOwnedName(target.ExpectedName, target.Identity, cleanupCodes);
        }

        var targetIdentities = targets
            .Select(file => file.Identity)
            .ToHashSet();
        var knownIdentities = knownFiles
            .Select(file => file.Identity)
            .ToHashSet();

        var names = new List<string>();
        try
        {
            foreach (var entry in TransferV3Posix.EnumerateDirectoryEntries(_rootDirectory))
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
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(cleanupCodes, "snapshot-entry-scan-failed");
        }
        names.Sort(StringComparer.Ordinal);

        foreach (var name in names)
        {
            SafeFileHandle? candidate = null;
            TransferV3FileIdentity? candidateIdentity = null;
            try
            {
                candidate = TransferV3Posix.OpenReadOnlyRegularFileAt(
                    _rootDirectory,
                    name);
                candidateIdentity = TransferV3Posix.GetIdentity(candidate);
            }
            catch (IOException)
            {
                try
                {
                    if (TransferV3Posix.EntryExistsNoFollow(_rootDirectory, name))
                    {
                        TransferV3Posix.AddCleanupCode(
                            cleanupCodes,
                            "unknown-entry-residue");
                    }
                }
                catch
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "snapshot-entry-scan-failed");
                }
            }
            finally
            {
                TryDispose(
                    candidate,
                    cleanupCodes,
                    "candidate-descriptor-close-failed");
            }

            if (candidateIdentity is null)
            {
                continue;
            }

            if (targetIdentities.Contains(candidateIdentity.Value))
            {
                TryUnlinkOwnedName(name, candidateIdentity.Value, cleanupCodes);
            }
            else if (!knownIdentities.Contains(candidateIdentity.Value))
            {
                TransferV3Posix.AddCleanupCode(cleanupCodes, "unknown-entry-residue");
            }
        }

        foreach (var target in targets)
        {
            try
            {
                if (TransferV3Posix.GetFileStat(target.RetainedIdentity).LinkCount > 0)
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "external-hard-link-residue");
                }
            }
            catch
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "owned-descriptor-stat-failed");
            }
        }

        TrySyncRoot(cleanupCodes);
    }

    private void TryUnlinkOwnedName(
        string name,
        TransferV3FileIdentity identity,
        List<string> cleanupCodes)
    {
        try
        {
            if (TransferV3Posix.TryUnlinkOwnedRegularFile(
                    _rootDirectory,
                    name,
                    identity)
                == TransferV3OwnedFileUnlinkResult.UnknownEntry)
            {
                TransferV3Posix.AddCleanupCode(cleanupCodes, "unknown-entry-residue");
            }
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(cleanupCodes, "owned-entry-unlink-failed");
        }
    }

    private void TrySyncRoot(List<string> cleanupCodes)
    {
        try
        {
            TransferV3Posix.Sync(_rootDirectory);
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(
                cleanupCodes,
                "snapshot-directory-sync-failed");
        }
    }

    private void EnsureWritableAndOwned()
    {
        ObjectDisposedException.ThrowIf(
            _state != TransferV3SnapshotDirectoryState.Active,
            this);
        if (_finalized)
        {
            throw new InvalidOperationException("The snapshot manifest has already been published.");
        }

        const uint privateDirectoryMode = 0x4000 | TransferV3Posix.PrivateDirectoryMode;
        var rootStat = TransferV3Posix.GetFileStat(_rootDirectory);
        if (rootStat.Fingerprint.Identity != _rootIdentity
            || rootStat.Mode != privateDirectoryMode)
        {
            throw new IOException(
                "The private snapshot root failed identity or mode verification.");
        }

        if (!TransferV3Posix.EntryMatches(
                _parentDirectory,
                _rootName,
                _rootIdentity))
        {
            throw new IOException(
                "The visible snapshot root no longer has the owned directory identity.");
        }

        if (!VisibleRootPathMatchesOwnedRoot())
        {
            throw new IOException(
                "The caller-visible snapshot path no longer resolves to the owned private root.");
        }
    }

    private bool VisibleRootPathMatchesOwnedRoot()
    {
        try
        {
            using var visibleRoot = TransferV3Posix.OpenDirectory(RootPath);
            var visibleStat = TransferV3Posix.GetFileStat(visibleRoot);
            const uint privateDirectoryMode = 0x4000 | TransferV3Posix.PrivateDirectoryMode;
            return visibleStat.Fingerprint.Identity == _rootIdentity
                   && visibleStat.Mode == privateDirectoryMode;
        }
        catch
        {
            return false;
        }
    }

    private static void ThrowIfFileCloseFailed(
        Exception? writeFailure,
        Exception? closeFailure)
    {
        if (writeFailure is not null)
        {
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(
                writeFailure,
                closeFailure is null
                    ? Array.Empty<string>()
                    : ["manifest-stream-close-failed"]);
        }

        if (closeFailure is not null)
        {
            TransferV3Posix.ThrowPrimaryWithCleanupCodes(
                closeFailure,
                Array.Empty<string>());
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || fileName.Length > 255
            || Path.IsPathRooted(fileName)
            || fileName is "." or ".."
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || string.Equals(fileName, ManifestFileName, StringComparison.OrdinalIgnoreCase)
            || fileName.Any(character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '.'
                    or '_'
                    or '-')))
        {
            throw new ArgumentException(
                "Snapshot file names must be safe single relative path components.",
                nameof(fileName));
        }
    }

    private static void TryDispose(IDisposable? disposable, List<Exception> errors)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static void TryDispose(
        IDisposable? disposable,
        List<string> cleanupCodes,
        string cleanupCode)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            TransferV3Posix.AddCleanupCode(cleanupCodes, cleanupCode);
        }
    }

    private static void CloseOwnedFiles(
        IEnumerable<OwnedFile> files,
        List<string> cleanupCodes)
    {
        foreach (var file in files)
        {
            TryDispose(file.Stream, cleanupCodes, "owned-stream-close-failed");
            TryDispose(
                file.RetainedIdentity,
                cleanupCodes,
                "owned-descriptor-close-failed");
        }
    }
}

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3SnapshotVerificationException : FormatException
{
    internal TransferV3SnapshotVerificationException(string code, int? tableOrdinal = null)
        : base(tableOrdinal.HasValue
            ? $"Transfer-v3 snapshot rejected ({code}; table={tableOrdinal.Value})."
            : $"Transfer-v3 snapshot rejected ({code}).")
    {
        Code = code;
        TableOrdinal = tableOrdinal;
    }

    internal string Code { get; }

    internal int? TableOrdinal { get; }
}

internal interface ITransferV3VerificationFactSink
{
    void BeginBatch(int tableOrdinal, int batchOrdinal);

    void AddVerifiedRow(TransferV3VerifiedRowFacts facts);

    void AddPhysicalBlob(
        long rowOrdinal,
        ReadOnlySpan<byte> uuidNetworkBytes,
        long length,
        ReadOnlySpan<byte> contentSha256);

    void CommitBatch();

    void AbortBatchNoThrow();
}

internal sealed record TransferV3VerifiedUniqueKeyFact(
    int RuleOrdinal,
    byte[] Sha256);

internal sealed record TransferV3VerifiedFieldFact(
    int ColumnOrdinal,
    bool IsNull,
    long EncodedBytes,
    byte[] EncodedSha256,
    Guid? UuidValue,
    long? IntegerValue,
    DateTime? LocalWallValue,
    bool? BooleanValue,
    bool ReviewedTextPatternValid);

internal sealed record TransferV3VerifiedRowFacts(
    int TableOrdinal,
    long RowOrdinal,
    ImmutableArray<TransferV3CursorComponent> CursorComponents,
    byte[] KeySha256,
    ImmutableArray<TransferV3VerifiedUniqueKeyFact> UniqueKeys,
    ImmutableArray<TransferV3VerifiedFieldFact> Fields)
{
    internal void ClearDigests()
    {
        CryptographicOperations.ZeroMemory(KeySha256);
        foreach (var unique in UniqueKeys)
            CryptographicOperations.ZeroMemory(unique.Sha256);
        foreach (var field in Fields)
            CryptographicOperations.ZeroMemory(field.EncodedSha256);
    }
}

internal sealed record TransferV3VerifiedTableFrameResult(
    long Rows,
    long DecodedBytes,
    long RawBytes,
    byte[] RawSha256,
    TransferV3BufferMetrics BufferMetrics);

internal sealed record TransferV3VerifiedBlobFrameResult(
    long Rows,
    long ContentBytes,
    long DecodedBytes,
    long RawBytes,
    byte[] RawSha256,
    TransferV3BufferMetrics BufferMetrics);

internal sealed record TransferV3VerifiedTableMetrics(
    long Rows,
    long DecodedBytes,
    long RawBytes,
    TransferV3BufferMetrics BufferMetrics);

internal sealed record TransferV3VerifiedBlobMetrics(
    long Rows,
    long ContentBytes,
    long DecodedBytes,
    long RawBytes,
    TransferV3BufferMetrics BufferMetrics);

internal sealed record TransferV3SnapshotVerifierHooks(
    TransferV3SnapshotReaderHooks? ReaderHooks = null,
    TransferV3BlobReferenceIndexHooks? IndexHooks = null);

internal sealed record TransferV3SnapshotVerificationMetrics(
    ImmutableArray<TransferV3VerifiedTableMetrics> Tables,
    TransferV3VerifiedBlobMetrics Blobs,
    TransferV3BlobReferenceFactCounts Facts);

internal sealed class TransferV3SnapshotVerifier
{
    private readonly TransferV3SnapshotVerifierHooks? _hooks;

    internal TransferV3SnapshotVerifier(TransferV3SnapshotVerifierHooks? hooks = null)
    {
        _hooks = hooks;
    }

    internal async Task<TransferV3VerifiedSnapshot> VerifyAsync(
        string snapshotRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransferV3PinnedSnapshot? snapshot = null;
        TransferV3BlobReferenceIndex? index = null;
        try
        {
            var contract = TransferV3SourceContract.LoadEmbedded();
            snapshot = await TransferV3SnapshotReader.OpenAsync(
                    snapshotRoot,
                    contract,
                    _hooks?.ReaderHooks,
                    cancellationToken)
                .ConfigureAwait(false);
            var limits = new TransferV3Limits(
                snapshot.Manifest.Limits.MaxFieldBytes,
                snapshot.Manifest.Limits.MaxBatchRows,
                snapshot.Manifest.Limits.MaxBatchBytes);
            index = await TransferV3BlobReferenceIndex.CreateAsync(
                    _hooks?.IndexHooks,
                    cancellationToken)
                .ConfigureAwait(false);
            var sink = new TransferV3IndexFactSink(contract, index, cancellationToken);
            var tableMetrics = ImmutableArray.CreateBuilder<TransferV3VerifiedTableMetrics>(
                contract.Tables.Count);
            for (var ordinal = 0; ordinal < contract.Tables.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = snapshot.OpenTableRead(ordinal);
                var result = await TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                        stream,
                        contract,
                        ordinal,
                        snapshot.Manifest.Tables[ordinal],
                        limits,
                        sink,
                    cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (stream.Length != result.RawBytes)
                        throw Failure("table-receipt");
                    snapshot.RecordVerifiedReceipt(ordinal, result.RawBytes, result.RawSha256);
                    tableMetrics.Add(new TransferV3VerifiedTableMetrics(
                        result.Rows,
                        result.DecodedBytes,
                        result.RawBytes,
                        result.BufferMetrics));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(result.RawSha256);
                }
            }

            TransferV3VerifiedBlobFrameResult blobMetrics;
            using (var stream = snapshot.OpenBlobBundleRead())
            {
                blobMetrics = await TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                        stream,
                        snapshot.Manifest.Blobs,
                        limits,
                    sink,
                    cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (stream.Length != blobMetrics.RawBytes)
                        throw Failure("blob-receipt");
                    snapshot.RecordVerifiedReceipt(
                        TransferV3PinnedSnapshot.BlobFileOrdinal,
                        blobMetrics.RawBytes,
                        blobMetrics.RawSha256);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(blobMetrics.RawSha256);
                }
            }

            var canonicalManifest = snapshot.GetCanonicalManifestCopy();
            try
            {
                var manifestDigest = SHA256.HashData(canonicalManifest);
                try
                {
                    snapshot.RecordVerifiedReceipt(
                        TransferV3PinnedSnapshot.ManifestFileOrdinal,
                        canonicalManifest.Length,
                        manifestDigest);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(manifestDigest);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalManifest);
            }

            index.ValidateSemanticClosure(contract, snapshot.Manifest, cancellationToken);
            var facts = index.GetFactCounts(cancellationToken);
            await index.DisposeAsync().ConfigureAwait(false);
            index = null;
            snapshot.VerifyUnchanged(cancellationToken);
            snapshot.SetVerificationMetrics(new TransferV3SnapshotVerificationMetrics(
                tableMetrics.MoveToImmutable(),
                new TransferV3VerifiedBlobMetrics(
                    blobMetrics.Rows,
                    blobMetrics.ContentBytes,
                    blobMetrics.DecodedBytes,
                    blobMetrics.RawBytes,
                    blobMetrics.BufferMetrics),
                facts));

            var verified = TransferV3VerifiedSnapshot.Promote(snapshot);
            snapshot = null;
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupNoThrowAsync(index, snapshot).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3SnapshotVerificationException)
        {
            await CleanupNoThrowAsync(index, snapshot).ConfigureAwait(false);
            throw;
        }
        catch (TransferV3SnapshotReadException exception)
        {
            await CleanupNoThrowAsync(index, snapshot).ConfigureAwait(false);
            throw Failure($"snapshot-{exception.Code}");
        }
        catch (TransferV3BlobReferenceIndexException exception)
        {
            await CleanupNoThrowAsync(index, snapshot).ConfigureAwait(false);
            throw Failure(exception.Code);
        }
        catch
        {
            await CleanupNoThrowAsync(index, snapshot).ConfigureAwait(false);
            throw Failure("verification");
        }
    }

    private static TransferV3SnapshotVerificationException Failure(string code) => new(code);

    private static async ValueTask CleanupNoThrowAsync(
        TransferV3BlobReferenceIndex? index,
        TransferV3PinnedSnapshot? snapshot)
    {
        if (index is not null)
        {
            try
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup cannot replace the authoritative verification failure.
            }
        }
        if (snapshot is not null)
        {
            try
            {
                snapshot.Dispose();
            }
            catch
            {
                // Cleanup cannot replace the authoritative verification failure.
            }
        }
    }
}

internal sealed class TransferV3IndexFactSink : ITransferV3VerificationFactSink
{
    private readonly TransferV3SourceContract _contract;
    private readonly TransferV3BlobReferenceIndex _index;
    private readonly CancellationToken _cancellationToken;

    internal TransferV3IndexFactSink(
        TransferV3SourceContract contract,
        TransferV3BlobReferenceIndex index,
        CancellationToken cancellationToken)
    {
        _contract = contract;
        _index = index;
        _cancellationToken = cancellationToken;
    }

    public void BeginBatch(int tableOrdinal, int batchOrdinal) =>
        _index.BeginBatch(tableOrdinal, batchOrdinal, _cancellationToken);

    public void AddVerifiedRow(TransferV3VerifiedRowFacts facts)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _index.AddRowKey(facts.RowOrdinal, facts.KeySha256, _cancellationToken);
            foreach (var unique in facts.UniqueKeys)
            {
                try
                {
                    _index.AddUniqueValue(
                        unique.RuleOrdinal,
                        facts.RowOrdinal,
                        unique.Sha256,
                        _cancellationToken);
                }
                catch (TransferV3BlobReferenceIndexException exception)
                    when (string.Equals(
                        exception.Code,
                        "index-constraint",
                        StringComparison.Ordinal))
                {
                    throw new TransferV3SnapshotVerificationException(
                        "unique-normalized-collision");
                }
            }

            foreach (var field in facts.Fields)
            {
                if (field.UuidValue is not { } uuid) continue;
                var network = NetworkBytes(uuid);
                try
                {
                    _index.AddUuidValue(
                        field.ColumnOrdinal,
                        facts.RowOrdinal,
                        network,
                        _cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(network);
                }
            }

            AddReferenceFacts(facts);
            AddReviewedFacts(facts);
        }
        catch (TransferV3SnapshotVerificationException)
        {
            throw;
        }
    }

    public void AddPhysicalBlob(
        long rowOrdinal,
        ReadOnlySpan<byte> uuidNetworkBytes,
        long length,
        ReadOnlySpan<byte> contentSha256) =>
        _index.AddPhysicalBlob(
            uuidNetworkBytes,
            length,
            contentSha256,
            _cancellationToken);

    public void CommitBatch() => _index.CommitBatch(_cancellationToken);

    public void AbortBatchNoThrow() => _index.AbortBatchNoThrow();

    private void AddReferenceFacts(TransferV3VerifiedRowFacts facts)
    {
        var table = _contract.Tables[facts.TableOrdinal];
        var referenceBase = _contract.Tables
            .Take(facts.TableOrdinal)
            .Sum(value => value.References.Count);
        for (var index = 0; index < table.References.Count; index++)
        {
            var reference = table.References[index];
            if (reference.Columns.Count != 1) throw Failure("reference-contract");
            var field = FindField(table, facts, reference.Columns[0]);
            if (field.IsNull) continue;
            if (field.UuidValue is not { } target) throw Failure("reference-contract");
            var targetBytes = NetworkBytes(target);
            try
            {
                var referenceOrdinal = checked(referenceBase + index);
                switch (reference.Policy)
                {
                    case TransferV3ReferencePolicy.BlobHard:
                    case TransferV3ReferencePolicy.BlobAndNameHard:
                        _index.AddHardBlobReference(
                            referenceOrdinal,
                            facts.RowOrdinal,
                            targetBytes,
                            _cancellationToken);
                        break;
                    case TransferV3ReferencePolicy.InformationalDigest:
                    case TransferV3ReferencePolicy.PolymorphicInformationalDigest:
                        var owner = FindField(table, facts, "Id").UuidValue;
                        if (owner is not { } ownerUuid) throw Failure("reference-contract");
                        var ownerBytes = NetworkBytes(ownerUuid);
                        try
                        {
                            long? discriminator = null;
                            if (reference.DiscriminatorColumn is { } discriminatorColumn)
                            {
                                discriminator = FindField(table, facts, discriminatorColumn)
                                    .IntegerValue;
                                if (!discriminator.HasValue)
                                    throw Failure("reference-contract");
                            }
                            _index.AddInformationalFact(
                                referenceOrdinal,
                                facts.RowOrdinal,
                                ownerBytes,
                                targetBytes,
                                discriminator,
                                _cancellationToken);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(ownerBytes);
                        }
                        break;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(targetBytes);
            }
        }
    }

    private void AddReviewedFacts(TransferV3VerifiedRowFacts facts)
    {
        var table = _contract.Tables[facts.TableOrdinal];
        if (string.Equals(table.Name, "ConfigItems", StringComparison.Ordinal))
        {
            var name = facts.CursorComponents[0].TextValue
                       ?? throw Failure("bootstrap-config");
            if (_contract.ExcludedConfigKeys.Contains(name, StringComparer.Ordinal))
                throw Failure("reserved-config");
            var configOrdinal = _contract.Bootstrap.Config
                .Select((value, index) => (value, index))
                .Where(value => string.Equals(value.value.Name, name, StringComparison.Ordinal))
                .Select(value => (int?)value.index)
                .SingleOrDefault();
            if (configOrdinal.HasValue)
            {
                var value = facts.Fields[1];
                if (!value.ReviewedTextPatternValid)
                    throw Failure("bootstrap-config");
                try
                {
                    _index.AddBootstrapConfigSecret(
                        configOrdinal.Value,
                        value.EncodedSha256,
                        _cancellationToken);
                }
                catch (TransferV3BlobReferenceIndexException exception)
                    when (string.Equals(
                        exception.Code,
                        "index-constraint",
                        StringComparison.Ordinal))
                {
                    throw Failure("bootstrap-config");
                }
            }
        }

        if (string.Equals(table.Name, "DavItems", StringComparison.Ordinal))
        {
            var id = facts.Fields[0].UuidValue ?? throw Failure("bootstrap-root");
            byte[]? parent = null;
            byte[]? fileBlob = null;
            byte[]? idBytes = null;
            try
            {
                parent = OptionalNetworkBytes(facts.Fields[9].UuidValue);
                fileBlob = OptionalNetworkBytes(facts.Fields[2].UuidValue);
                idBytes = NetworkBytes(id);
                _index.AddDavMetadata(
                    facts.RowOrdinal,
                    idBytes,
                    parent,
                    facts.Fields[13].IntegerValue ?? throw Failure("type-subtype-domain"),
                    facts.Fields[12].IntegerValue ?? throw Failure("type-subtype-domain"),
                    fileBlob,
                    _cancellationToken);
            }
            finally
            {
                if (idBytes is not null) CryptographicOperations.ZeroMemory(idBytes);
                if (parent is not null) CryptographicOperations.ZeroMemory(parent);
                if (fileBlob is not null) CryptographicOperations.ZeroMemory(fileBlob);
            }

            var root = _contract.Bootstrap.Roots
                .Select((value, index) => (value, index))
                .Where(value => Guid.ParseExact(value.value.Id, "D") == id)
                .Select(value => ((TransferV3BootstrapRootContract, int)?)value)
                .SingleOrDefault();
            if (root.HasValue)
            {
                ValidateBootstrapRoot(table, facts, root.Value.Item1);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var field in facts.Fields) hash.AppendData(field.EncodedSha256);
                var marker = hash.GetHashAndReset();
                try
                {
                    _index.AddBootstrapRootMarker(
                        root.Value.Item2,
                        marker,
                        _cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(marker);
                }
            }
        }

        if (facts.TableOrdinal is 11 or 12 or 13)
        {
            var id = facts.Fields[0].UuidValue ?? throw Failure("metadata-subtype");
            var bytes = NetworkBytes(id);
            try
            {
                _index.AddLegacyMetadata(
                    facts.TableOrdinal,
                    facts.RowOrdinal,
                    bytes,
                    _cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        if (string.Equals(table.Name, "HealthCheckResults", StringComparison.Ordinal))
        {
            var seconds = facts.Fields[1].IntegerValue ?? throw Failure("derived-state");
            var day = seconds / 86_400;
            if (seconds < 0 && seconds % 86_400 != 0) day--;
            var start = checked(day * 86_400);
            var end = checked(start + 86_400);
            if (end > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                throw Failure("derived-state");
            _index.AddHealthBucket(
                start,
                end,
                facts.Fields[4].IntegerValue ?? throw Failure("derived-state"),
                facts.Fields[5].IntegerValue ?? throw Failure("derived-state"),
                1,
                _cancellationToken);
        }
    }

    private static void ValidateBootstrapRoot(
        TransferV3TableContract table,
        TransferV3VerifiedRowFacts facts,
        TransferV3BootstrapRootContract root)
    {
        object?[] expected =
        [
            Guid.ParseExact(root.Id, "D"),
            root.CreatedAt,
            null,
            null,
            null,
            root.IdPrefix,
            null,
            root.Name,
            null,
            root.ParentId is null ? null : Guid.ParseExact(root.ParentId, "D"),
            root.Path,
            null,
            root.SubType,
            root.Type,
            null,
        ];
        for (var ordinal = 0; ordinal < expected.Length; ordinal++)
        {
            var encoded = TransferV3RowCodec.EncodeField(table.Columns[ordinal], expected[ordinal]);
            try
            {
                var digest = SHA256.HashData(encoded);
                try
                {
                    if (facts.Fields[ordinal].EncodedBytes != encoded.Length
                        || !CryptographicOperations.FixedTimeEquals(
                            digest,
                            facts.Fields[ordinal].EncodedSha256))
                    {
                        throw Failure("bootstrap-root");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(digest);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
    }

    private static TransferV3VerifiedFieldFact FindField(
        TransferV3TableContract table,
        TransferV3VerifiedRowFacts facts,
        string name)
    {
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, name, StringComparison.Ordinal))
                return facts.Fields[index];
        }
        throw Failure("reference-contract");
    }

    private static byte[] NetworkBytes(Guid uuid)
    {
        var bytes = new byte[16];
        if (!uuid.TryWriteBytes(bytes, bigEndian: true, out var written) || written != 16)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw Failure("uuid-network");
        }
        return bytes;
    }

    private static byte[]? OptionalNetworkBytes(Guid? uuid) =>
        uuid.HasValue ? NetworkBytes(uuid.Value) : null;

    private static TransferV3SnapshotVerificationException Failure(string code) => new(code);
}

internal static class TransferV3SnapshotFrameVerifier
{
    internal static async Task<TransferV3VerifiedTableFrameResult> VerifyTableAsync(
        Stream source,
        TransferV3SourceContract contract,
        int tableOrdinal,
        TransferV3ManifestTable manifest,
        TransferV3Limits limits,
        ITransferV3VerificationFactSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)tableOrdinal >= (uint)contract.Tables.Count)
            throw Failure("table-contract");

        var table = contract.Tables[tableOrdinal];
        var expectedFile = $"table-{tableOrdinal + 1:000}-{table.Name}.jsonl";
        if (!string.Equals(manifest.Name, table.Name, StringComparison.Ordinal)
            || !string.Equals(manifest.File, expectedFile, StringComparison.Ordinal))
        {
            throw Failure("table-manifest");
        }

        if (!source.CanRead || (source.CanSeek && source.Position != 0))
            throw Failure("table-source");

        using var hashing = new HashingReadStream(source);
        var observer = new TableObserver(
            contract,
            tableOrdinal,
            table,
            manifest,
            limits,
            sink,
            cancellationToken);
        try
        {
            var metrics = await TransferV3JsonlParser.ParseAsync(
                    hashing,
                    limits,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rawDigest = hashing.GetHashAndReset();
            var transferred = false;
            try
            {
                var result = new TransferV3VerifiedTableFrameResult(
                    observer.Rows,
                    observer.DecodedBytes,
                    hashing.BytesRead,
                    rawDigest,
                    metrics);
                transferred = true;
                return result;
            }
            finally
            {
                if (!transferred)
                    CryptographicOperations.ZeroMemory(rawDigest);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3SnapshotVerificationException)
        {
            throw;
        }
        catch (TransferV3RowFormatException exception)
        {
            throw Failure($"field-{exception.Code}");
        }
        catch (TransferV3BlobReferenceIndexException exception)
        {
            throw Failure(exception.Code);
        }
        catch (FormatException)
        {
            throw Failure("table-frame");
        }
        catch
        {
            throw Failure("table-verify", tableOrdinal);
        }
    }

    internal static async Task<TransferV3VerifiedBlobFrameResult> VerifyBlobsAsync(
        Stream source,
        TransferV3ManifestBlobs manifest,
        TransferV3Limits limits,
        ITransferV3VerificationFactSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(manifest.Name, "Blobs", StringComparison.Ordinal)
            || !string.Equals(manifest.File, "Blobs.jsonl", StringComparison.Ordinal))
        {
            throw Failure("blob-manifest");
        }
        if (!source.CanRead || (source.CanSeek && source.Position != 0))
            throw Failure("blob-source");

        using var hashing = new HashingReadStream(source);
        using var observer = new BlobObserver(manifest, limits, sink, cancellationToken);
        try
        {
            var metrics = await TransferV3JsonlParser.ParseAsync(
                    hashing,
                    limits,
                    observer,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rawDigest = hashing.GetHashAndReset();
            var transferred = false;
            try
            {
                var result = new TransferV3VerifiedBlobFrameResult(
                    observer.Rows,
                    observer.ContentBytes,
                    observer.DecodedBytes,
                    hashing.BytesRead,
                    rawDigest,
                    metrics);
                transferred = true;
                return result;
            }
            finally
            {
                if (!transferred)
                    CryptographicOperations.ZeroMemory(rawDigest);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TransferV3SnapshotVerificationException)
        {
            throw;
        }
        catch (TransferV3BlobReferenceIndexException exception)
        {
            throw Failure(exception.Code);
        }
        catch (FormatException)
        {
            throw Failure("blob-frame");
        }
        catch
        {
            throw Failure("blob-verify");
        }
    }

    private static TransferV3SnapshotVerificationException Failure(string code) => new(code);

    private static TransferV3SnapshotVerificationException Failure(
        string code,
        int tableOrdinal) => new(code, tableOrdinal);

    private sealed class BlobObserver : ITransferV3FrameObserver, IDisposable
    {
        private const int BlobTableOrdinal = 27;
        private const int DescriptorBytes = sizeof(long) + SHA256.HashSizeInBytes;

        private readonly TransferV3ManifestBlobs _manifest;
        private readonly TransferV3Limits _limits;
        private readonly ITransferV3VerificationFactSink _sink;
        private readonly CancellationToken _cancellationToken;
        private readonly IncrementalHash _inventoryHash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private BlobRowAccumulator? _row;
        private bool _batchOpen;
        private bool _completed;
        private bool _disposed;

        internal BlobObserver(
            TransferV3ManifestBlobs manifest,
            TransferV3Limits limits,
            ITransferV3VerificationFactSink sink,
            CancellationToken cancellationToken)
        {
            _manifest = manifest;
            _limits = limits;
            _sink = sink;
            _cancellationToken = cancellationToken;
        }

        internal long Rows { get; private set; }

        internal long ContentBytes { get; private set; }

        internal long DecodedBytes { get; private set; }

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
                case TransferV3BatchStartFrame batchStart:
                    if (_batchOpen || _row is not null)
                        throw Failure("blob-sequence");
                    _sink.BeginBatch(BlobTableOrdinal, batchStart.Batch);
                    _batchOpen = true;
                    return;
                case TransferV3RowFrame inline:
                    ZeroPayload(inline.Data);
                    throw Failure("blob-inline-row");
                case TransferV3ChunkedRowStartFrame rowStart:
                    if (!_batchOpen || _row is not null)
                        throw Failure("blob-sequence");
                    IReadOnlyList<TransferV3CursorComponent> cursor;
                    try
                    {
                        cursor = TransferV3CursorCodec.Decode(rowStart.Cursor);
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
                    _row = new BlobRowAccumulator(
                        Rows,
                        rowStart.Cursor,
                        cursor[0].UuidValue,
                        rowStart.Fields,
                        _limits,
                        _cancellationToken);
                    return;
                case TransferV3FieldChunkFrame chunk:
                    try
                    {
                        (_row ?? throw Failure("blob-sequence"))
                            .Append(chunk.Field, chunk.Chunk, chunk.Data);
                    }
                    finally
                    {
                        ZeroPayload(chunk.Data);
                    }
                    return;
                case TransferV3ChunkedRowEndFrame rowEnd:
                    if (_row is null || !string.Equals(_row.Cursor, rowEnd.Cursor, StringComparison.Ordinal))
                        throw Failure("blob-cursor");
                    var fact = _row.Complete(rowEnd.Fields, rowEnd.Bytes);
                    _row.Dispose();
                    _row = null;
                    try
                    {
                        _sink.AddPhysicalBlob(
                            Rows,
                            fact.NetworkUuid,
                            fact.Length,
                            fact.ContentSha256);
                        _inventoryHash.AppendData(fact.NetworkUuid);
                        Span<byte> length = stackalloc byte[sizeof(long)];
                        BinaryPrimitives.WriteInt64BigEndian(length, fact.Length);
                        _inventoryHash.AppendData(length);
                        _inventoryHash.AppendData(fact.ContentSha256);
                        Rows = checked(Rows + 1);
                        ContentBytes = checked(ContentBytes + fact.Length);
                        DecodedBytes = checked(DecodedBytes + rowEnd.Bytes);
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
            if (!_batchOpen || _row is not null)
                throw Failure("blob-sequence");
            _sink.CommitBatch();
            _batchOpen = false;
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_batchOpen || _row is not null || _completed)
                throw Failure("blob-sequence");
            var inventory = _inventoryHash.GetHashAndReset();
            try
            {
                var expectedInventory = Convert.FromHexString(_manifest.InventorySha256);
                try
                {
                    if (!string.Equals(tableEnd.Table, _manifest.Name, StringComparison.Ordinal)
                        || tableEnd.Batches != _manifest.Batches
                        || tableEnd.Rows != _manifest.Rows
                        || tableEnd.Bytes != _manifest.DecodedBytes
                        || !string.Equals(tableEnd.Sha256, _manifest.Sha256, StringComparison.Ordinal)
                        || Rows != _manifest.Count
                        || Rows != tableEnd.Rows
                        || ContentBytes != _manifest.TotalBytes
                        || DecodedBytes != tableEnd.Bytes
                        || !CryptographicOperations.FixedTimeEquals(inventory, expectedInventory))
                    {
                        throw Failure("blob-manifest");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedInventory);
                }
            }
            catch (FormatException)
            {
                throw Failure("blob-manifest");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inventory);
            }
            _completed = true;
        }

        public void Abort(Exception failure)
        {
            try
            {
                _row?.Dispose();
            }
            catch
            {
                // Preserve the authoritative parse failure.
            }
            finally
            {
                _row = null;
                if (_batchOpen)
                {
                    try
                    {
                        _sink.AbortBatchNoThrow();
                    }
                    catch
                    {
                        // The sink is specified no-throw.
                    }
                    _batchOpen = false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _row?.Dispose();
            _inventoryHash.Dispose();
        }

        private sealed class BlobRowAccumulator : IDisposable
        {
            private readonly long _rowOrdinal;
            private readonly Guid _uuid;
            private readonly int _declaredFields;
            private readonly TransferV3Limits _limits;
            private readonly CancellationToken _cancellationToken;
            private readonly IncrementalHash _contentHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private byte[]? _expectedContentSha256;
            private long _length = -1;
            private long _contentBytes;
            private long _currentFieldBytes;
            private int _currentField = -1;
            private bool _descriptorSeen;
            private bool _completed;

            internal BlobRowAccumulator(
                long rowOrdinal,
                string cursor,
                Guid uuid,
                int declaredFields,
                TransferV3Limits limits,
                CancellationToken cancellationToken)
            {
                _rowOrdinal = rowOrdinal;
                Cursor = cursor;
                _uuid = uuid;
                _declaredFields = declaredFields;
                _limits = limits;
                _cancellationToken = cancellationToken;
                if (declaredFields is < 2 or > 1024)
                    throw Failure("blob-shape");
            }

            internal string Cursor { get; }

            internal void Append(int field, int chunk, ReadOnlyMemory<byte> data)
            {
                if (_completed) throw Failure("blob-sequence");
                _cancellationToken.ThrowIfCancellationRequested();
                if (field == 0)
                {
                    if (_descriptorSeen || chunk != 0 || data.Length != DescriptorBytes)
                        throw Failure("blob-descriptor");
                    _length = BinaryPrimitives.ReadInt64BigEndian(data.Span);
                    if (_length < 0) throw Failure("blob-descriptor");
                    _expectedContentSha256 = data.Span[sizeof(long)..].ToArray();
                    var contentFields = Math.Max(
                        1L,
                        checked((_length + _limits.MaxFieldBytes - 1) / _limits.MaxFieldBytes));
                    if (contentFields > 1023
                        || _declaredFields != checked((int)(contentFields + 1)))
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
                if (_contentBytes > _length || _currentFieldBytes > _limits.MaxFieldBytes)
                    throw Failure("blob-shape");
                _contentHash.AppendData(data.Span);
            }

            internal VerifiedBlobFact Complete(int fields, long decodedBytes)
            {
                if (_completed || !_descriptorSeen || fields != _declaredFields)
                    throw Failure("blob-shape");
                ValidateFinishedContentField();
                if (_currentField != _declaredFields - 1
                    || _contentBytes != _length
                    || decodedBytes != checked(DescriptorBytes + _length)
                    || _expectedContentSha256 is null)
                {
                    throw Failure("blob-shape");
                }
                var actual = _contentHash.GetHashAndReset();
                byte[]? networkCopy = null;
                var transferred = false;
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, _expectedContentSha256))
                        throw Failure("blob-content");
                    Span<byte> network = stackalloc byte[16];
                    try
                    {
                        if (!_uuid.TryWriteBytes(
                                network,
                                bigEndian: true,
                                out var written)
                            || written != 16)
                        {
                            throw Failure("blob-cursor");
                        }
                        networkCopy = network.ToArray();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(network);
                    }
                    var fact = new VerifiedBlobFact(networkCopy, _length, actual);
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

            public void Dispose()
            {
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

        private sealed record VerifiedBlobFact(byte[] NetworkUuid, long Length, byte[] ContentSha256)
        {
            internal void Clear()
            {
                CryptographicOperations.ZeroMemory(NetworkUuid);
                CryptographicOperations.ZeroMemory(ContentSha256);
            }
        }
    }

    private sealed class TableObserver : ITransferV3FrameObserver
    {
        private readonly TransferV3SourceContract _contract;
        private readonly int _tableOrdinal;
        private readonly TransferV3TableContract _table;
        private readonly TransferV3ManifestTable _manifest;
        private readonly TransferV3Limits _limits;
        private readonly ITransferV3VerificationFactSink _sink;
        private readonly CancellationToken _cancellationToken;
        private RowAccumulator? _row;
        private bool _batchOpen;
        private bool _completed;

        internal TableObserver(
            TransferV3SourceContract contract,
            int tableOrdinal,
            TransferV3TableContract table,
            TransferV3ManifestTable manifest,
            TransferV3Limits limits,
            ITransferV3VerificationFactSink sink,
            CancellationToken cancellationToken)
        {
            _contract = contract;
            _tableOrdinal = tableOrdinal;
            _table = table;
            _manifest = manifest;
            _limits = limits;
            _sink = sink;
            _cancellationToken = cancellationToken;
        }

        internal long Rows { get; private set; }

        internal long DecodedBytes { get; private set; }

        public void Observe(TransferV3Frame frame)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(frame.Table, _table.Name, StringComparison.Ordinal))
                throw Failure("table-name");

            switch (frame)
            {
                case TransferV3TableHeaderFrame { Version: TransferV3FrameCodec.FormatVersion }:
                    return;
                case TransferV3TableHeaderFrame:
                    throw Failure("table-version");
                case TransferV3BatchStartFrame batchStart:
                    if (_batchOpen || _row is not null)
                        throw Failure("table-sequence");
                    _sink.BeginBatch(_tableOrdinal, batchStart.Batch);
                    _batchOpen = true;
                    return;
                case TransferV3RowFrame inline:
                    ZeroPayload(inline.Data);
                    throw Failure("table-inline-row");
                case TransferV3ChunkedRowStartFrame rowStart:
                    if (!_batchOpen || _row is not null)
                        throw Failure("table-sequence");
                    if (rowStart.Fields != _table.Columns.Count)
                        throw Failure("table-field-count");
                    _row = new RowAccumulator(
                        _contract,
                        _tableOrdinal,
                        _table,
                        Rows,
                        rowStart.Cursor,
                        _limits,
                        _cancellationToken);
                    return;
                case TransferV3FieldChunkFrame chunk:
                    try
                    {
                        (_row ?? throw Failure("table-sequence"))
                            .Append(chunk.Field, chunk.Data);
                    }
                    finally
                    {
                        ZeroPayload(chunk.Data);
                    }
                    return;
                case TransferV3ChunkedRowEndFrame rowEnd:
                    if (_row is null || rowEnd.Fields != _table.Columns.Count)
                        throw Failure("table-field-count");
                    var completedRow = _row;
                    var facts = completedRow.Complete(rowEnd.Cursor);
                    try
                    {
                        completedRow.Dispose();
                        _row = null;
                        _sink.AddVerifiedRow(facts);
                        Rows = checked(Rows + 1);
                        DecodedBytes = checked(DecodedBytes + rowEnd.Bytes);
                    }
                    finally
                    {
                        facts.ClearDigests();
                        completedRow.Dispose();
                        _row = null;
                    }
                    return;
                case TransferV3BatchEndFrame:
                    // The parser dispatches this through CommitBatch after its
                    // canonical digest and totals have already been verified.
                    return;
                default:
                    throw Failure("table-sequence");
            }
        }

        public void CommitBatch(TransferV3BatchEndFrame batchEnd)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_batchOpen || _row is not null)
                throw Failure("table-sequence");
            _sink.CommitBatch();
            _batchOpen = false;
        }

        public void CompleteTable(TransferV3TableEndFrame tableEnd)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_batchOpen || _row is not null || _completed
                || !string.Equals(tableEnd.Table, _manifest.Name, StringComparison.Ordinal)
                || tableEnd.Batches != _manifest.Batches
                || tableEnd.Rows != _manifest.Rows
                || tableEnd.Bytes != _manifest.DecodedBytes
                || !string.Equals(tableEnd.Sha256, _manifest.Sha256, StringComparison.Ordinal)
                || Rows != tableEnd.Rows
                || DecodedBytes != tableEnd.Bytes)
            {
                throw Failure("table-manifest");
            }

            _completed = true;
        }

        public void Abort(Exception failure)
        {
            try
            {
                _row?.Dispose();
            }
            catch
            {
                // Abort is intentionally no-throw; the primary is authoritative.
            }
            finally
            {
                _row = null;
                if (_batchOpen)
                {
                    try
                    {
                        _sink.AbortBatchNoThrow();
                    }
                    catch
                    {
                        // The sink contract is no-throw, but hostile diagnostic
                        // implementations cannot replace the primary failure.
                    }
                    _batchOpen = false;
                }
            }
        }
    }

    private sealed class RowAccumulator : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly TransferV3SourceContract _contract;
        private readonly int _tableOrdinal;
        private readonly TransferV3TableContract _table;
        private readonly long _rowOrdinal;
        private readonly string _cursor;
        private readonly ImmutableArray<TransferV3CursorComponent> _cursorComponents;
        private readonly TransferV3Limits _limits;
        private readonly CancellationToken _cancellationToken;
        private readonly TransferV3VerifiedFieldFact?[] _fields;
        private FieldAccumulator? _current;
        private int _currentField = -1;
        private bool _completed;
        private bool _factsTransferred;
        private bool _disposed;

        internal RowAccumulator(
            TransferV3SourceContract contract,
            int tableOrdinal,
            TransferV3TableContract table,
            long rowOrdinal,
            string cursor,
            TransferV3Limits limits,
            CancellationToken cancellationToken)
        {
            _contract = contract;
            _tableOrdinal = tableOrdinal;
            _table = table;
            _rowOrdinal = rowOrdinal;
            _cursor = cursor;
            _limits = limits;
            _cancellationToken = cancellationToken;
            _fields = new TransferV3VerifiedFieldFact?[table.Columns.Count];
            try
            {
                _cursorComponents = TransferV3CursorCodec.Decode(cursor).ToImmutableArray();
            }
            catch (FormatException)
            {
                throw Failure("cursor-shape");
            }

            if (_cursorComponents.Length != table.Keyset.Count)
                throw Failure("cursor-shape");
            for (var index = 0; index < table.Keyset.Count; index++)
            {
                var column = table.Columns[FindColumn(table, table.Keyset[index].Column)];
                var expected = CursorType(column.Kind);
                if (_cursorComponents[index].Type != expected)
                    throw Failure("cursor-shape");
            }
        }

        internal void Append(int fieldOrdinal, ReadOnlyMemory<byte> data)
        {
            EnsureReady();
            _cancellationToken.ThrowIfCancellationRequested();
            if (fieldOrdinal != _currentField)
            {
                if (fieldOrdinal != _currentField + 1)
                    throw Failure("table-field-order");
                FinalizeCurrent();
                _currentField = fieldOrdinal;
                var keyIndex = FindKeyIndex(_table, fieldOrdinal);
                _current = new FieldAccumulator(
                    fieldOrdinal,
                    _table.Columns[fieldOrdinal],
                    _limits,
                    keyIndex < 0 ? null : _cursorComponents[keyIndex],
                    IsReviewedSecretValue(fieldOrdinal),
                    _cancellationToken);
            }

            _current!.Append(data);
        }

        internal TransferV3VerifiedRowFacts Complete(string cursor)
        {
            EnsureReady();
            _cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(cursor, _cursor, StringComparison.Ordinal))
                throw Failure("cursor-value");
            FinalizeCurrent();
            if (_currentField != _table.Columns.Count - 1
                || _fields.Any(value => value is null))
            {
                throw Failure("table-field-count");
            }

            var concreteFields = _fields.Select(value => value!).ToImmutableArray();
            ValidateCursorValues(concreteFields);
            byte[] cursorBytes;
            try
            {
                cursorBytes = TransferV3CursorCodec.DecodeBase64Url(_cursor);
            }
            catch (FormatException)
            {
                throw Failure("cursor-shape");
            }

            byte[]? keySha = null;
            var unique = ImmutableArray.CreateBuilder<TransferV3VerifiedUniqueKeyFact>(
                _table.UniqueKeys.Count);
            Span<byte> uniqueNumber = stackalloc byte[sizeof(long)];
            var transferred = false;
            try
            {
                try
                {
                    using var keyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    AppendDomainString(keyHash, "nzbdav-transfer-v3-row-key-v1");
                    AppendDomainString(keyHash, _table.Name);
                    keyHash.AppendData(cursorBytes);
                    keySha = keyHash.GetHashAndReset();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cursorBytes);
                }

                var baseOrdinal = _contract.Tables
                    .Take(_tableOrdinal)
                    .Sum(value => value.UniqueKeys.Count);
                for (var ruleIndex = 0; ruleIndex < _table.UniqueKeys.Count; ruleIndex++)
                {
                    var rule = _table.UniqueKeys[ruleIndex];
                    var selected = rule.Columns
                        .Select(name => concreteFields[FindColumn(_table, name)])
                        .ToArray();
                    if (selected.Any(field => field.IsNull))
                        continue;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    AppendDomainString(hash, "nzbdav-transfer-v3-unique-v1");
                    AppendDomainString(hash, _table.Name);
                    AppendDomainString(hash, rule.Name);
                    BinaryPrimitives.WriteInt32BigEndian(uniqueNumber, selected.Length);
                    hash.AppendData(uniqueNumber[..sizeof(int)]);
                    foreach (var field in selected)
                    {
                        hash.AppendData([(byte)_table.Columns[field.ColumnOrdinal].Kind, 0]);
                        BinaryPrimitives.WriteInt64BigEndian(uniqueNumber, field.EncodedBytes);
                        hash.AppendData(uniqueNumber);
                        hash.AppendData(field.EncodedSha256);
                    }
                    var uniqueDigest = hash.GetHashAndReset();
                    try
                    {
                        unique.Add(new TransferV3VerifiedUniqueKeyFact(
                            checked(baseOrdinal + ruleIndex),
                            uniqueDigest));
                    }
                    catch
                    {
                        CryptographicOperations.ZeroMemory(uniqueDigest);
                        throw;
                    }
                }

                var facts = new TransferV3VerifiedRowFacts(
                    _tableOrdinal,
                    _rowOrdinal,
                    _cursorComponents,
                    keySha,
                    unique.ToImmutable(),
                    concreteFields);
                _completed = true;
                _factsTransferred = true;
                transferred = true;
                return facts;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(uniqueNumber);
                if (!transferred)
                {
                    if (keySha is not null)
                        CryptographicOperations.ZeroMemory(keySha);
                    foreach (var value in unique)
                        CryptographicOperations.ZeroMemory(value.Sha256);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Dispose();
            _current = null;
            if (!_factsTransferred)
            {
                foreach (var field in _fields)
                {
                    if (field is not null)
                        CryptographicOperations.ZeroMemory(field.EncodedSha256);
                }
            }
        }

        private void FinalizeCurrent()
        {
            if (_current is null) return;
            _fields[_currentField] = _current.Complete();
            _current.Dispose();
            _current = null;
        }

        private bool IsReviewedSecretValue(int fieldOrdinal) =>
            string.Equals(_table.Name, "ConfigItems", StringComparison.Ordinal)
            && fieldOrdinal == 1
            && _cursorComponents.Length == 1
            && _cursorComponents[0].Type == TransferV3CursorComponentType.Text
            && _contract.Bootstrap.Config.Any(value =>
                string.Equals(value.Name, _cursorComponents[0].TextValue, StringComparison.Ordinal));

        private void ValidateCursorValues(ImmutableArray<TransferV3VerifiedFieldFact> fields)
        {
            var canonical = new TransferV3CursorComponent[_table.Keyset.Count];
            for (var keyIndex = 0; keyIndex < _table.Keyset.Count; keyIndex++)
            {
                var field = fields[FindColumn(_table, _table.Keyset[keyIndex].Column)];
                var cursor = _cursorComponents[keyIndex];
                if (field.IsNull)
                    throw Failure("cursor-value");
                canonical[keyIndex] = cursor.Type switch
                {
                    TransferV3CursorComponentType.Uuid when field.UuidValue == cursor.UuidValue =>
                        TransferV3CursorComponent.FromGuid(cursor.UuidValue),
                    TransferV3CursorComponentType.SignedInteger
                        when field.IntegerValue == cursor.IntegerValue =>
                        TransferV3CursorComponent.FromInt64(cursor.IntegerValue),
                    TransferV3CursorComponentType.Text =>
                        TransferV3CursorComponent.FromText(cursor.TextValue!),
                    _ => throw Failure("cursor-value"),
                };
            }

            string encoded;
            try
            {
                encoded = TransferV3CursorCodec.Encode(canonical);
            }
            catch
            {
                throw Failure("cursor-shape");
            }
            if (!string.Equals(encoded, _cursor, StringComparison.Ordinal))
                throw Failure("cursor-value");
        }

        private void EnsureReady()
        {
            if (_completed) throw Failure("table-sequence");
        }

        private static int FindKeyIndex(TransferV3TableContract table, int fieldOrdinal)
        {
            var column = table.Columns[fieldOrdinal].Name;
            for (var index = 0; index < table.Keyset.Count; index++)
            {
                if (string.Equals(table.Keyset[index].Column, column, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }
    }

    private sealed class FieldAccumulator : IDisposable
    {
        private readonly int _ordinal;
        private readonly TransferV3ColumnContract _column;
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly TransferV3RowCodec.TransferV3TextFieldDecoder? _textDecoder;
        private readonly TransferV3CursorComponent? _keyComponent;
        private readonly bool _reviewedSecret;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _fixed = new byte[17];
        private readonly byte[]? _expectedKeyText;
        private int _fixedBytes;
        private long _encodedBytes;
        private int _keyTextOffset;
        private int _reviewedTextBytes;
        private bool _reviewedTextValid = true;
        private bool _completed;

        internal FieldAccumulator(
            int ordinal,
            TransferV3ColumnContract column,
            TransferV3Limits limits,
            TransferV3CursorComponent? keyComponent,
            bool reviewedSecret,
            CancellationToken cancellationToken)
        {
            _ordinal = ordinal;
            _column = column;
            _keyComponent = keyComponent;
            _reviewedSecret = reviewedSecret;
            _cancellationToken = cancellationToken;
            if (column.Kind == TransferV3ColumnKind.Text)
            {
                _textDecoder = TransferV3RowCodec.CreateTextFieldDecoder(
                    column,
                    limits.MaxFieldBytes,
                    new TransferV3FieldStreamMetrics());
                if (keyComponent is { Type: TransferV3CursorComponentType.Text })
                    _expectedKeyText = Encoding.UTF8.GetBytes(keyComponent.Value.TextValue!);
            }
        }

        internal void Append(ReadOnlyMemory<byte> data)
        {
            if (_completed) throw Failure("table-field-state");
            _cancellationToken.ThrowIfCancellationRequested();
            _hash.AppendData(data.Span);
            _encodedBytes = checked(_encodedBytes + data.Length);
            if (_textDecoder is not null)
            {
                var payload = _textDecoder.Append(data, _cancellationToken);
                if (_expectedKeyText is not null)
                {
                    if (_keyTextOffset + payload.Length > _expectedKeyText.Length
                        || !payload.Span.SequenceEqual(
                            _expectedKeyText.AsSpan(_keyTextOffset, payload.Length)))
                    {
                        throw Failure("cursor-value");
                    }
                    _keyTextOffset += payload.Length;
                }
                if (_reviewedSecret)
                {
                    foreach (var value in payload.Span)
                    {
                        _reviewedTextValid &= value is >= (byte)'0' and <= (byte)'9'
                            or >= (byte)'a' and <= (byte)'f';
                    }
                    _reviewedTextBytes = checked(_reviewedTextBytes + payload.Length);
                    if (_reviewedTextBytes > 32) _reviewedTextValid = false;
                }
                return;
            }

            if (_fixedBytes + data.Length > _fixed.Length)
                throw TransferV3RowCodec.Failure("field-length");
            data.Span.CopyTo(_fixed.AsSpan(_fixedBytes));
            _fixedBytes += data.Length;
        }

        internal TransferV3VerifiedFieldFact Complete()
        {
            if (_completed) throw Failure("table-field-state");
            _cancellationToken.ThrowIfCancellationRequested();
            TransferV3DecodedField decoded;
            bool reviewedPatternValid;
            if (_textDecoder is not null)
            {
                var streamed = _textDecoder.Complete(_cancellationToken);
                if (_expectedKeyText is not null
                    && (streamed.IsNull || _keyTextOffset != _expectedKeyText.Length))
                {
                    throw Failure("cursor-value");
                }
                decoded = new TransferV3DecodedField(streamed.IsNull, null);
                reviewedPatternValid = !_reviewedSecret
                    || (!streamed.IsNull && _reviewedTextValid && _reviewedTextBytes == 32);
            }
            else
            {
                decoded = TransferV3RowCodec.DecodeField(
                    _column,
                    _fixed.AsSpan(0, _fixedBytes));
                reviewedPatternValid = false;
            }

            var digest = _hash.GetHashAndReset();
            try
            {
                var fact = new TransferV3VerifiedFieldFact(
                    _ordinal,
                    decoded.IsNull,
                    _encodedBytes,
                    digest,
                    decoded.Value is Guid uuid ? uuid : null,
                    decoded.Value switch
                    {
                        int value => value,
                        long value => value,
                        bool value => value ? 1 : 0,
                        _ => null,
                    },
                    decoded.Value is DateTime timestamp ? timestamp : null,
                    decoded.Value is bool boolean ? boolean : null,
                    reviewedPatternValid);
                _completed = true;
                return fact;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(digest);
                throw;
            }
        }

        public void Dispose()
        {
            _hash.Dispose();
            CryptographicOperations.ZeroMemory(_fixed);
            if (_expectedKeyText is not null)
                CryptographicOperations.ZeroMemory(_expectedKeyText);
        }
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finalized;

        internal long BytesRead { get; private set; }

        internal byte[] GetHashAndReset()
        {
            if (_finalized) throw Failure("table-source");
            _finalized = true;
            return _hash.GetHashAndReset();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Observe(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Observe(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Observe(buffer.Span[..read]);
            return read;
        }

        private void Observe(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            _hash.AppendData(bytes);
            BytesRead = checked(BytesRead + bytes.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static int FindColumn(TransferV3TableContract table, string name)
    {
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        throw Failure("table-contract");
    }

    private static TransferV3CursorComponentType CursorType(TransferV3ColumnKind kind) =>
        kind switch
        {
            TransferV3ColumnKind.Uuid => TransferV3CursorComponentType.Uuid,
            TransferV3ColumnKind.Text => TransferV3CursorComponentType.Text,
            TransferV3ColumnKind.Boolean
                or TransferV3ColumnKind.EnumInt32
                or TransferV3ColumnKind.Int32
                or TransferV3ColumnKind.Int64
                or TransferV3ColumnKind.Instant => TransferV3CursorComponentType.SignedInteger,
            _ => throw Failure("cursor-shape"),
        };

    private static void AppendDomainString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ZeroPayload(ReadOnlyMemory<byte> payload)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload, out var segment)
            && segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(segment.AsSpan());
        }
    }
}

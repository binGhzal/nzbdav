using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3SnapshotExporterFaultPoint
{
    AfterTableExport,
    AfterBlobExport,
    AfterSessionFinalized,
    BeforeManifestPublication,
}

internal sealed record TransferV3SnapshotExporterHooks(
    Action<TransferV3SnapshotExporterFaultPoint>? AfterFaultPoint = null,
    TransferV3SnapshotDirectoryHooks? SnapshotDirectoryHooks = null,
    TransferV3SqliteTableExporterHooks? TableExporterHooks = null,
    TransferV3BlobBundleWriterHooks? BlobBundleWriterHooks = null,
    Action<bool>? AfterManifestBufferCleared = null,
    Action? BeforePublishedSnapshotClose = null);

internal sealed record TransferV3SnapshotExportResult(
    string ManifestSha256,
    TransferV3SqliteTableExportMetrics TableMetrics,
    TransferV3BlobBundleMetrics BlobMetrics,
    ImmutableArray<string> CleanupCodes);

internal sealed class TransferV3SnapshotExporter
{
    private const string BlobFileName = "Blobs.jsonl";
    private readonly TransferV3SnapshotExporterHooks? _hooks;

    internal TransferV3SnapshotExporter(TransferV3SnapshotExporterHooks? hooks = null)
    {
        _hooks = hooks;
    }

    internal async Task<TransferV3SnapshotExportResult> ExportAsync(
        TransferV3SqliteExportSession session,
        string outputPath,
        TransferV3Limits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.MaxFieldBytes < 40)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Transfer-v3 snapshot fields must be at least 40 bytes.");
        }
        if (session.State != TransferV3SqliteExportSessionState.Ready)
        {
            throw new InvalidOperationException(
                "The Transfer-v3 export session must be ready before snapshot creation.");
        }

        var contract = TransferV3SourceContract.LoadEmbedded();
        var expectedDataFiles = BuildExpectedDataFiles(contract);
        TransferV3SnapshotDirectory? snapshot = null;
        byte[]? manifestBytes = null;
        var published = false;
        try
        {
            snapshot = TransferV3SnapshotDirectory.CreateNew(
                outputPath,
                _hooks?.SnapshotDirectoryHooks);
            var outputs = snapshot.CreateDataOutputFactory(expectedDataFiles);
            var pending = await session.RunExportAsync(
                    async (context, token) =>
                    {
                        var tableResult = await new TransferV3SqliteTableExporter(
                                _hooks?.TableExporterHooks)
                            .ExportAsync(context, limits, outputs, token)
                            .ConfigureAwait(false);
                        InvokeHook(
                            TransferV3SnapshotExporterFaultPoint.AfterTableExport,
                            token);

                        var blobResult = await new TransferV3BlobBundleWriter(
                                _hooks?.BlobBundleWriterHooks)
                            .ExportAsync(context, limits, outputs, token)
                            .ConfigureAwait(false);
                        InvokeHook(
                            TransferV3SnapshotExporterFaultPoint.AfterBlobExport,
                            token);

                        ValidateDescriptors(
                            contract,
                            context.Validation,
                            tableResult,
                            blobResult,
                            expectedDataFiles);
                        var manifest = BuildManifest(
                            contract,
                            context.Validation,
                            context.Provenance,
                            limits,
                            tableResult,
                            blobResult);
                        manifestBytes = TransferV3ManifestCodec.Serialize(manifest, contract);
                        return new PendingSnapshot(
                            tableResult.Metrics,
                            blobResult.Metrics);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (manifestBytes is null)
            {
                throw new InvalidDataException(
                    "The Transfer-v3 manifest was not assembled by the export callback.");
            }
            InvokeHook(
                TransferV3SnapshotExporterFaultPoint.AfterSessionFinalized,
                cancellationToken);
            InvokeHook(
                TransferV3SnapshotExporterFaultPoint.BeforeManifestPublication,
                cancellationToken);
            await snapshot.PublishManifestAsync(manifestBytes, cancellationToken)
                .ConfigureAwait(false);
            published = true;

            var manifestSha256 = TransferV3ManifestCodec.ComputeSha256(manifestBytes);
            var closeCodes = ClosePublishedSnapshot(
                snapshot,
                _hooks?.BeforePublishedSnapshotClose);
            snapshot = null;
            return new TransferV3SnapshotExportResult(
                manifestSha256,
                pending.TableMetrics,
                pending.BlobMetrics,
                closeCodes);
        }
        catch (Exception primary)
        {
            if (snapshot is not null)
            {
                var cleanupCodes = new List<string>();
                try
                {
                    snapshot.Dispose();
                }
                catch (Exception cleanup)
                {
                    AddCleanupEvidence(cleanupCodes, cleanup);
                }

                if (!published && snapshot.CleanupResiduePath is { } residuePath)
                {
                    TransferV3Posix.AddCleanupCode(
                        cleanupCodes,
                        "snapshot-empty-root-residue");
                    try
                    {
                        primary.Data[TransferV3SnapshotDirectory.EmptyResiduePathDataKey] =
                            residuePath;
                    }
                    catch
                    {
                        // Operational residue evidence must not replace the primary.
                    }
                }

                if (cleanupCodes.Count > 0)
                    TransferV3Posix.ThrowPrimaryWithCleanupCodes(primary, cleanupCodes);
            }

            throw;
        }
        finally
        {
            if (manifestBytes is not null)
            {
                CryptographicOperations.ZeroMemory(manifestBytes);
                var cleared = manifestBytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;
                try
                {
                    _hooks?.AfterManifestBufferCleared?.Invoke(cleared);
                }
                catch
                {
                    // Test/diagnostic observation must never replace the export outcome.
                }
            }
        }
    }

    private void InvokeHook(
        TransferV3SnapshotExporterFaultPoint point,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hooks?.AfterFaultPoint?.Invoke(point);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ImmutableArray<string> BuildExpectedDataFiles(
        TransferV3SourceContract contract)
    {
        var names = ImmutableArray.CreateBuilder<string>(contract.Tables.Count + 1);
        for (var index = 0; index < contract.Tables.Count; index++)
        {
            names.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"table-{index + 1:000}-{contract.Tables[index].Name}.jsonl"));
        }
        names.Add(BlobFileName);
        return names.MoveToImmutable();
    }

    private static TransferV3Manifest BuildManifest(
        TransferV3SourceContract contract,
        TransferV3ValidatedSource validation,
        TransferV3SourceProvenance provenance,
        TransferV3Limits limits,
        TransferV3SqliteTableExportResult tables,
        TransferV3BlobBundleExportResult blobs) =>
        new(
            contract.FormatVersion,
            contract.Provider,
            validation.ContractSha256,
            contract.SourceSchemaSha256,
            contract.MigrationSourceContractSha256,
            provenance.SourceTimeZoneId,
            new TransferV3ManifestLimits(
                limits.MaxFieldBytes,
                limits.MaxBatchRows,
                limits.MaxBatchBytes),
            tables.Tables,
            tables.DerivedTables,
            validation.InformationalReferences.Select(reference =>
                new TransferV3ManifestInformationalReference(
                    reference.Name,
                    reference.UnresolvedCount,
                    reference.UnresolvedSha256)),
            blobs.Blobs);

    private static void ValidateDescriptors(
        TransferV3SourceContract contract,
        TransferV3ValidatedSource validation,
        TransferV3SqliteTableExportResult tables,
        TransferV3BlobBundleExportResult blobs,
        ImmutableArray<string> expectedDataFiles)
    {
        if (tables.Tables.Length != contract.Tables.Count
            || tables.DerivedTables.Length != contract.DerivedTables.Count
            || expectedDataFiles.Length != contract.Tables.Count + 1
            || validation.Tables.Count != contract.Tables.Count
            || !string.Equals(
                validation.ContractSha256,
                contract.ComputeSha256(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Transfer-v3 snapshot descriptors did not match the reviewed contract.");
        }

        for (var index = 0; index < contract.Tables.Count; index++)
        {
            if (!string.Equals(
                    tables.Tables[index].Name,
                    contract.Tables[index].Name,
                    StringComparison.Ordinal)
                || !string.Equals(
                    tables.Tables[index].File,
                    expectedDataFiles[index],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A Transfer-v3 table descriptor did not match its reviewed output.");
            }
        }

        for (var index = 0; index < contract.DerivedTables.Count; index++)
        {
            if (!string.Equals(
                    tables.DerivedTables[index].Name,
                    contract.DerivedTables[index].Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A Transfer-v3 derived descriptor did not match the reviewed contract.");
            }
        }

        if (!string.Equals(blobs.Blobs.Name, "Blobs", StringComparison.Ordinal)
            || !string.Equals(blobs.Blobs.File, BlobFileName, StringComparison.Ordinal)
            || !string.Equals(expectedDataFiles[^1], BlobFileName, StringComparison.Ordinal)
            || blobs.Blobs.Count != validation.Blobs.Count
            || blobs.Blobs.TotalBytes != validation.Blobs.TotalBytes
            || !string.Equals(
                blobs.Blobs.InventorySha256,
                validation.Blobs.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Transfer-v3 blob descriptor did not match retained validation.");
        }
    }

    private static ImmutableArray<string> ClosePublishedSnapshot(
        TransferV3SnapshotDirectory snapshot,
        Action? beforeClose)
    {
        var cleanupCodes = new List<string>();
        try
        {
            beforeClose?.Invoke();
            snapshot.Dispose();
        }
        catch (Exception first)
        {
            AddCleanupEvidence(cleanupCodes, first);
            try
            {
                snapshot.Dispose();
            }
            catch (Exception second)
            {
                AddCleanupEvidence(cleanupCodes, second);
            }
            if (cleanupCodes.Count == 0)
            {
                TransferV3Posix.AddCleanupCode(
                    cleanupCodes,
                    "snapshot-descriptor-close-failed");
            }
        }
        return [.. cleanupCodes];
    }

    private static void AddCleanupEvidence(
        List<string> cleanupCodes,
        Exception cleanup)
    {
        try
        {
            if (cleanup.Data["TransferV3CleanupCodes"] is IEnumerable<string> codes)
            {
                var initialCount = cleanupCodes.Count;
                foreach (var code in codes)
                {
                    if (IsKnownCleanupCode(code))
                        TransferV3Posix.AddCleanupCode(cleanupCodes, code);
                }
                if (cleanupCodes.Count > initialCount)
                    return;
            }
        }
        catch
        {
            // Hostile diagnostic storage is reduced to a generic sanitized code.
        }

        TransferV3Posix.AddCleanupCode(cleanupCodes, "snapshot-cleanup-failed");
    }

    private static bool IsKnownCleanupCode(string? code) => code is
        "cleanup-failed"
        or "untracked-created-entry"
        or "unknown-entry-residue"
        or "owned-entry-unlink-failed"
        or "owned-stream-close-failed"
        or "owned-descriptor-close-failed"
        or "owned-descriptor-stat-failed"
        or "candidate-descriptor-close-failed"
        or "external-hard-link-residue"
        or "snapshot-entry-scan-failed"
        or "snapshot-directory-sync-failed"
        or "snapshot-residue-check-failed"
        or "manifest-stream-close-failed"
        or "root-descriptor-close-failed"
        or "parent-descriptor-close-failed";

    private sealed record PendingSnapshot(
        TransferV3SqliteTableExportMetrics TableMetrics,
        TransferV3BlobBundleMetrics BlobMetrics);
}

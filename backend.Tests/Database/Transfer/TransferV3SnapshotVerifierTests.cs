using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ValidExportReturnsPinnedFullyReceiptedSnapshot()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var output = Path.Combine(source.ValidationWorkspaceRoot, "verified-snapshot");
        await using (var session = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                session,
                output,
                new TransferV3Limits(1024 * 1024, 17, 2 * 1024 * 1024));
        }

        using var verified = await new TransferV3SnapshotVerifier().VerifyAsync(output);

        Assert.Equal(27, verified.Manifest.Tables.Length);
        Assert.NotNull(verified.Metrics);
        Assert.Equal(27, verified.Metrics!.Tables.Length);
        Assert.Equal(verified.Manifest.Blobs.Count, verified.Metrics.Blobs.Rows);
        Assert.Equal(verified.Manifest.Tables.Sum(value => value.Rows),
            verified.Metrics.Tables.Sum(value => value.Rows));
        verified.VerifyUnchanged();
        using var first = verified.OpenTableRead(0);
        using var blobs = verified.OpenBlobBundleRead();
        Assert.True(first.Length > 0);
        Assert.True(blobs.Length > 0);
    }

    [Fact]
    public async Task VerifyAsync_ExtraEntryRejectsWithoutLeakingCallerPath()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var output = Path.Combine(source.ValidationWorkspaceRoot, "hostile-private-path");
        await using (var session = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                session,
                output,
                new TransferV3Limits(1024 * 1024));
        }
        await File.WriteAllBytesAsync(Path.Combine(output, "extra-secret-name"), [1]);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            new TransferV3SnapshotVerifier().VerifyAsync(output));

        Assert.Equal("snapshot-entry-set", failure.Code);
        Assert.DoesNotContain(output, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("extra-secret-name", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_PreCanceledTokenIsPreservedExactly()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TransferV3SnapshotVerifier().VerifyAsync(
                "/the-snapshot-path-must-not-be-opened",
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task VerifyAsync_ReaderAndIndexCancellationPreserveTheExactCallerToken()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var output = Path.Combine(source.ValidationWorkspaceRoot, "cancel-snapshot");
        await using (var session = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                session,
                output,
                new TransferV3Limits(1024 * 1024));
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
                ReaderHooks: new TransferV3SnapshotReaderHooks((point, _) =>
                {
                    if (point == TransferV3SnapshotReaderFaultPoint.AfterRootOpened)
                        cancellation.Cancel();
                })));
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                verifier.VerifyAsync(output, cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
                IndexHooks: new TransferV3BlobReferenceIndexHooks(point =>
                {
                    if (!string.Equals(point, "before-write", StringComparison.Ordinal)) return;
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                })));
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                verifier.VerifyAsync(output, cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
    }

    [Fact]
    public async Task VerifyAsync_InjectedIndexFailureRedactsHookDataAndCallerPath()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var output = Path.Combine(source.ValidationWorkspaceRoot, "redaction-private-snapshot");
        await using (var session = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                session,
                output,
                new TransferV3Limits(1024 * 1024));
        }
        const string redactionCanary = "redaction canary: hook data";
        const string privateUuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string fullDigest =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
            IndexHooks: new TransferV3BlobReferenceIndexHooks(point =>
            {
                if (string.Equals(point, "before-write", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"{output}|{redactionCanary}|{privateUuid}|{fullDigest}");
            })));

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            verifier.VerifyAsync(output));

        Assert.Equal("index-write", failure.Code);
        Assert.DoesNotContain(output, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(redactionCanary, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privateUuid, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fullDigest, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticClosure_EmptyFactsWithReviewedBootstrapAndZeroSummariesPasses()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        await using var index = await CreateSemanticBaselineAsync(contract);

        index.ValidateSemanticClosure(contract, CreateSemanticManifest(contract));

        var counts = index.GetFactCounts();
        Assert.Equal(2, counts.BootstrapConfigSecrets);
        Assert.Equal(5, counts.BootstrapRootMarkers);
    }

    [Fact]
    public async Task SemanticClosure_DeclaredForeignKeyMissingRejects()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        await using var index = await CreateSemanticBaselineAsync(contract);
        var value = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        index.BeginBatch(6, 0);
        index.AddUuidValue(1, 0, value);
        index.CommitBatch();

        var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            index.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));

        Assert.Equal("foreign-key", failure.Code);
    }

    [Fact]
    public async Task SemanticClosure_StateAwareTombstoneAlternativePasses()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        await using var index = await CreateSemanticBaselineAsync(contract);
        var value = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        index.BeginBatch(5, 0);
        index.AddUuidValue(9, 0, value);
        index.CommitBatch();
        index.BeginBatch(23, 0);
        index.AddUuidValue(0, 0, value);
        index.CommitBatch();

        index.ValidateSemanticClosure(contract, CreateSemanticManifest(contract));
    }

    [Fact]
    public async Task SemanticClosure_ApplicationNameReferenceRejectsDespitePhysicalBlob()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        await using var index = await CreateSemanticBaselineAsync(contract);
        var value = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var digest = SHA256.HashData("blob"u8.ToArray());
        index.BeginBatch(3, 0);
        index.AddUuidValue(0, 0, value);
        index.AddHardBlobReference(3, 0, value);
        index.CommitBatch();
        index.BeginBatch(27, 0);
        index.AddPhysicalBlob(value, 4, digest);
        index.CommitBatch();

        var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            index.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));

        Assert.Equal("reference-hard", failure.Code);
    }

    [Fact]
    public async Task SemanticClosure_ConditionalCleanupAllowsTombstoneOnlyWithoutLiveUse()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var value = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        await using (var accepted = await CreateSemanticBaselineAsync(contract))
        {
            accepted.BeginBatch(24, 0);
            accepted.AddUuidValue(0, 0, value);
            accepted.CommitBatch();
            accepted.BeginBatch(25, 0);
            accepted.AddUuidValue(0, 0, value);
            accepted.CommitBatch();
            accepted.ValidateSemanticClosure(contract, CreateSemanticManifest(contract));
        }

        await using var rejected = await CreateSemanticBaselineAsync(contract);
        rejected.BeginBatch(24, 0);
        rejected.AddUuidValue(0, 0, value);
        rejected.CommitBatch();
        rejected.BeginBatch(25, 0);
        rejected.AddUuidValue(0, 0, value);
        rejected.CommitBatch();
        rejected.BeginBatch(3, 0);
        rejected.AddUuidValue(0, 0, value);
        rejected.CommitBatch();

        var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            rejected.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));

        Assert.Equal("reference-conditional", failure.Code);
    }

    [Fact]
    public async Task SemanticClosure_MetadataDomainAndSourceAreIndependent()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var id = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var root = new byte[16];
        await using (var invalidDomain = await CreateSemanticBaselineAsync(contract))
        {
            AddDavPrincipalAndParent(invalidDomain, id, root, type: 2, subType: 4);
            var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
                invalidDomain.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));
            Assert.Equal("type-subtype-domain", failure.Code);
        }

        await using var missingSource = await CreateSemanticBaselineAsync(contract);
        AddDavPrincipalAndParent(missingSource, id, root, type: 2, subType: 201);
        var sourceFailure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            missingSource.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));
        Assert.Equal("metadata-source", sourceFailure.Code);
    }

    [Fact]
    public async Task SemanticClosure_InformationalAndDerivedEvidenceMustMatchManifest()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var owner = Network(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var target = Network(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        await using (var informational = await CreateSemanticBaselineAsync(contract))
        {
            informational.BeginBatch(2, 0);
            informational.AddInformationalFact(0, 0, owner, target, null);
            informational.CommitBatch();
            var failure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
                informational.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));
            Assert.Equal("informational-reference", failure.Code);
        }

        await using var derived = await CreateSemanticBaselineAsync(contract);
        derived.BeginBatch(14, 0);
        derived.AddHealthBucket(0, 86_400, 0, 0, 1);
        derived.CommitBatch();
        var derivedFailure = Assert.Throws<TransferV3SnapshotVerificationException>(() =>
            derived.ValidateSemanticClosure(contract, CreateSemanticManifest(contract)));
        Assert.Equal("derived-state", derivedFailure.Code);
    }

    [Fact]
    public async Task TableFrames_ValidChunkedRowBindsContractCursorAndManifest()
    {
        var fixture = await WriteBlobCleanupTableAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        await using var source = new MemoryStream(fixture.Bytes, writable: false);
        var sink = new RecordingFactSink();

        var result = await TransferV3SnapshotFrameVerifier.VerifyTableAsync(
            source,
            fixture.Contract,
            fixture.TableOrdinal,
            fixture.Manifest,
            fixture.Limits,
            sink);

        Assert.Equal(1, result.Rows);
        Assert.Equal(fixture.Bytes.Length, result.RawBytes);
        Assert.Equal(32, result.RawSha256.Length);
        var row = Assert.Single(sink.Rows);
        Assert.Equal(fixture.TableOrdinal, row.TableOrdinal);
        Assert.Equal(0, row.RowOrdinal);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Assert.Single(row.CursorComponents).UuidValue);
        Assert.Equal(32, row.KeySha256.Length);
        var field = Assert.Single(row.Fields);
        Assert.False(field.IsNull);
        Assert.Equal(17, field.EncodedBytes);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), field.UuidValue);
        Assert.Equal(32, field.EncodedSha256.Length);
        Assert.Equal(1, sink.BeginBatches);
        Assert.Equal(1, sink.CommitBatches);
        Assert.Equal(0, sink.AbortBatches);
    }

    [Fact]
    public async Task TableFrames_RejectsInlineRowsEvenWhenGenericFramingIsValid()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = contract.Tables
            .Select((table, index) => (table, index))
            .Single(value => value.table.Name == "BlobCleanupItems")
            .index;
        var limits = new TransferV3Limits(1024 * 1024);
        await using var bytes = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(bytes, "BlobCleanupItems", limits))
        {
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            await writer.WriteRowAsync(
                TransferV3CursorCodec.Encode(TransferV3CursorComponent.FromGuid(Guid.NewGuid())),
                new byte[17]);
            await writer.EndBatchAsync();
            var end = await writer.EndTableAsync();
            bytes.Position = 0;
            var manifest = Descriptor("BlobCleanupItems", tableOrdinal, end);

            var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
                TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                    bytes,
                    contract,
                    tableOrdinal,
                    manifest,
                    limits,
                    new RecordingFactSink()));

            Assert.Equal("table-inline-row", failure.Code);
            Assert.DoesNotContain("Guid", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TableFrames_RejectsCursorThatDoesNotEqualDecodedKeyField()
    {
        var fixture = await WriteBlobCleanupTableAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            cursor: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                source,
                fixture.Contract,
                fixture.TableOrdinal,
                fixture.Manifest,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("cursor-value", failure.Code);
    }

    [Fact]
    public async Task TableFrames_RejectsWrongFieldCountBeforeAcceptingRowFacts()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = contract.Tables
            .Select((table, index) => (table, index))
            .Single(value => value.table.Name == "BlobCleanupItems")
            .index;
        var limits = new TransferV3Limits(1024 * 1024);
        await using var bytes = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(bytes, "BlobCleanupItems", limits))
        {
            var cursor = TransferV3CursorCodec.Encode(
                TransferV3CursorComponent.FromGuid(Guid.NewGuid()));
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            await writer.StartChunkedRowAsync(cursor, 2);
            await writer.WriteFieldChunkAsync(0, new byte[17]);
            await writer.WriteFieldChunkAsync(1, new byte[] { 0 });
            await writer.EndChunkedRowAsync();
            await writer.EndBatchAsync();
            var end = await writer.EndTableAsync();
            bytes.Position = 0;
            var sink = new RecordingFactSink();

            var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
                TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                    bytes,
                    contract,
                    tableOrdinal,
                    Descriptor("BlobCleanupItems", tableOrdinal, end),
                    limits,
                    sink));

            Assert.Equal("table-field-count", failure.Code);
            Assert.Empty(sink.Rows);
        }
    }

    [Fact]
    public async Task TableFrames_RejectsDescriptorThatDiffersFromVerifiedTableEnd()
    {
        var fixture = await WriteBlobCleanupTableAsync(Guid.NewGuid());
        await using var source = new MemoryStream(fixture.Bytes, writable: false);
        var changed = fixture.Manifest with { Rows = fixture.Manifest.Rows + 1 };

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyTableAsync(
                source,
                fixture.Contract,
                fixture.TableOrdinal,
                changed,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("table-manifest", failure.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(145)]
    public async Task BlobFrames_VerifiesDescriptorPartitionContentAndInventory(int length)
    {
        var contents = Enumerable.Range(0, length).Select(value => (byte)(value % 251)).ToArray();
        var fixture = await WriteBlobTableAsync(contents);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);
        var sink = new RecordingFactSink();

        var result = await TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
            source,
            fixture.Manifest,
            fixture.Limits,
            sink);

        Assert.Equal(1, result.Rows);
        Assert.Equal(length, result.ContentBytes);
        Assert.Equal(40 + length, result.DecodedBytes);
        var fact = Assert.Single(sink.Blobs);
        Assert.Equal(length, fact.Length);
        Assert.Equal(SHA256.HashData(contents), fact.ContentSha256);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new Guid(fact.Uuid, bigEndian: true));
        Assert.Equal(1, sink.BeginBatches);
        Assert.Equal(1, sink.CommitBatches);
    }

    [Fact]
    public async Task BlobFrames_RejectsNegativeDescriptorLength()
    {
        var descriptor = new byte[40];
        BinaryPrimitives.WriteInt64BigEndian(descriptor, -1);
        SHA256.HashData(ReadOnlySpan<byte>.Empty).CopyTo(descriptor, 8);
        var fixture = await WriteBlobTableAsync([], descriptorOverride: descriptor);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                source,
                fixture.Manifest,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("blob-descriptor", failure.Code);
    }

    [Fact]
    public async Task BlobFrames_RejectsContentDigestMismatch()
    {
        var contents = "verified-content"u8.ToArray();
        var descriptor = new byte[40];
        BinaryPrimitives.WriteInt64BigEndian(descriptor, contents.Length);
        new byte[32].CopyTo(descriptor, 8);
        var fixture = await WriteBlobTableAsync(contents, descriptorOverride: descriptor);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                source,
                fixture.Manifest,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("blob-content", failure.Code);
    }

    [Fact]
    public async Task BlobFrames_RejectsWrongDeclaredContentFieldPartition()
    {
        var fixture = await WriteBlobTableAsync([42], extraEmptyContentField: true);
        await using var source = new MemoryStream(fixture.Bytes, writable: false);

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                source,
                fixture.Manifest,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("blob-shape", failure.Code);
    }

    [Fact]
    public async Task BlobFrames_RejectsInventoryDescriptorMismatch()
    {
        var fixture = await WriteBlobTableAsync("content"u8.ToArray());
        await using var source = new MemoryStream(fixture.Bytes, writable: false);
        var changed = fixture.Manifest with
        {
            InventorySha256 = new string('0', 64),
        };

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            TransferV3SnapshotFrameVerifier.VerifyBlobsAsync(
                source,
                changed,
                fixture.Limits,
                new RecordingFactSink()));

        Assert.Equal("blob-manifest", failure.Code);
    }

    [Fact]
    public void VerifierProductionSource_HasNoTargetDatabaseOrRuntimeBlobSurface()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SnapshotVerifier.cs"));

        Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgre", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DatabaseTransferService", source, StringComparison.Ordinal);
    }

    private static async Task<TableFixture> WriteBlobCleanupTableAsync(
        Guid id,
        Guid? cursor = null)
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var tableOrdinal = contract.Tables
            .Select((table, index) => (table, index))
            .Single(value => value.table.Name == "BlobCleanupItems")
            .index;
        var table = contract.Tables[tableOrdinal];
        var limits = new TransferV3Limits(1024 * 1024);
        await using var bytes = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(bytes, table.Name, limits))
        {
            var encoded = TransferV3RowCodec.EncodeField(table.Columns[0], id);
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            await writer.StartChunkedRowAsync(
                TransferV3CursorCodec.Encode(
                    TransferV3CursorComponent.FromGuid(cursor ?? id)),
                table.Columns.Count);
            await writer.WriteFieldChunkAsync(0, encoded);
            await writer.EndChunkedRowAsync();
            await writer.EndBatchAsync();
            var end = await writer.EndTableAsync();
            return new TableFixture(
                bytes.ToArray(),
                contract,
                tableOrdinal,
                Descriptor(table.Name, tableOrdinal, end),
                limits);
        }
    }

    private static async Task<BlobFixture> WriteBlobTableAsync(
        byte[] contents,
        byte[]? descriptorOverride = null,
        bool extraEmptyContentField = false)
    {
        const int maxFieldBytes = 64;
        var limits = new TransferV3Limits(maxFieldBytes);
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await using var bytes = new MemoryStream();
        await using (var writer = new TransferV3JsonlWriter(bytes, "Blobs", limits))
        {
            await writer.WriteTableHeaderAsync();
            await writer.StartBatchAsync(0, null);
            var contentFields = Math.Max(1, (contents.Length + maxFieldBytes - 1) / maxFieldBytes);
            await writer.StartChunkedRowAsync(
                TransferV3CursorCodec.Encode(TransferV3CursorComponent.FromGuid(id)),
                1 + contentFields + (extraEmptyContentField ? 1 : 0));
            var descriptor = descriptorOverride ?? new byte[40];
            if (descriptorOverride is null)
            {
                BinaryPrimitives.WriteInt64BigEndian(descriptor, contents.Length);
                SHA256.HashData(contents).CopyTo(descriptor, 8);
            }
            await writer.WriteFieldChunkAsync(0, descriptor);
            if (contents.Length == 0)
            {
                await writer.WriteFieldChunkAsync(1, ReadOnlyMemory<byte>.Empty);
            }
            else
            {
                var offset = 0;
                for (var field = 1; offset < contents.Length; field++)
                {
                    var count = Math.Min(maxFieldBytes, contents.Length - offset);
                    await writer.WriteFieldChunkAsync(field, contents.AsMemory(offset, count));
                    offset += count;
                }
            }
            if (extraEmptyContentField)
            {
                await writer.WriteFieldChunkAsync(
                    1 + contentFields,
                    ReadOnlyMemory<byte>.Empty);
            }
            await writer.EndChunkedRowAsync();
            await writer.EndBatchAsync();
            var end = await writer.EndTableAsync();

            Span<byte> network = stackalloc byte[16];
            Assert.True(id.TryWriteBytes(network, bigEndian: true, out var written));
            Assert.Equal(16, written);
            using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            inventory.AppendData(network);
            Span<byte> length = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(length, contents.Length);
            inventory.AppendData(length);
            inventory.AppendData(SHA256.HashData(contents));
            return new BlobFixture(
                bytes.ToArray(),
                new TransferV3ManifestBlobs(
                    "Blobs",
                    "Blobs.jsonl",
                    end.Batches,
                    end.Rows,
                    end.Bytes,
                    end.Sha256,
                    1,
                    contents.Length,
                    Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant()),
                limits);
        }
    }

    private static TransferV3ManifestTable Descriptor(
        string table,
        int ordinal,
        TransferV3TableEndFrame end) =>
        new(
            table,
            $"table-{ordinal + 1:000}-{table}.jsonl",
            end.Batches,
            end.Rows,
            end.Bytes,
            end.Sha256);

    private static async Task<TransferV3SqliteExportSession> OpenSessionAsync(
        TransferV3ValidationSource source)
    {
        using var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        var provenance = new TransferV3SourceProvenance(
            database.Identity,
            blobs.Identity,
            TimeZoneInfo.Utc.Id);
        return await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                source.Options(),
                provenance);
    }

    private static async Task<TransferV3BlobReferenceIndex> CreateSemanticBaselineAsync(
        TransferV3SourceContract contract)
    {
        var index = await TransferV3BlobReferenceIndex.CreateAsync();
        index.BeginBatch(0, 0);
        for (var config = 0; config < contract.Bootstrap.Config.Count; config++)
        {
            index.AddBootstrapConfigSecret(
                config,
                Enumerable.Repeat((byte)(config + 1), 32).ToArray());
        }
        for (var root = 0; root < contract.Bootstrap.Roots.Count; root++)
        {
            index.AddBootstrapRootMarker(
                root,
                Enumerable.Repeat((byte)(root + 17), 32).ToArray());
        }
        index.CommitBatch();
        return index;
    }

    private static TransferV3Manifest CreateSemanticManifest(
        TransferV3SourceContract contract)
    {
        var emptyDigest = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty))
            .ToLowerInvariant();
        var tables = contract.Tables.Select((table, index) =>
            new TransferV3ManifestTable(
                table.Name,
                $"table-{index + 1:000}-{table.Name}.jsonl",
                0,
                0,
                0,
                emptyDigest));
        var informational = contract.Tables
            .SelectMany(table => table.References)
            .Where(reference => reference.Policy is TransferV3ReferencePolicy.InformationalDigest
                or TransferV3ReferencePolicy.PolymorphicInformationalDigest)
            .Select(reference => new TransferV3ManifestInformationalReference(
                reference.Name,
                0,
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference.Name)))
                    .ToLowerInvariant()));
        return new TransferV3Manifest(
            3,
            contract.Provider,
            contract.ComputeSha256(),
            contract.SourceSchemaSha256,
            contract.MigrationSourceContractSha256,
            TimeZoneInfo.Utc.Id,
            new TransferV3ManifestLimits(1024 * 1024, 1000, 16 * 1024 * 1024),
            tables,
            [new TransferV3ManifestDerivedTable("HealthCheckStats", 0, emptyDigest)],
            informational,
            new TransferV3ManifestBlobs(
                "Blobs",
                "Blobs.jsonl",
                0,
                0,
                0,
                emptyDigest,
                0,
                0,
                emptyDigest));
    }

    private static void AddDavPrincipalAndParent(
        TransferV3BlobReferenceIndex index,
        byte[] id,
        byte[] root,
        long type,
        long subType)
    {
        index.BeginBatch(5, 0);
        index.AddUuidValue(0, 0, id);
        index.AddUuidValue(9, 0, root);
        index.AddUuidValue(0, 1, root);
        index.AddDavMetadata(0, id, root, type, subType, null);
        index.CommitBatch();
    }

    private static byte[] Network(Guid value)
    {
        var bytes = new byte[16];
        Assert.True(value.TryWriteBytes(bytes, bigEndian: true, out var written));
        Assert.Equal(16, written);
        return bytes;
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

    private sealed record TableFixture(
        byte[] Bytes,
        TransferV3SourceContract Contract,
        int TableOrdinal,
        TransferV3ManifestTable Manifest,
        TransferV3Limits Limits);

    private sealed record BlobFixture(
        byte[] Bytes,
        TransferV3ManifestBlobs Manifest,
        TransferV3Limits Limits);

    private sealed record BlobFact(byte[] Uuid, long Length, byte[] ContentSha256);

    private sealed class RecordingFactSink : ITransferV3VerificationFactSink
    {
        internal List<TransferV3VerifiedRowFacts> Rows { get; } = [];
        internal List<BlobFact> Blobs { get; } = [];
        internal int BeginBatches { get; private set; }
        internal int CommitBatches { get; private set; }
        internal int AbortBatches { get; private set; }

        public void BeginBatch(int tableOrdinal, int batchOrdinal) => BeginBatches++;

        public void AddVerifiedRow(TransferV3VerifiedRowFacts facts) => Rows.Add(facts);

        public void AddPhysicalBlob(
            long rowOrdinal,
            ReadOnlySpan<byte> uuidNetworkBytes,
            long length,
            ReadOnlySpan<byte> contentSha256) =>
            Blobs.Add(new BlobFact(
                uuidNetworkBytes.ToArray(),
                length,
                contentSha256.ToArray()));

        public void CommitBatch() => CommitBatches++;

        public void AbortBatchNoThrow() => AbortBatches++;
    }
}

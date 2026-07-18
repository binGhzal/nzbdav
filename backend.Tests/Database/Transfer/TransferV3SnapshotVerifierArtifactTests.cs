using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotVerifierArtifactTests
{
    private const string QueueId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string EmptyMetadataBlobId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string LargeOrphanBlobId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string DavItemId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

    [Fact]
    public async Task VerifyAsync_AcceptsCanonicalNonEmptyExporterArtifactAcrossAllEvidenceKinds()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var contract = TransferV3SourceContract.LoadEmbedded();
        var manifest = await ReadManifestAsync(fixture.OutputPath, contract);

        Assert.Equal(1, Table(manifest, "QueueItems").Rows);
        Assert.Equal(1, Table(manifest, "NzbNames").Rows);
        Assert.Equal(1, Table(manifest, "QueueNzbContents").Rows);
        Assert.True(Table(manifest, "QueueNzbContents").DecodedBytes > 128 * 1024);
        Assert.Equal(3, manifest.Blobs.Count);
        Assert.True(manifest.Blobs.TotalBytes > 1024 * 1024);
        Assert.True(manifest.InformationalReferences.Single(value =>
            value.Name == "HealthCheckResults_DavItemId").UnresolvedCount > 0);
        Assert.True(manifest.DerivedTables.Single(value =>
            value.Name == "HealthCheckStats").Rows > 0);

        var davTable = contract.Tables.Single(value => value.Name == "DavItems");
        var davRows = CanonicalArtifactRewriter.ParseRows(
            await File.ReadAllBytesAsync(Path.Combine(
                fixture.OutputPath,
                Table(manifest, "DavItems").File)));
        var migratedDav = davRows.Single(row => CursorUuid(row) == Guid.Parse(DavItemId));
        Assert.Equal(
            Guid.Parse(EmptyMetadataBlobId),
            DecodeField(davTable, 2, migratedDav).Value);
        Assert.Equal(2, DecodeField(davTable, 13, migratedDav).Value);

        var blobRows = CanonicalArtifactRewriter.ParseRows(
            await File.ReadAllBytesAsync(Path.Combine(fixture.OutputPath, "Blobs.jsonl")));
        Assert.Contains(blobRows, row =>
            CursorUuid(row) == Guid.Parse(EmptyMetadataBlobId)
            && DescriptorLength(row) == 0
            && row.Fields.Count == 2
            && row.Fields[1].Count == 1
            && row.Fields[1][0].Length == 0);
        Assert.Contains(blobRows, row =>
            CursorUuid(row) == Guid.Parse(QueueId)
            && DescriptorLength(row) == 9);
        Assert.Contains(blobRows, row =>
            CursorUuid(row) == Guid.Parse(LargeOrphanBlobId)
            && DescriptorLength(row) > manifest.Limits.MaxFieldBytes
            && row.Fields.Count > 2);

        using var verified = await new TransferV3SnapshotVerifier().VerifyAsync(fixture.OutputPath);

        Assert.Equal(3, verified.Metrics!.Facts.PhysicalBlobs);
        Assert.Equal(2, verified.Metrics.Facts.HardBlobReferences);
        Assert.True(verified.Metrics.Facts.InformationalFacts > 0);
        Assert.True(verified.Metrics.Facts.HealthBuckets > 0);
    }

    [Theory]
    [InlineData("reserved", "reserved-config")]
    [InlineData("missing", "bootstrap-config")]
    [InlineData("malformed", "bootstrap-config")]
    [InlineData("equal", "bootstrap-config")]
    public async Task VerifyAsync_RejectsCanonicalBootstrapConfigTamper(
        string mutation,
        string expectedCode)
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var rewriter = new CanonicalArtifactRewriter(fixture.OutputPath);
        await rewriter.RewriteTableAsync("ConfigItems", (table, rows) =>
        {
            var firstName = table.BootstrapConfigNames[0];
            var secondName = table.BootstrapConfigNames[1];
            var first = rows.Single(row => DecodeText(table.Contract, 0, row) == firstName);
            var second = rows.Single(row => DecodeText(table.Contract, 0, row) == secondName);
            switch (mutation)
            {
                case "reserved":
                    first.Cursor = TransferV3CursorCodec.Encode(
                        TransferV3CursorComponent.FromText(
                            TransferV3ReservedConfigPolicy.ImportStateKey));
                    first.Fields[0] = OneChunk(TransferV3RowCodec.EncodeField(
                        table.Contract.Columns[0],
                        TransferV3ReservedConfigPolicy.ImportStateKey));
                    break;
                case "missing":
                    Assert.True(rows.Remove(first));
                    break;
                case "malformed":
                    first.Fields[1] = OneChunk(TransferV3RowCodec.EncodeField(
                        table.Contract.Columns[1],
                        "not-a-reviewed-secret"));
                    break;
                case "equal":
                    second.Fields[1] = CloneChunks(first.Fields[1]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        await AssertReaderAcceptsAsync(fixture.OutputPath);
        var semanticClosureEntered = false;
        var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
            IndexHooks: new TransferV3BlobReferenceIndexHooks(point =>
            {
                if (string.Equals(point, "before-query", StringComparison.Ordinal))
                    semanticClosureEntered = true;
            })));

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            verifier.VerifyAsync(fixture.OutputPath));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(mutation == "missing", semanticClosureEntered);
    }

    [Fact]
    public async Task VerifyAsync_RejectsCanonicalArtifactWhoseLiveQueueLostItsRequiredName()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var rewriter = new CanonicalArtifactRewriter(fixture.OutputPath);
        await rewriter.RewriteTableAsync("NzbNames", (_, rows) =>
        {
            Assert.Equal(1, rows.RemoveAll(row => CursorUuid(row) == Guid.Parse(QueueId)));
        });
        await AssertReaderAcceptsAsync(fixture.OutputPath);
        var semanticClosureEntered = false;
        var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
            IndexHooks: new TransferV3BlobReferenceIndexHooks(point =>
            {
                if (string.Equals(point, "before-query", StringComparison.Ordinal))
                    semanticClosureEntered = true;
            })));

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            verifier.VerifyAsync(fixture.OutputPath));

        Assert.Equal("reference-hard", failure.Code);
        Assert.True(semanticClosureEntered);
    }

    [Fact]
    public async Task VerifyAsync_RejectsCanonicalBundleWhoseLiveEmptyBlobWasRemoved()
    {
        await using var fixture = await ArtifactFixture.CreateAsync();
        var rewriter = new CanonicalArtifactRewriter(fixture.OutputPath);
        await rewriter.RewriteBlobsAsync(rows =>
        {
            Assert.Equal(1, rows.RemoveAll(row =>
                CursorUuid(row) == Guid.Parse(EmptyMetadataBlobId)
                && DescriptorLength(row) == 0));
        });
        var manifest = await ReadManifestAsync(
            fixture.OutputPath,
            TransferV3SourceContract.LoadEmbedded());
        Assert.Equal(2, manifest.Blobs.Count);
        await AssertReaderAcceptsAsync(fixture.OutputPath);
        var semanticClosureEntered = false;
        var verifier = new TransferV3SnapshotVerifier(new TransferV3SnapshotVerifierHooks(
            IndexHooks: new TransferV3BlobReferenceIndexHooks(point =>
            {
                if (string.Equals(point, "before-query", StringComparison.Ordinal))
                    semanticClosureEntered = true;
            })));

        var failure = await Assert.ThrowsAsync<TransferV3SnapshotVerificationException>(() =>
            verifier.VerifyAsync(fixture.OutputPath));

        Assert.Equal("reference-hard", failure.Code);
        Assert.True(semanticClosureEntered);
    }

    private static async Task AssertReaderAcceptsAsync(string outputPath)
    {
        using var pinned = await TransferV3SnapshotReader.OpenAsync(
            outputPath,
            TransferV3SourceContract.LoadEmbedded());
        Assert.Equal(27, pinned.Manifest.Tables.Length);
    }

    private static TransferV3ManifestTable Table(
        TransferV3Manifest manifest,
        string name) => manifest.Tables.Single(value => value.Name == name);

    private static Guid CursorUuid(ArtifactRow row)
    {
        var component = Assert.Single(TransferV3CursorCodec.Decode(row.Cursor));
        Assert.Equal(TransferV3CursorComponentType.Uuid, component.Type);
        return component.UuidValue;
    }

    private static long DescriptorLength(ArtifactRow row)
    {
        var descriptor = Concatenate(row.Fields[0]);
        Assert.Equal(40, descriptor.Length);
        return BinaryPrimitives.ReadInt64BigEndian(descriptor);
    }

    private static string DecodeText(
        TransferV3TableContract table,
        int columnOrdinal,
        ArtifactRow row)
    {
        var decoded = DecodeField(table, columnOrdinal, row);
        return Encoding.UTF8.GetString(Assert.IsType<byte[]>(decoded.Value));
    }

    private static TransferV3DecodedField DecodeField(
        TransferV3TableContract table,
        int columnOrdinal,
        ArtifactRow row) => TransferV3RowCodec.DecodeField(
            table.Columns[columnOrdinal],
            Concatenate(row.Fields[columnOrdinal]));

    private static List<byte[]> OneChunk(byte[] value) => [value];

    private static List<byte[]> CloneChunks(IEnumerable<byte[]> chunks) =>
        chunks.Select(chunk => chunk.ToArray()).ToList();

    private static byte[] Concatenate(IEnumerable<byte[]> chunks)
    {
        using var output = new MemoryStream();
        foreach (var chunk in chunks) output.Write(chunk);
        return output.ToArray();
    }

    private static async Task<TransferV3Manifest> ReadManifestAsync(
        string root,
        TransferV3SourceContract contract) => TransferV3ManifestCodec.Parse(
        await File.ReadAllBytesAsync(Path.Combine(root, "manifest.json")),
        contract);

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

    private sealed class ArtifactFixture : IAsyncDisposable
    {
        private readonly TransferV3ValidationSource _source;

        private ArtifactFixture(TransferV3ValidationSource source, string outputPath)
        {
            _source = source;
            OutputPath = outputPath;
        }

        internal string OutputPath { get; }

        internal static async Task<ArtifactFixture> CreateAsync()
        {
            var source = await TransferV3ValidationSource.CreateAsync();
            try
            {
                await source.InsertValidQueueItemAsync(QueueId);
                await source.WriteBlobAsync(QueueId, "short-nzb"u8.ToArray());
                await source.InsertQueueContentsAsync(
                    QueueId,
                    "<nzb>" + new string('x', 192 * 1024) + "</nzb>");

                await source.InsertUsenetDavItemAsync(
                    DavItemId,
                    subType: 201,
                    fileBlobId: EmptyMetadataBlobId);
                await source.WriteBlobAsync(EmptyMetadataBlobId, []);

                var largeOrphan = new byte[1024 * 1024 + 17];
                for (var index = 0; index < largeOrphan.Length; index++)
                    largeOrphan[index] = (byte)(index % 251);
                await source.WriteBlobAsync(LargeOrphanBlobId, largeOrphan);

                await source.ExecuteAsync(
                    """
                    INSERT INTO HealthCheckResults(
                        Id, CreatedAt, DavItemId, Path, Result, RepairStatus, Message)
                    VALUES (
                        'ffffffff-ffff-ffff-ffff-ffffffffffff', 86401,
                        '99999999-9999-9999-9999-999999999999',
                        '/historical/missing', 1, 2, 'synthetic');
                    """);

                var output = Path.Combine(source.ValidationWorkspaceRoot, "artifact");
                await using (var session = await OpenSessionAsync(source))
                {
                    await new TransferV3SnapshotExporter().ExportAsync(
                        session,
                        output,
                        new TransferV3Limits(
                            1024 * 1024,
                            maxBatchRows: 2,
                            maxBatchBytes: 4 * 1024 * 1024));
                }
                return new ArtifactFixture(source, output);
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => _source.DisposeAsync();
    }

    private sealed class CanonicalArtifactRewriter
    {
        private readonly string _root;
        private readonly TransferV3SourceContract _contract =
            TransferV3SourceContract.LoadEmbedded();

        internal CanonicalArtifactRewriter(string root)
        {
            _root = root;
        }

        internal async Task RewriteTableAsync(
            string tableName,
            Action<TableRewriteContext, List<ArtifactRow>> mutate)
        {
            var manifest = await ReadManifestAsync(_root, _contract);
            var tableOrdinal = _contract.Tables
                .Select((table, ordinal) => (table, ordinal))
                .Single(value => value.table.Name == tableName)
                .ordinal;
            var table = _contract.Tables[tableOrdinal];
            var file = manifest.Tables[tableOrdinal].File;
            var rows = ParseRows(await File.ReadAllBytesAsync(Path.Combine(_root, file)));
            mutate(
                new TableRewriteContext(
                    table,
                    _contract.Bootstrap.Config.Select(value => value.Name).ToArray()),
                rows);
            rows.Sort((left, right) => TransferV3CursorCodec.Compare(left.Cursor, right.Cursor));

            var rewritten = await WriteRowsAsync(
                table.Name,
                rows,
                Limits(manifest));
            await File.WriteAllBytesAsync(Path.Combine(_root, file), rewritten.Bytes);
            var descriptor = new TransferV3ManifestTable(
                table.Name,
                file,
                rewritten.End.Batches,
                rewritten.End.Rows,
                rewritten.End.Bytes,
                rewritten.End.Sha256);
            var tables = manifest.Tables.SetItem(tableOrdinal, descriptor);
            await WriteManifestAsync(manifest with { Tables = tables });
        }

        internal async Task RewriteBlobsAsync(Action<List<ArtifactRow>> mutate)
        {
            var manifest = await ReadManifestAsync(_root, _contract);
            var file = manifest.Blobs.File;
            var rows = ParseRows(await File.ReadAllBytesAsync(Path.Combine(_root, file)));
            mutate(rows);
            rows.Sort((left, right) => TransferV3CursorCodec.Compare(left.Cursor, right.Cursor));

            var rewritten = await WriteRowsAsync("Blobs", rows, Limits(manifest));
            await File.WriteAllBytesAsync(Path.Combine(_root, file), rewritten.Bytes);
            using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;
            var network = new byte[16];
            foreach (var row in rows)
            {
                var id = CursorUuid(row);
                Assert.True(id.TryWriteBytes(network, bigEndian: true, out var written));
                Assert.Equal(network.Length, written);
                var descriptor = Concatenate(row.Fields[0]);
                Assert.Equal(40, descriptor.Length);
                var length = BinaryPrimitives.ReadInt64BigEndian(descriptor);
                Assert.True(length >= 0);
                totalBytes = checked(totalBytes + length);
                inventory.AppendData(network);
                inventory.AppendData(descriptor.AsSpan(0, sizeof(long)));
                inventory.AppendData(descriptor.AsSpan(sizeof(long), 32));
            }
            var blobs = new TransferV3ManifestBlobs(
                "Blobs",
                file,
                rewritten.End.Batches,
                rewritten.End.Rows,
                rewritten.End.Bytes,
                rewritten.End.Sha256,
                rows.Count,
                totalBytes,
                Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant());
            await WriteManifestAsync(manifest with { Blobs = blobs });
        }

        internal static List<ArtifactRow> ParseRows(byte[] bytes)
        {
            var rows = new List<ArtifactRow>();
            ArtifactRow? open = null;
            var start = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                var frame = TransferV3FrameCodec.ParseCanonical(
                    bytes.AsMemory(start, index - start));
                start = index + 1;
                try
                {
                    switch (frame)
                    {
                        case TransferV3ChunkedRowStartFrame rowStart:
                            if (open is not null) throw new InvalidDataException("nested-row");
                            open = new ArtifactRow(rowStart.Cursor, rowStart.Fields);
                            break;
                        case TransferV3FieldChunkFrame chunk:
                            if (open is null || chunk.Cursor != open.Cursor)
                                throw new InvalidDataException("orphan-chunk");
                            open.Fields[chunk.Field].Add(chunk.Data.ToArray());
                            break;
                        case TransferV3ChunkedRowEndFrame rowEnd:
                            if (open is null || rowEnd.Cursor != open.Cursor)
                                throw new InvalidDataException("orphan-row-end");
                            if (open.Fields.Any(field => field.Count == 0))
                                throw new InvalidDataException("missing-field");
                            rows.Add(open);
                            open = null;
                            break;
                        case TransferV3RowFrame:
                            throw new InvalidDataException("whole-row-not-supported");
                    }
                }
                finally
                {
                    TransferV3FrameCodec.ClearDecodedPayload(frame);
                }
            }
            if (start != bytes.Length || open is not null)
                throw new InvalidDataException("incomplete-jsonl");
            return rows;
        }

        private async Task WriteManifestAsync(TransferV3Manifest manifest)
        {
            var bytes = TransferV3ManifestCodec.Serialize(manifest, _contract);
            await File.WriteAllBytesAsync(Path.Combine(_root, "manifest.json"), bytes);
        }

        private static TransferV3Limits Limits(TransferV3Manifest manifest) => new(
            manifest.Limits.MaxFieldBytes,
            manifest.Limits.MaxBatchRows,
            manifest.Limits.MaxBatchBytes);

        private static async Task<RewrittenRows> WriteRowsAsync(
            string table,
            IReadOnlyList<ArtifactRow> rows,
            TransferV3Limits limits)
        {
            await using var output = new MemoryStream();
            await using var writer = new TransferV3JsonlWriter(output, table, limits);
            await writer.WriteTableHeaderAsync();
            string? after = null;
            for (var ordinal = 0; ordinal < rows.Count; ordinal++)
            {
                var row = rows[ordinal];
                await writer.StartBatchAsync(ordinal, after);
                await writer.StartChunkedRowAsync(row.Cursor, row.Fields.Count);
                for (var field = 0; field < row.Fields.Count; field++)
                {
                    foreach (var chunk in row.Fields[field])
                        await writer.WriteFieldChunkAsync(field, chunk);
                }
                await writer.EndChunkedRowAsync();
                await writer.EndBatchAsync();
                after = row.Cursor;
            }
            var end = await writer.EndTableAsync();
            return new RewrittenRows(output.ToArray(), end);
        }
    }

    private sealed class ArtifactRow
    {
        internal ArtifactRow(string cursor, int fieldCount)
        {
            Cursor = cursor;
            Fields = Enumerable.Range(0, fieldCount)
                .Select(_ => new List<byte[]>())
                .ToList();
        }

        internal string Cursor { get; set; }
        internal List<List<byte[]>> Fields { get; }
    }

    private sealed record TableRewriteContext(
        TransferV3TableContract Contract,
        IReadOnlyList<string> BootstrapConfigNames);

    private sealed record RewrittenRows(
        byte[] Bytes,
        TransferV3TableEndFrame End);
}

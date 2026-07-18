using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotExporterTests
{
    [Fact]
    public async Task ExportAsync_EmptySyntheticSourcePublishesExactCanonicalSnapshotLast()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "snapshot");
        var contract = TransferV3SourceContract.LoadEmbedded();
        var limits = new TransferV3Limits(
            1024 * 1024,
            maxBatchRows: 17,
            maxBatchBytes: 2 * 1024 * 1024);
        await using var session = await OpenSessionAsync(source);
        var directoryEvents = new List<TransferV3SnapshotDirectoryFaultPoint>();
        bool? manifestBufferCleared = null;
        var hooks = new TransferV3SnapshotExporterHooks(
            SnapshotDirectoryHooks: new TransferV3SnapshotDirectoryHooks(
                directoryEvents.Add),
            AfterManifestBufferCleared: cleared => manifestBufferCleared = cleared);

        var result = await new TransferV3SnapshotExporter(hooks)
            .ExportAsync(session, outputPath, limits);

        var expectedDataNames = ExpectedDataNames(contract);
        var actualNames = Directory.EnumerateFileSystemEntries(outputPath)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedDataNames.Append("manifest.json").Order(StringComparer.Ordinal),
            actualNames);
        Assert.Equal(29, actualNames.Length);

        var manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(outputPath, "manifest.json"));
        var manifest = TransferV3ManifestCodec.Parse(manifestBytes, contract);
        Assert.Equal(3, manifest.FormatVersion);
        Assert.Equal(contract.Provider, manifest.SourceProvider);
        Assert.Equal(contract.ComputeSha256(), manifest.SourceContractSha256);
        Assert.Equal(contract.SourceSchemaSha256, manifest.SourceSchemaSha256);
        Assert.Equal(
            contract.MigrationSourceContractSha256,
            manifest.MigrationContractSha256);
        Assert.Equal(TimeZoneInfo.Utc.Id, manifest.SourceTimeZoneId);
        Assert.Equal(limits.MaxFieldBytes, manifest.Limits.MaxFieldBytes);
        Assert.Equal(limits.MaxBatchRows, manifest.Limits.MaxBatchRows);
        Assert.Equal(limits.MaxBatchBytes, manifest.Limits.MaxBatchBytes);
        Assert.Equal(contract.Tables.Select(table => table.Name), manifest.Tables.Select(table => table.Name));
        Assert.Equal(expectedDataNames[..^1], manifest.Tables.Select(table => table.File));
        Assert.Equal(contract.DerivedTables.Select(table => table.Name), manifest.DerivedTables.Select(table => table.Name));
        Assert.Equal("Blobs.jsonl", manifest.Blobs.File);
        Assert.Equal(
            TransferV3ManifestCodec.ComputeSha256(manifestBytes),
            result.ManifestSha256);
        Assert.Equal(manifest.Tables.Sum(table => table.Rows), result.TableMetrics.TransferredRows);
        Assert.Equal(manifest.Blobs.Rows, result.BlobMetrics.Rows);
        Assert.Empty(result.CleanupCodes);
        Assert.True(manifestBufferCleared);

        var beforeManifest = directoryEvents.IndexOf(
            TransferV3SnapshotDirectoryFaultPoint.BeforeManifestTemporaryCreated);
        Assert.True(beforeManifest >= 0);
        Assert.Equal(
            28,
            directoryEvents.Take(beforeManifest).Count(point =>
                point == TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed));
        Assert.Equal(
            28,
            directoryEvents.Take(beforeManifest).Count(point =>
                point == TransferV3SnapshotDirectoryFaultPoint.AfterDataVerified));
        Assert.DoesNotContain(
            directoryEvents.Skip(beforeManifest),
            point => point is TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed
                or TransferV3SnapshotDirectoryFaultPoint.AfterDataVerified);

        using var root = TransferV3Posix.OpenDirectory(outputPath);
        foreach (var name in actualNames)
        {
            using var file = TransferV3Posix.OpenReadOnlyRegularFileAt(root, name!);
            var stat = TransferV3Posix.GetFileStat(file);
            Assert.Equal(0x8000u | TransferV3Posix.PrivateFileMode, stat.Mode);
            Assert.Equal(1UL, stat.LinkCount);
        }
    }

    [Fact]
    public async Task ExportAsync_RejectsBlobFieldLimitBeforeCreatingRootOrConsumingSession()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "invalid-limits");
        await using var session = await OpenSessionAsync(source);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new TransferV3SnapshotExporter().ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(39)));

        Assert.Equal(TransferV3SqliteExportSessionState.Ready, session.State);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task ExportAsync_RejectsNonReadySessionBeforeCreatingRoot()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "completed-session");
        await using var session = await OpenSessionAsync(source);
        await session.RunExportAsync((_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TransferV3SnapshotExporter().ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(1024)));

        Assert.Equal(TransferV3SqliteExportSessionState.Completed, session.State);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task ExportAsync_PostCallbackSessionFailureNeverPublishesManifest()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "failed-snapshot");
        var injected = new IOException("stable-session-finalization-primary");
        var options = source.Options() with
        {
            SessionHooks = new TransferV3SqliteExportSessionHooks(_ => throw injected),
        };
        await using var session = await OpenSessionAsync(source, options);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            new TransferV3SnapshotExporter().ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(1024 * 1024)));

        Assert.Contains(
            Flatten(failure),
            exception => ReferenceEquals(exception, injected));
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
        Assert.DoesNotContain(source.DatabasePath, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(source.BlobRootPath, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_NonEmptyTablesAndMultiChunkBlobMatchIndependentTotals()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var contents = Enumerable.Range(0, 1024 * 1024 + 17)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await source.InsertValidQueueItemAsync(id);
        await source.WriteBlobAsync(id, contents);
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "non-empty-snapshot");
        var limits = new TransferV3Limits(
            512 * 1024,
            maxBatchRows: 2,
            maxBatchBytes: 2 * 1024 * 1024);
        await using var session = await OpenSessionAsync(source);

        var result = await new TransferV3SnapshotExporter()
            .ExportAsync(session, outputPath, limits);

        var manifestBytes = await File.ReadAllBytesAsync(
            Path.Combine(outputPath, "manifest.json"));
        var manifest = TransferV3ManifestCodec.Parse(
            manifestBytes,
            TransferV3SourceContract.LoadEmbedded());
        Assert.Equal(1, manifest.Blobs.Count);
        Assert.Equal(contents.Length, manifest.Blobs.TotalBytes);
        Assert.Equal(40L + contents.Length, manifest.Blobs.DecodedBytes);
        Assert.Equal(manifest.Blobs.Count, result.BlobMetrics.Rows);
        Assert.Equal(contents.Length, result.BlobMetrics.ContentBytes);
        Assert.True(result.BlobMetrics.SourceReadOperations >= 2);
        Assert.True(manifest.Tables.Sum(table => table.Rows) >= 2);
        Assert.Equal(
            manifest.Tables.Sum(table => table.Rows),
            result.TableMetrics.TransferredRows);

        Span<byte> networkId = stackalloc byte[16];
        Assert.True(Guid.Parse(id).TryWriteBytes(networkId, bigEndian: true, out var written));
        Assert.Equal(16, written);
        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        inventory.AppendData(networkId);
        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(lengthBytes, contents.Length);
        inventory.AppendData(lengthBytes);
        inventory.AppendData(SHA256.HashData(contents));
        Assert.Equal(
            Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant(),
            manifest.Blobs.InventorySha256);

        var blobFrames = ParseFrames(
            await File.ReadAllBytesAsync(Path.Combine(outputPath, "Blobs.jsonl")));
        var rowStart = Assert.Single(blobFrames.OfType<TransferV3ChunkedRowStartFrame>());
        Assert.Equal(4, rowStart.Fields);
        var tableEnd = Assert.Single(blobFrames.OfType<TransferV3TableEndFrame>());
        Assert.Equal(manifest.Blobs.Rows, tableEnd.Rows);
        Assert.Equal(manifest.Blobs.DecodedBytes, tableEnd.Bytes);
        Assert.Equal(manifest.Blobs.Sha256, tableEnd.Sha256);
    }

    [Fact]
    public async Task ExportAsync_OutputMutationAfterSessionFinalizationFailsPublicationClosed()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "mutated-snapshot");
        var mutated = false;
        var hooks = new TransferV3SnapshotExporterHooks(point =>
        {
            if (point != TransferV3SnapshotExporterFaultPoint.AfterSessionFinalized)
                return;
            var target = Directory.EnumerateFiles(outputPath, "table-*.jsonl")
                .Order(StringComparer.Ordinal)
                .First();
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Position = Math.Min(1, stream.Length);
            stream.WriteByte(0xff);
            stream.Flush(flushToDisk: true);
            mutated = true;
        });
        await using var session = await OpenSessionAsync(source);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new TransferV3SnapshotExporter(hooks).ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(1024 * 1024)));

        Assert.True(mutated);
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
    }

    [Theory]
    [InlineData("after-table")]
    [InlineData("after-blob")]
    [InlineData("after-session")]
    [InlineData("before-publication")]
    [InlineData("after-data-close")]
    [InlineData("after-data-verification")]
    [InlineData("before-manifest-temp")]
    [InlineData("after-manifest-temp")]
    [InlineData("before-manifest-rename")]
    [InlineData("after-manifest-published")]
    public async Task ExportAsync_PreCompletionBoundaryFailureNeverLeavesManifest(
        string stage)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(
            source.ValidationWorkspaceRoot,
            $"boundary-{stage}");
        var injected = new IOException("private-boundary-secret");
        bool? manifestBufferCleared = null;
        var directoryHooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            var shouldFail = stage switch
            {
                "after-data-close" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed,
                "after-data-verification" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.AfterDataVerified,
                "before-manifest-temp" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.BeforeManifestTemporaryCreated,
                "after-manifest-temp" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.AfterManifestTemporaryCreated,
                "before-manifest-rename" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.BeforeManifestRename,
                "after-manifest-published" =>
                    point == TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished,
                _ => false,
            };
            if (shouldFail) throw injected;
        });
        var hooks = new TransferV3SnapshotExporterHooks(
            point =>
            {
                var shouldFail = stage switch
                {
                    "after-table" =>
                        point == TransferV3SnapshotExporterFaultPoint.AfterTableExport,
                    "after-blob" =>
                        point == TransferV3SnapshotExporterFaultPoint.AfterBlobExport,
                    "after-session" =>
                        point == TransferV3SnapshotExporterFaultPoint.AfterSessionFinalized,
                    "before-publication" =>
                        point == TransferV3SnapshotExporterFaultPoint.BeforeManifestPublication,
                    _ => false,
                };
                if (shouldFail) throw injected;
            },
            SnapshotDirectoryHooks: directoryHooks,
            AfterManifestBufferCleared: cleared => manifestBufferCleared = cleared);
        await using var session = await OpenSessionAsync(source);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            new TransferV3SnapshotExporter(hooks).ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(1024 * 1024)));

        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
        Assert.DoesNotContain(source.DatabasePath, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(source.BlobRootPath, failure.ToString(), StringComparison.Ordinal);
        Assert.All(
            CleanupCodes(failure),
            code => Assert.DoesNotContain(
                "private-boundary-secret",
                code,
                StringComparison.Ordinal));
        if (stage is "after-session"
            or "before-publication"
            or "before-manifest-temp"
            or "after-manifest-temp"
            or "before-manifest-rename"
            or "after-manifest-published")
        {
            Assert.True(manifestBufferCleared);
        }
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("post-session")]
    public async Task ExportAsync_CancellationAtReceiptOrPublicationBoundaryPreservesToken(
        string stage)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(
            source.ValidationWorkspaceRoot,
            $"cancel-{stage}");
        using var cancellation = new CancellationTokenSource();
        bool? manifestBufferCleared = null;
        var hooks = new TransferV3SnapshotExporterHooks(
            point =>
            {
                if (stage == "post-session"
                    && point == TransferV3SnapshotExporterFaultPoint.AfterSessionFinalized)
                {
                    cancellation.Cancel();
                }
            },
            SnapshotDirectoryHooks: new TransferV3SnapshotDirectoryHooks(point =>
            {
                if (stage == "receipt"
                    && point == TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed)
                {
                    cancellation.Cancel();
                }
            }),
            AfterManifestBufferCleared: cleared => manifestBufferCleared = cleared);
        await using var session = await OpenSessionAsync(source);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TransferV3SnapshotExporter(hooks).ExportAsync(
                session,
                outputPath,
                new TransferV3Limits(1024 * 1024),
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
        if (stage == "post-session") Assert.True(manifestBufferCleared);
    }

    [Fact]
    public async Task ExportAsync_PostPublicationCloseFailureReturnsSanitizedTelemetryAndArtifact()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var outputPath = Path.Combine(source.ValidationWorkspaceRoot, "published-close-failure");
        bool? manifestBufferCleared = null;
        var hooks = new TransferV3SnapshotExporterHooks(
            AfterManifestBufferCleared: cleared => manifestBufferCleared = cleared,
            BeforePublishedSnapshotClose: () =>
                throw new IOException("private-published-close-secret"));
        await using var session = await OpenSessionAsync(source);

        var result = await new TransferV3SnapshotExporter(hooks).ExportAsync(
            session,
            outputPath,
            new TransferV3Limits(1024 * 1024));

        Assert.True(File.Exists(Path.Combine(outputPath, "manifest.json")));
        Assert.Equal(29, Directory.EnumerateFiles(outputPath).Count());
        Assert.Equal(["snapshot-cleanup-failed"], result.CleanupCodes.ToArray());
        Assert.DoesNotContain(
            "private-published-close-secret",
            string.Join(',', result.CleanupCodes),
            StringComparison.Ordinal);
        Assert.True(manifestBufferCleared);
    }

    [Fact]
    public async Task ExportAsync_RepeatedSyntheticSourceProducesByteIdenticalSnapshot()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var firstPath = Path.Combine(source.ValidationWorkspaceRoot, "first");
        var secondPath = Path.Combine(source.ValidationWorkspaceRoot, "second");
        var limits = new TransferV3Limits(1024 * 1024, 23, 3 * 1024 * 1024);

        await using (var firstSession = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                firstSession,
                firstPath,
                limits);
        }
        await using (var secondSession = await OpenSessionAsync(source))
        {
            await new TransferV3SnapshotExporter().ExportAsync(
                secondSession,
                secondPath,
                limits);
        }

        var firstNames = Directory.EnumerateFiles(firstPath)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var secondNames = Directory.EnumerateFiles(secondPath)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(firstNames, secondNames);
        foreach (var name in firstNames)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(firstPath, name!)),
                await File.ReadAllBytesAsync(Path.Combine(secondPath, name!)));
        }
    }

    [Fact]
    public void SnapshotExporterSource_HasNoPostgreSqlRuntimeOrImportSurface()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SnapshotExporter.cs"));

        Assert.DoesNotContain("Postgre", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DatabaseTransferService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("target", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("import", source, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TransferV3SqliteExportSession> OpenSessionAsync(
        TransferV3ValidationSource source,
        TransferV3SqliteValidationOptions? options = null)
    {
        var provenance = CaptureProvenance(source);
        return await new TransferV3SqlitePreflightValidator()
            .OpenValidatedExportSessionAsync(
                source.DatabasePath,
                options ?? source.Options(),
                provenance);
    }

    private static TransferV3SourceProvenance CaptureProvenance(
        TransferV3ValidationSource source)
    {
        using var database = TransferV3SqliteSourceGuard.Open(source.DatabasePath);
        using var blobs = TransferV3BlobSourceGuard.Open(source.BlobRootPath);
        return new TransferV3SourceProvenance(
            database.Identity,
            blobs.Identity,
            TimeZoneInfo.Utc.Id);
    }

    private static ImmutableArray<string> ExpectedDataNames(
        TransferV3SourceContract contract)
    {
        var names = ImmutableArray.CreateBuilder<string>(contract.Tables.Count + 1);
        for (var index = 0; index < contract.Tables.Count; index++)
        {
            names.Add($"table-{index + 1:000}-{contract.Tables[index].Name}.jsonl");
        }
        names.Add("Blobs.jsonl");
        return names.MoveToImmutable();
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                yield return inner;
        }
        else if (exception.InnerException is not null)
        {
            foreach (var inner in Flatten(exception.InnerException))
                yield return inner;
        }
    }

    private static IEnumerable<string> CleanupCodes(Exception exception)
    {
        foreach (var value in Flatten(exception))
        {
            if (value.Data["TransferV3CleanupCodes"] is not IEnumerable<string> codes)
                continue;
            foreach (var code in codes) yield return code;
        }
    }

    private static IReadOnlyList<TransferV3Frame> ParseFrames(byte[] bytes)
    {
        var frames = new List<TransferV3Frame>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            frames.Add(TransferV3FrameCodec.ParseCanonical(
                bytes.AsMemory(start, index - start)));
            start = index + 1;
        }
        Assert.Equal(bytes.Length, start);
        return frames;
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
}

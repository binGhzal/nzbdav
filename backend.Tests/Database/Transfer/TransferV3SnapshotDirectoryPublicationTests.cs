using System.Reflection;
using System.Runtime.InteropServices;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SnapshotDirectoryPublicationTests
{
    [Fact]
    public async Task VerifiedOutputFactory_FreezesExpectedNamesAndPublishesOnlyAfterReceipt()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath);
        var expected = new[] { "table-001-DavItems.jsonl" };
        var outputs = snapshot.CreateDataOutputFactory(expected);
        expected[0] = "mutated.jsonl";

        await WriteCompletedAsync(outputs, "table-001-DavItems.jsonl", "row"u8.ToArray());
        await snapshot.PublishManifestAsync("{\"version\":3}"u8.ToArray());

        Assert.Equal("row", await File.ReadAllTextAsync(
            Path.Combine(outputPath, "table-001-DavItems.jsonl")));
        Assert.Equal("{\"version\":3}", await File.ReadAllTextAsync(
            Path.Combine(outputPath, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(outputPath, "mutated.jsonl")));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    public void VerifiedOutputFactory_RejectsDuplicateAndInvalidExpectedNames(string scenario)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        IReadOnlyList<string> names = scenario == "duplicate"
            ? ["one.jsonl", "one.jsonl"]
            : ["../escape.jsonl"];

        Assert.Throws<ArgumentException>(() => snapshot.CreateDataOutputFactory(names));
    }

    [Fact]
    public async Task Publication_RejectsDisposeOnlyOutputWithoutPrivateReceipt()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        var output = await outputs.CreateAsync("data.jsonl", CancellationToken.None);
        await output.Stream.WriteAsync("partial"u8.ToArray());
        await output.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));

        snapshot.Dispose();
    }

    [Fact]
    public void RawFileReceipt_IsPrivateAndHasNoDefaultValueFormattingSurface()
    {
        var receipt = typeof(TransferV3SnapshotDirectory)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name == "DataFileReceipt");

        Assert.True(receipt.IsNestedPrivate);
        Assert.DoesNotContain(
            receipt.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(
            receipt.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.DeclaringType == receipt && method.Name == nameof(ToString));
    }

    [Fact]
    public async Task CompleteDurablyAsync_RejectsMutationAfterHashBeforeReceiptStorage()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var dataPath = Path.Combine(outputPath, "data.jsonl");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == TransferV3SnapshotDirectoryFaultPoint.AfterDataVerified)
            {
                File.AppendAllText(dataPath, "mutation");
            }
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        var output = await outputs.CreateAsync("data.jsonl", CancellationToken.None);
        await output.Stream.WriteAsync("stable"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() =>
            output.CompleteDurablyAsync(CancellationToken.None).AsTask());
        await output.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        snapshot.Dispose();
    }

    [Theory]
    [InlineData("content")]
    [InlineData("replacement")]
    [InlineData("chmod")]
    [InlineData("hardlink")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("root-chmod")]
    public async Task PublicationGate_RejectsPostReceiptTampering(string scenario)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var dataPath = Path.Combine(outputPath, "data.jsonl");
        var externalAlias = Path.Combine(parent.Path, "external-alias");
        var extraPath = Path.Combine(outputPath, "extra.tmp");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "stable-content"u8.ToArray());

        switch (scenario)
        {
            case "content":
                await File.AppendAllTextAsync(dataPath, "mutation");
                break;
            case "replacement":
                File.Delete(dataPath);
                await File.WriteAllTextAsync(dataPath, "stable-content");
                break;
            case "chmod":
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(dataPath, UnixFileMode.UserRead);
                }
                break;
            case "hardlink":
                CreateHardLink(dataPath, externalAlias);
                break;
            case "extra":
                await File.WriteAllTextAsync(extraPath, "unexpected");
                break;
            case "missing":
                File.Delete(dataPath);
                break;
            case "root-chmod":
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(
                        outputPath,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute);
                }
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        await Assert.ThrowsAsync<IOException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));

        if (File.Exists(externalAlias)) File.Delete(externalAlias);
        if (File.Exists(extraPath)) File.Delete(extraPath);
        if (scenario == "replacement" && File.Exists(dataPath)) File.Delete(dataPath);
        snapshot.Dispose();
    }

    [Fact]
    public async Task OutputFactory_RejectsUnexpectedAndSecondCreation()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outputs.CreateAsync("unexpected.jsonl", CancellationToken.None).AsTask());
        var output = await outputs.CreateAsync("data.jsonl", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outputs.CreateAsync("data.jsonl", CancellationToken.None).AsTask());
        await output.DisposeAsync();
        snapshot.Dispose();
    }

    [Fact]
    public async Task Dispose_ZeroesPrivateRawReceiptDigest()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "receipt-evidence"u8.ToArray());

        var ownedFiles = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            GetPrivateField(snapshot, "_ownedFiles"));
        var owned = ownedFiles.Cast<object>().Single();
        var receipt = GetProperty(owned, "Receipt");
        Assert.NotNull(receipt);
        var digest = Assert.IsType<byte[]>(GetProperty(receipt!, "RawSha256"));
        Assert.Contains(digest, value => value != 0);

        snapshot.Dispose();

        Assert.All(digest, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CancellationBeforeRename_RemovesTemporaryAndLeavesPublicationRetryable()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        using var cancellation = new CancellationTokenSource();
        var cancelOnce = true;
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == TransferV3SnapshotDirectoryFaultPoint.BeforeManifestRename
                && cancelOnce)
            {
                cancelOnce = false;
                cancellation.Cancel();
            }
        });
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            snapshot.PublishManifestAsync("first"u8.ToArray(), cancellation.Token).AsTask());
        Assert.Equal(
            ["data.jsonl"],
            Directory.EnumerateFileSystemEntries(outputPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));

        await snapshot.PublishManifestAsync("second"u8.ToArray());
        Assert.Equal("second", await File.ReadAllTextAsync(
            Path.Combine(outputPath, "manifest.json")));
    }

    [Fact]
    public async Task CancellationAfterRename_DoesNotInterruptFinalDirectoryDurability()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        using var cancellation = new CancellationTokenSource();
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished)
            {
                cancellation.Cancel();
            }
        });
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

        await snapshot.PublishManifestAsync("manifest"u8.ToArray(), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("manifest", await File.ReadAllTextAsync(
            Path.Combine(outputPath, "manifest.json")));
    }

    [Fact]
    public async Task CompleteDurablyAsync_RefusesAPreviouslyRecordedDurableCloseFailure()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        var output = await outputs.CreateAsync("data.jsonl", CancellationToken.None);
        await output.Stream.WriteAsync("stable"u8.ToArray());
        var durable = Assert.IsType<TransferV3DurableFileStream>(output.Stream);
        var closeState = Assert.IsType<TransferV3DurableCloseState>(
            GetPrivateField(durable, "_closeState"));
        var injected = new IOException("stable-durable-close-primary");
        var first = Record.Exception(() => closeState.ExecuteClose(() => throw injected));
        Assert.Same(injected, first);

        var caught = await Assert.ThrowsAsync<IOException>(() =>
            output.CompleteDurablyAsync(CancellationToken.None).AsTask());
        Assert.Same(injected, caught);
        await output.DisposeAsync();
        snapshot.Dispose();
    }

    [Fact]
    public async Task Publication_RejectsIncompleteExpectedOutputSet()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        var outputs = snapshot.CreateDataOutputFactory(["one.jsonl", "two.jsonl"]);
        await WriteCompletedAsync(outputs, "one.jsonl", "one"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());

        snapshot.Dispose();
    }

    [Fact]
    public async Task DurableCloseBoundaryFault_PreservesExactPrimaryAndStoresNoReceipt()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var injected = new IOException("stable-data-close-primary");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == TransferV3SnapshotDirectoryFaultPoint.AfterDataDurablyClosed)
            {
                throw injected;
            }
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"),
            hooks);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        var output = await outputs.CreateAsync("data.jsonl", CancellationToken.None);
        await output.Stream.WriteAsync("stable"u8.ToArray());

        var caught = await Assert.ThrowsAsync<IOException>(() =>
            output.CompleteDurablyAsync(CancellationToken.None).AsTask());
        Assert.Same(injected, caught);
        await output.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        snapshot.Dispose();
    }

    [Fact]
    public async Task Publication_RehashesAndRejectsAReceiptDigestMismatch()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());
        var ownedFiles = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            GetPrivateField(snapshot, "_ownedFiles"));
        var receipt = GetProperty(ownedFiles.Cast<object>().Single(), "Receipt");
        Assert.NotNull(receipt);
        var digest = Assert.IsType<byte[]>(GetProperty(receipt!, "RawSha256"));
        digest[0] ^= 0xff;

        await Assert.ThrowsAsync<IOException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        snapshot.Dispose();
    }

    [Fact]
    public async Task Publication_RejectsInvalidUtf8DirectoryEntryBeforeManifestTempCreation()
    {
        // APFS/libc rejects invalid byte names with EILSEQ before readdir can
        // observe them. Linux permits the fixture and exercises the fail-closed
        // decoder/publication path.
        if (!OperatingSystem.IsLinux() || !TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath);
        var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
        await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());
        using var root = TransferV3Posix.OpenDirectory(outputPath);
        CreateInvalidUtf8Entry(root);

        await Assert.ThrowsAsync<IOException>(() =>
            snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
        Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));

        RemoveInvalidUtf8Entry(root);
        snapshot.Dispose();
    }

    [Fact]
    public void ReceiptHashing_IsBoundedAndAvoidsWholeFileHelpers()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SnapshotDirectory.cs"));

        Assert.Contains("new byte[1024 * 1024]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEnd", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_AllowsOnlyOneFrozenOutputFactory()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        using var snapshot = TransferV3SnapshotDirectory.CreateNew(
            Path.Combine(parent.Path, "snapshot"));
        _ = snapshot.CreateDataOutputFactory([]);

        Assert.Throws<InvalidOperationException>(() =>
            snapshot.CreateDataOutputFactory([]));
    }

    [Fact]
    public async Task ManifestTemporaryReplacement_IsNeverPublishedByNameAlone()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterManifestTemporaryCreated)
            {
                return;
            }
            var temporary = Directory.EnumerateFiles(outputPath).Single(path =>
                Path.GetFileName(path).StartsWith(".manifest.json.", StringComparison.Ordinal));
            File.Delete(temporary);
            File.WriteAllText(temporary, "attacker-manifest");
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
            await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("trusted-manifest"u8.ToArray()).AsTask());
            Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        }
        finally
        {
            foreach (var temporary in Directory.EnumerateFiles(outputPath)
                         .Where(path => Path.GetFileName(path).StartsWith(
                             ".manifest.json.",
                             StringComparison.Ordinal)))
            {
                File.Delete(temporary);
            }
            snapshot.Dispose();
        }
    }

    [Theory]
    [InlineData("before-temp")]
    [InlineData("before-rename")]
    public async Task MutationReturningFromPreRenameHook_IsRechecked(string boundary)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var dataPath = Path.Combine(outputPath, "data.jsonl");
        var mutationPoint = boundary == "before-temp"
            ? TransferV3SnapshotDirectoryFaultPoint.BeforeManifestTemporaryCreated
            : TransferV3SnapshotDirectoryFaultPoint.BeforeManifestRename;
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == mutationPoint) File.AppendAllText(dataPath, "mutation");
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
            await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
            Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("root")]
    public async Task MutationReturningFromPostPublishHook_IsRecheckedBeforeFinalization(
        string scenario)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var movedPath = Path.Combine(parent.Path, "moved-snapshot");
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished) return;
            if (scenario == "manifest")
            {
                File.WriteAllText(Path.Combine(outputPath, "manifest.json"), "attacker-manifest");
            }
            else
            {
                Directory.Move(outputPath, movedPath);
                Directory.CreateDirectory(outputPath);
            }
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
            await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("trusted-manifest"u8.ToArray()).AsTask());
        }
        finally
        {
            try
            {
                snapshot.Dispose();
            }
            catch (IOException)
            {
                // Unknown replacements are intentionally preserved; the test
                // fixture root is removed after the owned descriptors close.
            }
        }
    }

    [Fact]
    public async Task PublicationGate_RejectsPathSwapDuringDescriptorHash()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var dataPath = Path.Combine(outputPath, "data.jsonl");
        var armed = false;
        var swapped = false;
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterFileDescriptorHashed
                || !armed
                || swapped)
            {
                return;
            }
            File.Delete(dataPath);
            File.WriteAllText(dataPath, "stable");
            swapped = true;
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
            await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());
            armed = true;

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
            Assert.True(swapped);
            Assert.False(File.Exists(Path.Combine(outputPath, "manifest.json")));
        }
        finally
        {
            if (File.Exists(dataPath)) File.Delete(dataPath);
            snapshot.Dispose();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FinalMultiFileGate_DoesNotLetLaterOrManifestHashHookMutateEarlierFile(
        int mutationHash)
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var outputPath = Path.Combine(parent.Path, "snapshot");
        var firstPath = Path.Combine(outputPath, "a.jsonl");
        var armed = false;
        var hashesAfterPublication = 0;
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point == TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished)
            {
                armed = true;
                return;
            }
            if (point == TransferV3SnapshotDirectoryFaultPoint.AfterFileDescriptorHashed
                && armed
                && ++hashesAfterPublication == mutationHash)
            {
                File.AppendAllText(firstPath, "later-file-mutation");
            }
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["a.jsonl", "b.jsonl"]);
            await WriteCompletedAsync(outputs, "a.jsonl", "first"u8.ToArray());
            await WriteCompletedAsync(outputs, "b.jsonl", "second"u8.ToArray());

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
            Assert.Equal(3, hashesAfterPublication);
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    [Fact]
    public async Task VisibleRootCheck_RejectsRenamedAndRecreatedParentPath()
    {
        if (!TransferV3Posix.IsSupported) return;

        using var parent = new TemporaryDirectory();
        var visibleParent = Path.Combine(parent.Path, "visible-parent");
        var movedParent = Path.Combine(parent.Path, "moved-parent");
        var outputPath = Path.Combine(visibleParent, "snapshot");
        var sentinelPath = Path.Combine(outputPath, "replacement-sentinel");
        Directory.CreateDirectory(visibleParent);
        var hooks = new TransferV3SnapshotDirectoryHooks(point =>
        {
            if (point != TransferV3SnapshotDirectoryFaultPoint.AfterManifestPublished) return;
            Directory.Move(visibleParent, movedParent);
            Directory.CreateDirectory(outputPath);
            File.WriteAllText(sentinelPath, "replacement-must-survive");
        });
        var snapshot = TransferV3SnapshotDirectory.CreateNew(outputPath, hooks);
        try
        {
            var outputs = snapshot.CreateDataOutputFactory(["data.jsonl"]);
            await WriteCompletedAsync(outputs, "data.jsonl", "stable"u8.ToArray());

            await Assert.ThrowsAsync<IOException>(() =>
                snapshot.PublishManifestAsync("manifest"u8.ToArray()).AsTask());
            Assert.Equal("replacement-must-survive", File.ReadAllText(sentinelPath));
        }
        finally
        {
            snapshot.Dispose();
        }
        Assert.Null(snapshot.CleanupResiduePath);
    }

    private static object? GetPrivateField(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    private static object? GetProperty(object instance, string name) =>
        instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    private static void CreateHardLink(string existingPath, string linkPath)
    {
        if (CreateHardLinkNative(existingPath, linkPath) != 0)
        {
            throw new IOException(
                $"Could not create the synthetic hard-link fixture (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkNative(string existingPath, string linkPath);

    private static void CreateInvalidUtf8Entry(Microsoft.Win32.SafeHandles.SafeFileHandle root)
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
                throw new IOException(
                    $"Could not create the invalid-UTF8 fixture (errno {Marshal.GetLastPInvokeError()}).");
            }
            _ = CloseNative(descriptor);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void RemoveInvalidUtf8Entry(Microsoft.Win32.SafeHandles.SafeFileHandle root)
    {
        var name = Marshal.AllocHGlobal(2);
        try
        {
            Marshal.WriteByte(name, 0, 0xff);
            Marshal.WriteByte(name, 1, 0);
            if (UnlinkAtRaw(TransferV3Posix.Descriptor(root), name, 0) != 0)
            {
                throw new IOException(
                    $"Could not remove the invalid-UTF8 fixture (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
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

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtRaw(int directory, IntPtr path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAtRaw(int directory, IntPtr path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNative(int descriptor);

    private static async Task WriteCompletedAsync(
        ITransferV3TableOutputFactory outputs,
        string fileName,
        ReadOnlyMemory<byte> bytes)
    {
        var output = await outputs.CreateAsync(fileName, CancellationToken.None);
        await output.Stream.WriteAsync(bytes);
        await output.CompleteDurablyAsync(CancellationToken.None);
        await output.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                $".nzbdav-transfer-v3-publication-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

using System.Reflection;
using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class BlobStoreTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public BlobStoreTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadBlob_ReturnsNullForCorruptSerializedBlobInsteadOfThrowing()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        try
        {
            var blobPath = GetBlobPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            await File.WriteAllTextAsync(blobPath, "truncated-zstd-payload");

            var blob = await BlobStore.ReadBlob<DavNzbFile>(id);

            Assert.Null(blob);
            Assert.True(File.Exists(blobPath));
        }
        finally
        {
            BlobStore.Delete(id);
        }
    }

    [Fact]
    public async Task ReadBlob_ReturnsNullWhenBlobPathIsDirectory()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(GetBlobPath(id));

            var rawBlob = BlobStore.ReadBlob(id);
            var serializedBlob = await BlobStore.ReadBlob<DavNzbFile>(id);

            Assert.Null(rawBlob);
            Assert.Null(serializedBlob);
        }
        finally
        {
            BlobStore.Delete(id);
        }
    }

    [Fact]
    public async Task ReadBlob_ReturnsNullWhenBlobOpenTemporarilyFails()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        var blobPath = GetBlobPath(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            await File.WriteAllTextAsync(blobPath, "locked");
            await using var locked = new FileStream(
                blobPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            Assert.Null(BlobStore.ReadBlob(id));
        }
        finally
        {
            BlobStore.Delete(id);
        }
    }

    [Fact]
    public async Task Delete_RemovesEmptyDirectoryAtBlobPath()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        var blobPath = GetBlobPath(id);
        Directory.CreateDirectory(blobPath);

        BlobStore.Delete(id);

        Assert.False(Directory.Exists(blobPath));
    }

    [Fact]
    public async Task Delete_RemovesNonEmptyDirectoryAtBlobPath()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        var blobPath = GetBlobPath(id);
        Directory.CreateDirectory(blobPath);
        await File.WriteAllTextAsync(Path.Combine(blobPath, "leftover.tmp"), "stale blob data");

        BlobStore.Delete(id);

        Assert.False(Directory.Exists(blobPath));
    }

    [Fact]
    public async Task WriteBlob_ReplacesExistingSerializedBlobAtomically()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        try
        {
            await BlobStore.WriteBlob(id, new DavNzbFile { Id = id, SegmentIds = ["old"] });

            await BlobStore.WriteBlob(id, new DavNzbFile { Id = id, SegmentIds = ["new"] });

            var blob = await BlobStore.ReadBlob<DavNzbFile>(id);
            Assert.NotNull(blob);
            Assert.Equal(["new"], blob.SegmentIds);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(GetBlobPath(id))!,
                "*.tmp-*"));
        }
        finally
        {
            BlobStore.Delete(id);
        }
    }

    [Fact]
    public async Task WriteBlob_ReplacesDirectoryAtBlobPath()
    {
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        var blobPath = GetBlobPath(id);
        Directory.CreateDirectory(blobPath);
        await File.WriteAllTextAsync(Path.Combine(blobPath, "stale.tmp"), "stale");

        try
        {
            await BlobStore.WriteBlob(id, new DavNzbFile { Id = id, SegmentIds = ["recovered"] });

            Assert.False(Directory.Exists(blobPath));
            var blob = await BlobStore.ReadBlob<DavNzbFile>(id);
            Assert.NotNull(blob);
            Assert.Equal(["recovered"], blob.SegmentIds);
        }
        finally
        {
            BlobStore.Delete(id);
        }
    }

    private static string GetBlobPath(Guid id)
    {
        var method = typeof(BlobStore).GetMethod(
            "GetBlobPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, [id])!;
    }
}

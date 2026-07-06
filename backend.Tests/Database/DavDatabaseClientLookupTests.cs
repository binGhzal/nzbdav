using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using System.Reflection;

namespace backend.Tests.Database;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DavDatabaseClientLookupTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DavDatabaseClientLookupTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetFileById_ReturnsNullForMalformedIds()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);

        var item = await dbClient.GetFileById("not-a-guid");

        Assert.Null(item);
    }

    [Fact]
    public async Task GetHistoryItemAsync_ReturnsNullForMalformedIds()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var dbClient = new DavDatabaseClient(dbContext);

        var item = await dbClient.GetHistoryItemAsync("not-a-guid");

        Assert.Null(item);
    }

    [Fact]
    public async Task MetadataLookupMethodsDoNotTrackDatabaseRows()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var nzbItem = CreateItem("Movie.mkv", DavItem.ItemSubType.NzbFile);
        var rarItem = CreateItem("Archive.mkv", DavItem.ItemSubType.RarFile);
        var multipartItem = CreateItem("Multipart.mkv", DavItem.ItemSubType.MultipartFile);
        dbContext.Items.AddRange(nzbItem, rarItem, multipartItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = nzbItem.Id,
            SegmentIds = ["nzb-segment"]
        });
        dbContext.RarFiles.Add(new DavRarFile
        {
            Id = rarItem.Id,
            RarParts =
            [
                new DavRarFile.RarPart
                {
                    SegmentIds = ["rar-segment"],
                    PartSize = 1,
                    Offset = 0,
                    ByteCount = 1
                }
            ]
        });
        dbContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = multipartItem.Id,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["multipart-segment"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 1),
                        FilePartByteRange = LongRange.FromStartAndSize(0, 1)
                    }
                ]
            }
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var dbClient = new DavDatabaseClient(dbContext);

        Assert.NotNull(await dbClient.GetDavNzbFileAsync(nzbItem));
        Assert.NotNull(await dbClient.GetDavRarFileAsync(rarItem));
        Assert.NotNull(await dbClient.GetDavMultipartFileAsync(multipartItem));

        Assert.Empty(dbContext.ChangeTracker.Entries<DavNzbFile>());
        Assert.Empty(dbContext.ChangeTracker.Entries<DavRarFile>());
        Assert.Empty(dbContext.ChangeTracker.Entries<DavMultipartFile>());
    }

    [Fact]
    public async Task GetDavNzbFileAsync_ThrowsRetryableWhenBlobMetadataIsTemporarilyUnreadable()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var blobId = Guid.NewGuid();
        var nzbItem = CreateItem("Movie.mkv", DavItem.ItemSubType.NzbFile);
        nzbItem.FileBlobId = blobId;
        dbContext.Items.Add(nzbItem);
        await dbContext.SaveChangesAsync();
        await BlobStore.WriteBlob(blobId, new DavNzbFile
        {
            Id = nzbItem.Id,
            SegmentIds = ["segment-1"]
        });
        await using var lockedBlob = new FileStream(
            GetBlobPath(blobId),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var dbClient = new DavDatabaseClient(dbContext);

        var exception = await Assert.ThrowsAsync<RetryableDownloadException>(() =>
            dbClient.GetDavNzbFileAsync(nzbItem));

        Assert.Contains("metadata blob", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DavItem CreateItem(string name, DavItem.ItemSubType subType)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = name,
            FileSize = 1,
            Type = DavItem.ItemType.UsenetFile,
            SubType = subType,
            Path = $"/content/{name}"
        };
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

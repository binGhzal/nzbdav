using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.Queue.FileAggregators;

public class RarAggregatorTests
{
    [Fact]
    public void UpdateDatabaseKeepsMatchingPathsFromSeparateArchivesSeparate()
    {
        using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var mountDirectory = CreateMountDirectory();
        var aggregator = new RarAggregator(dbClient, mountDirectory, checkedFullHealth: false);

        aggregator.UpdateDatabase([
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment("archive-a", "episode.mkv")
                ]
            },
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment("archive-b", "episode.mkv")
                ]
            }
        ]);

        var items = dbContext.ChangeTracker.Entries<DavItem>()
            .Select(x => x.Entity)
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("episode.mkv", item.Name));
    }

    [Fact]
    public void UpdateDatabaseAllowsMultipleRangesFromSameRarVolume()
    {
        using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var mountDirectory = CreateMountDirectory();
        var aggregator = new RarAggregator(dbClient, mountDirectory, checkedFullHealth: false);

        aggregator.UpdateDatabase([
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment(
                        archiveName: "archive-a",
                        pathWithinArchive: "episode.mkv",
                        partNumberFromFilename: 1,
                        byteRangeWithinPart: LongRange.FromStartAndSize(0, 50),
                        fileUncompressedSize: 100),
                    CreateStoredFileSegment(
                        archiveName: "archive-a",
                        pathWithinArchive: "episode.mkv",
                        partNumberFromFilename: 1,
                        byteRangeWithinPart: LongRange.FromStartAndSize(50, 50),
                        fileUncompressedSize: 100)
                ]
            }
        ]);

        var multipartFile = Assert.Single(dbContext.BlobMultipartFiles);
        Assert.Equal(2, multipartFile.Metadata.FileParts.Length);
        Assert.Equal(0, multipartFile.Metadata.FileParts[0].FilePartByteRange.StartInclusive);
        Assert.Equal(50, multipartFile.Metadata.FileParts[1].FilePartByteRange.StartInclusive);
    }

    private static DavItem CreateMountDirectory()
    {
        var categoryDirectory = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: "tv",
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null
        );

        return DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryDirectory,
            name: "season-pack",
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: Guid.NewGuid(),
            fileBlobId: null
        );
    }

    private static RarProcessor.StoredFileSegment CreateStoredFileSegment(
        string archiveName,
        string pathWithinArchive,
        int partNumberFromFilename = 1,
        LongRange? byteRangeWithinPart = null,
        long fileUncompressedSize = 100)
    {
        return new RarProcessor.StoredFileSegment
        {
            NzbFile = new NzbFile { Subject = $"{archiveName}.part01.rar" },
            PartSize = 100,
            ArchiveName = archiveName,
            PartNumber = new RarProcessor.PartNumber
            {
                PartNumberFromHeader = 0,
                PartNumberFromFilename = partNumberFromFilename
            },
            ReleaseDate = DateTimeOffset.UtcNow,
            PathWithinArchive = pathWithinArchive,
            ByteRangeWithinPart = byteRangeWithinPart ?? LongRange.FromStartAndSize(0, 100),
            AesParams = null,
            FileUncompressedSize = fileUncompressedSize
        };
    }
}

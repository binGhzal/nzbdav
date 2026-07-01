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

    private static RarProcessor.StoredFileSegment CreateStoredFileSegment(string archiveName, string pathWithinArchive)
    {
        return new RarProcessor.StoredFileSegment
        {
            NzbFile = new NzbFile { Subject = $"{archiveName}.part01.rar" },
            PartSize = 100,
            ArchiveName = archiveName,
            PartNumber = new RarProcessor.PartNumber
            {
                PartNumberFromHeader = 0,
                PartNumberFromFilename = 1
            },
            ReleaseDate = DateTimeOffset.UtcNow,
            PathWithinArchive = pathWithinArchive,
            ByteRangeWithinPart = LongRange.FromStartAndSize(0, 100),
            AesParams = null,
            FileUncompressedSize = 100
        };
    }
}

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
    public void ToDavMultipartFileMetaRejectsNullRarParts()
    {
        var rarFile = new DavRarFile
        {
            Id = Guid.NewGuid(),
            RarParts = null!
        };

        var exception = Assert.Throws<InvalidDataException>(() => rarFile.ToDavMultipartFileMeta());
        Assert.Contains("rar", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public void UpdateDatabaseStoresMinimalSegmentSlicesForArchiveRanges()
    {
        using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var mountDirectory = CreateMountDirectory();
        var aggregator = new RarAggregator(dbClient, mountDirectory, checkedFullHealth: false);
        var nzbFile = CreateNzbFile(("segment-1", 1, 4), ("segment-2", 2, 4), ("segment-3", 3, 4));

        aggregator.UpdateDatabase([
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment(
                        archiveName: "archive-a",
                        pathWithinArchive: "episode.mkv",
                        byteRangeWithinPart: new LongRange(2, 9),
                        fileUncompressedSize: 7,
                        partSize: 12,
                        nzbFile: nzbFile)
                ]
            }
        ]);

        var multipartFile = Assert.Single(dbContext.BlobMultipartFiles);
        var filePart = Assert.Single(multipartFile.Metadata.FileParts);

        Assert.Empty(filePart.SegmentIds);
        Assert.Equal(3, filePart.SegmentSlices.Length);
        Assert.Equal("segment-1", filePart.SegmentSlices[0].SegmentId);
        Assert.Equal(new LongRange(2, 4), filePart.SegmentSlices[0].SegmentByteRange);
        Assert.Equal(new LongRange(0, 2), filePart.SegmentSlices[0].FilePartByteRange);
        Assert.Equal("segment-2", filePart.SegmentSlices[1].SegmentId);
        Assert.Equal(new LongRange(0, 4), filePart.SegmentSlices[1].SegmentByteRange);
        Assert.Equal(new LongRange(2, 6), filePart.SegmentSlices[1].FilePartByteRange);
        Assert.Equal("segment-3", filePart.SegmentSlices[2].SegmentId);
        Assert.Equal(new LongRange(0, 1), filePart.SegmentSlices[2].SegmentByteRange);
        Assert.Equal(new LongRange(6, 7), filePart.SegmentSlices[2].FilePartByteRange);
    }

    [Fact]
    public void UpdateDatabaseFallsBackWhenSegmentBytesDoNotMatchDecodedPartSize()
    {
        using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var mountDirectory = CreateMountDirectory();
        var aggregator = new RarAggregator(dbClient, mountDirectory, checkedFullHealth: false);
        var nzbFile = CreateNzbFile(("segment-1", 1, 5), ("segment-2", 2, 5));

        aggregator.UpdateDatabase([
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment(
                        archiveName: "archive-a",
                        pathWithinArchive: "episode.mkv",
                        byteRangeWithinPart: new LongRange(2, 8),
                        fileUncompressedSize: 6,
                        partSize: 8,
                        nzbFile: nzbFile)
                ]
            }
        ]);

        var filePart = Assert.Single(Assert.Single(dbContext.BlobMultipartFiles).Metadata.FileParts);
        Assert.Empty(filePart.SegmentSlices);
        Assert.Equal(["segment-1", "segment-2"], filePart.SegmentIds);
    }

    [Fact]
    public void UpdateDatabaseFallsBackWhenAnySegmentBytesAreMissing()
    {
        using var dbContext = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbContext);
        var mountDirectory = CreateMountDirectory();
        var aggregator = new RarAggregator(dbClient, mountDirectory, checkedFullHealth: false);
        var nzbFile = CreateNzbFile(("segment-1", 1, 4), ("segment-2", 2, 0));

        aggregator.UpdateDatabase([
            new RarProcessor.Result
            {
                StoredFileSegments =
                [
                    CreateStoredFileSegment(
                        archiveName: "archive-a",
                        pathWithinArchive: "episode.mkv",
                        byteRangeWithinPart: new LongRange(1, 4),
                        fileUncompressedSize: 3,
                        partSize: 4,
                        nzbFile: nzbFile)
                ]
            }
        ]);

        var filePart = Assert.Single(Assert.Single(dbContext.BlobMultipartFiles).Metadata.FileParts);
        Assert.Empty(filePart.SegmentSlices);
        Assert.Equal(["segment-1", "segment-2"], filePart.SegmentIds);
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
        long fileUncompressedSize = 100,
        long partSize = 100,
        NzbFile? nzbFile = null)
    {
        return new RarProcessor.StoredFileSegment
        {
            NzbFile = nzbFile ?? new NzbFile { Subject = $"{archiveName}.part01.rar" },
            PartSize = partSize,
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

    private static NzbFile CreateNzbFile(params (string MessageId, int Number, long Bytes)[] segments)
    {
        var nzbFile = new NzbFile { Subject = "archive-a.part01.rar" };
        foreach (var segment in segments)
        {
            nzbFile.Segments.Add(new NzbSegment
            {
                MessageId = segment.MessageId,
                Number = segment.Number,
                Bytes = segment.Bytes
            });
        }

        return nzbFile;
    }
}

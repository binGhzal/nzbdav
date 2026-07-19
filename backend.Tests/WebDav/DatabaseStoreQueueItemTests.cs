using System.Reflection;
using System.Text;
using backend.Tests.Services;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.WebDav;

namespace backend.Tests.WebDav;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DatabaseStoreQueueItemTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DatabaseStoreQueueItemTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetReadableStreamAsync_ReadsModernBlobBackedUpload()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var expected = "<nzb><file subject=\"blob-backed\" /></nzb>"u8.ToArray();
        var queueItem = CreateQueueItem(expected.Length);
        dbContext.QueueItems.Add(queueItem);
        await dbContext.SaveChangesAsync();
        await using var input = new MemoryStream(expected);
        await BlobStore.WriteBlob(queueItem.Id, (Stream)input);

        try
        {
            var item = new DatabaseStoreQueueItem(queueItem, new DavDatabaseClient(dbContext));

            await using var stream = await item.GetReadableStreamAsync(CancellationToken.None);

            Assert.Equal(expected, await ReadAllBytesAsync(stream));
        }
        finally
        {
            BlobStore.Delete(queueItem.Id);
        }
    }

    [Fact]
    public async Task GetReadableStreamAsync_FallsBackToLegacyDatabaseContents()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        const string expected = "<nzb><file subject=\"legacy\" /></nzb>";
        var queueItem = CreateQueueItem(Encoding.UTF8.GetByteCount(expected));
        dbContext.QueueItems.Add(queueItem);
        dbContext.QueueNzbContents.Add(new QueueNzbContents
        {
            Id = queueItem.Id,
            NzbContents = expected
        });
        await dbContext.SaveChangesAsync();
        var item = new DatabaseStoreQueueItem(queueItem, new DavDatabaseClient(dbContext));

        await using var stream = await item.GetReadableStreamAsync(CancellationToken.None);

        Assert.Equal(expected, Encoding.UTF8.GetString(await ReadAllBytesAsync(stream)));
    }

    [Fact]
    public async Task GetReadableStreamAsync_ReportsLockedBlobAsRetryable()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var queueItem = CreateQueueItem(1);
        dbContext.QueueItems.Add(queueItem);
        dbContext.QueueNzbContents.Add(new QueueNzbContents
        {
            Id = queueItem.Id,
            NzbContents = "<nzb><file subject=\"stale-legacy\" /></nzb>"
        });
        await dbContext.SaveChangesAsync();
        await using var input = new MemoryStream([1]);
        await BlobStore.WriteBlob(queueItem.Id, (Stream)input);

        try
        {
            await using var locked = new FileStream(
                GetBlobPath(queueItem.Id),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var item = new DatabaseStoreQueueItem(queueItem, new DavDatabaseClient(dbContext));

            await Assert.ThrowsAsync<RetryableDownloadException>(() =>
                item.GetReadableStreamAsync(CancellationToken.None));
        }
        finally
        {
            BlobStore.Delete(queueItem.Id);
        }
    }

    private static QueueItem CreateQueueItem(long size)
    {
        var id = Guid.NewGuid();
        return new QueueItem
        {
            Id = id,
            CreatedAt = DateTime.UtcNow,
            FileName = $"{id:N}.nzb",
            JobName = id.ToString("N"),
            NzbFileSize = size,
            TotalSegmentBytes = 0,
            Category = "movies",
            Priority = QueueItem.PriorityOption.Paused,
            PostProcessing = QueueItem.PostProcessingOption.None,
            PauseUntil = null
        };
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
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

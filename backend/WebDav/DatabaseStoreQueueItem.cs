using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreQueueItem(
    QueueItem queueItem,
    DavDatabaseClient dbClient
) : BaseStoreReadonlyItem
{
    public override string Name => queueItem.FileName;
    public override string UniqueKey => queueItem.Id.ToString();
    public override long FileSize => queueItem.NzbFileSize;
    public override DateTime CreatedAt => queueItem.CreatedAt;

    public override async Task<Stream> GetReadableStreamAsync(CancellationToken ct)
    {
        var id = queueItem.Id;
        var stream = await dbClient.ReadQueueNzbStreamAsync(id, ct).ConfigureAwait(false);
        return stream ?? throw new FileNotFoundException($"Could not find nzb document with id: {id}");
    }
}

using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStore(
    IHttpContextAccessor httpContextAccessor,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    ArrDownloadReportService arrDownloadReportService,
    ArrOperationsService arrOperationsService,
    NzbBlobIngestCoordinator nzbBlobIngestCoordinator
) : IStore
{
    private readonly DatabaseStoreCollection _root = new(
        DavItem.Root,
        httpContextAccessor.HttpContext!,
        dbClient,
        configManager,
        usenetClient,
        queueManager,
        websocketManager,
        arrDownloadReportService,
        arrOperationsService,
        nzbBlobIngestCoordinator
    );

    public async Task<IStoreItem?> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        path = path.Trim('/');
        return path == "" ? _root : await _root.ResolvePath(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<IStoreItem?> GetItemAsync(Uri uri, CancellationToken cancellationToken)
    {
        var request = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException("A WebDAV request context is required.");
        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        return GetItemAsync(
            WebDavPathBaseMiddleware.StripPathBase(request, absolutePath),
            cancellationToken);
    }

    public async Task<IStoreCollection?> GetCollectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        return await GetItemAsync(uri, cancellationToken).ConfigureAwait(false) as IStoreCollection;
    }
}

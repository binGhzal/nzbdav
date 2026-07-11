using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreSymlinkCollection(
    DavItem davDirectory,
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : BaseStoreReadonlyCollection
{
    public override string Name => davDirectory.Name;
    public override string UniqueKey => davDirectory.Id.ToString();
    public override DateTime CreatedAt => davDirectory.CreatedAt;

    private Guid TargetId => davDirectory.Id == DavItem.SymlinkFolder.Id ? DavItem.ContentFolder.Id : davDirectory.Id;
    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // return database item
        var name = Regex.Replace(request.Name, @"\.rclonelink$", "");
        var child = await dbClient
            .GetDirectoryChildAsync(TargetId, name, request.CancellationToken)
            .ConfigureAwait(false);
        if (child is not null)
        {
            var hidden = await dbClient.Ctx.ImportReceipts
                .AsNoTracking()
                .AnyAsync(
                    x => x.DavItemId == child.Id && x.State != ImportReceiptState.Available,
                    request.CancellationToken)
                .ConfigureAwait(false);
            return hidden ? null : GetItem(child);
        }

        // return empty category folder
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        if (isSymlinkFolder)
        {
            var categories = configManager.GetApiCategories();
            if (categories.Contains(request.Name))
            {
                return new BaseStoreEmptyCollection(request.Name);
            }
        }

        // the item does not exist
        return null;
    }

    protected override async Task<IStoreItem[]> GetAllItemsAsync(CancellationToken cancellationToken)
    {
        // if we are a category folder within the /completed-symlinks dir,
        // then we only want to show children that correspond to Completed History items.
        var isCategoryFolder = davDirectory.ParentId == DavItem.ContentFolder.Id;
        var children = isCategoryFolder
            ? await dbClient.GetCompletedSymlinkCategoryChildren(davDirectory.Name, cancellationToken)
                .ConfigureAwait(false)
            : await dbClient.GetDirectoryChildrenAsync(TargetId, cancellationToken).ConfigureAwait(false);

        var childIds = children.Select(x => x.Id).ToList();
        var hiddenIds = childIds.Count == 0
            ? []
            : await dbClient.Ctx.ImportReceipts
                .AsNoTracking()
                .Where(x => childIds.Contains(x.DavItemId) && x.State != ImportReceiptState.Available)
                .Select(x => x.DavItemId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        // map DavItems to IStoreItems
        var hiddenIdSet = hiddenIds.ToHashSet();
        var result = children.Where(x => !hiddenIdSet.Contains(x.Id)).Select(GetItem);

        // include any missing category folders
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        if (isSymlinkFolder)
        {
            result = result.Concat(configManager.GetApiCategories()
                .Except(children.Select(x => x.Name))
                .Select(x => new BaseStoreEmptyCollection(x)));
        }

        return result.ToArray();
    }

    protected override async Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        // Cannot delete from symlink root folder
        var isSymlinkFolder = davDirectory.Id == DavItem.SymlinkFolder.Id;
        if (isSymlinkFolder) return await base.DeleteItemAsync(request).ConfigureAwait(false);

        // Items cannot be deleted from the '/completed-symlinks' folder.
        // This path simply mirrors the '/content' folder, except with symlinks.
        // This allows radarr/sonarr to import the lightweight symlink, instead
        // of trying to import large-sized media.
        //
        // However, when radarr attempts to import the symlink, it does so by moving
        // it to the media library. But since the symlinks lives in a separate
        // file-system (rclone-mounted webdav), the operating system will instead
        // perform a copy-and-delete operation. For the import to succeed, we must
        // trick the OS into thinking that the "delete" worked.
        //
        // The symlink doesn't actually exist anywhere. It takes zero storage and
        // just gets created in memory, as needed, for webdav requests. The only
        // thing that exists is the underlying data within the '/content' directory
        // But in this request, we only want to "delete" the symlink. We don't want
        // to delete the underlying media within the '/content' directory.
        //
        // (204 No Content) is the correct status code to return for a successful
        // deletion of a file. This status code means the server has successfully
        // processed the request, and there is no additional content to send in the
        // response body. (200 OK) is also acceptable, but more appropriate for when
        // the server also returns a response body with the status of the operation.
        var name = Regex.Replace(request.Name, @"\.rclonelink$", "");
        var child = await dbClient
            .GetDirectoryChildAsync(TargetId, name, request.CancellationToken)
            .ConfigureAwait(false);
        if (child?.HistoryItemId == null || child.Type != DavItem.ItemType.UsenetFile)
            return DavStatusCode.NotFound;

        try
        {
            await new ImportReceiptService(dbClient.Ctx)
                .ClaimAsync(
                    new ImportClaimRequest(child.Id, child.HistoryItemId.Value, DateTimeOffset.UtcNow),
                    request.CancellationToken)
                .ConfigureAwait(false);
            return DavStatusCode.NoContent;
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Unable to persist completed-symlink import claim for {DavItemId}.", child.Id);
            dbClient.Ctx.ChangeTracker.Clear();
            return DavStatusCode.ServiceUnavailable;
        }
    }

    private IStoreItem GetItem(DavItem davItem)
    {
        return davItem.SubType switch
        {
            DavItem.ItemSubType.Directory =>
                new DatabaseStoreSymlinkCollection(davItem, dbClient, configManager),
            DavItem.ItemSubType.NzbFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            DavItem.ItemSubType.RarFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            DavItem.ItemSubType.MultipartFile =>
                new DatabaseStoreSymlinkFile(davItem, configManager),
            _ => throw new ArgumentException("Unrecognized directory child type.")
        };
    }

}

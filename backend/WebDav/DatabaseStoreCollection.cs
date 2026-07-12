using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreCollection(
    DavItem davDirectory,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    LazyRarResolver lazyRarResolver
) : BaseStoreReadonlyCollection
{
    public override string Name => davDirectory.Name;
    public override string UniqueKey => davDirectory.Id.ToString();
    public override DateTime CreatedAt => davDirectory.CreatedAt;
    private static readonly StaticEmbeddedFile Readme = new("StaticFiles.root.README.md", "README");

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // return readme file
        var isReadme = davDirectory.Id == DavItem.Root.Id && request.Name == Readme.Name;
        if (isReadme) return Readme;

        // return database item
        var child = await dbClient
            .GetDirectoryChildAsync(davDirectory.Id, request.Name, request.CancellationToken)
            .ConfigureAwait(false);
        if (child is not null) return GetItem(child);

        // return empty category folder
        var isContentFolder = davDirectory.Id == DavItem.ContentFolder.Id;
        if (isContentFolder)
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
        // read DavItems from the database
        var children = await dbClient
            .GetDirectoryChildrenAsync(davDirectory.Id, cancellationToken)
            .ConfigureAwait(false);

        // map DavItems to IStoreItems
        var result = children.Select(GetItem);

        // include the readme file
        if (davDirectory.Id == DavItem.Root.Id)
            result = result.Append(Readme);

        // include any missing category folders
        if (davDirectory.Id == DavItem.ContentFolder.Id)
        {
            result = result.Concat(configManager.GetApiCategories()
                .Except(children.Select(x => x.Name))
                .Select(x => new BaseStoreEmptyCollection(x)));
        }

        return result.ToArray();
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }

    protected override async Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        // Cannot delete items if readonly-webdav is enabled
        if (configManager.IsEnforceReadonlyWebdavEnabled())
            return DavStatusCode.Forbidden;

        // Cannot delete items from dav root.
        if (davDirectory.Id == DavItem.Root.Id)
            return DavStatusCode.Forbidden;

        // Get the item being deleted
        var davItem = await dbClient.GetDirectoryChildAsync(davDirectory.Id, request.Name, request.CancellationToken)
            .ConfigureAwait(false);
        if (davItem is null) return DavStatusCode.NotFound;

        // If the item is a file, simply delete it and we're done.
        if (davItem.Type is DavItem.ItemType.UsenetFile)
        {
            var historyItemId = davItem.HistoryItemId;
            dbClient.Ctx.Items.Remove(davItem);
            await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
            await PruneEmptyHistoryAsync(historyItemId, request.CancellationToken).ConfigureAwait(false);
            return DavStatusCode.Ok;
        }

        // If the item is a directory and it not a protected directory, simply delete it.
        if (davItem.Type == DavItem.ItemType.Directory && !davItem.IsProtected())
        {
            var historyItemId = davItem.HistoryItemId;
            dbClient.Ctx.Items.Remove(davItem);
            await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
            await PruneEmptyHistoryAsync(historyItemId, request.CancellationToken).ConfigureAwait(false);
            return DavStatusCode.Ok;
        }

        // forbid deletion of any other items
        return DavStatusCode.Forbidden;
    }

    // After deleting a DavItem, if no other DavItems still reference its HistoryItem,
    // remove the HistoryItem too. Without this, external tools polling /api?mode=history
    // (third-party SAB-compatible clients, Sonarr, etc.) see the entry as Completed and hand the
    // player a URL pointing at the file we just deleted — re-clicking never re-enqueues.
    private async Task PruneEmptyHistoryAsync(Guid? historyItemId, CancellationToken ct)
    {
        if (historyItemId is null) return;
        var stillReferenced = await dbClient.Ctx.Items
            .AsNoTracking()
            .AnyAsync(x => x.HistoryItemId == historyItemId.Value, ct)
            .ConfigureAwait(false);
        if (stillReferenced) return;

        var history = await dbClient.Ctx.HistoryItems
            .FirstOrDefaultAsync(h => h.Id == historyItemId.Value, ct)
            .ConfigureAwait(false);
        if (history is null) return;

        dbClient.Ctx.HistoryItems.Remove(history);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, historyItemId.Value.ToString());
    }

    private IStoreItem GetItem(DavItem davItem)
    {
        return davItem.SubType switch
        {
            DavItem.ItemSubType.IdsRoot =>
                new DatabaseStoreIdsCollection(
                    davItem.Name, "", httpContext, dbClient, usenetClient, configManager, lazyRarResolver),
            DavItem.ItemSubType.NzbsRoot =>
                new DatabaseStoreWatchFolder(
                    davItem, dbClient, configManager, queueManager, websocketManager),
            DavItem.ItemSubType.Directory or DavItem.ItemSubType.ContentRoot =>
                new DatabaseStoreCollection(
                    davItem, httpContext, dbClient, configManager, usenetClient, queueManager, websocketManager,
                    lazyRarResolver),
            DavItem.ItemSubType.SymlinkRoot =>
                new DatabaseStoreSymlinkCollection(
                    davItem, dbClient, configManager),
            DavItem.ItemSubType.NzbFile =>
                new DatabaseStoreNzbFile(
                    davItem, httpContext, dbClient, usenetClient, configManager),
            DavItem.ItemSubType.RarFile =>
                new DatabaseStoreRarFile(
                    davItem, httpContext, dbClient, usenetClient, configManager),
            DavItem.ItemSubType.MultipartFile =>
                new DatabaseStoreMultipartFile(
                    davItem, httpContext, dbClient, usenetClient, configManager, lazyRarResolver),
            _ => throw new ArgumentException("Unrecognized directory child type.")
        };
    }
}

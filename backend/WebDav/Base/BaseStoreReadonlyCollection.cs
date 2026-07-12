using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Requests;
using Serilog;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreReadonlyCollection : BaseStoreCollection
{
    protected override Task<StoreItemResult> CopyAsync(CopyRequest request)
    {
        Log.Warning("Cannot copy item {ItemName}: forbidden", request.Name);
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreItemResult> CreateItemAsync(CreateItemRequest request)
    {
        Log.Warning("Cannot create item {ItemName}: forbidden", request.Name);
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreCollectionResult> CreateCollectionAsync(CreateCollectionRequest request)
    {
        Log.Warning("Cannot create directory {DirectoryName}: forbidden", request.Name);
        return Task.FromResult(new StoreCollectionResult(DavStatusCode.Forbidden));
    }

    protected override Task<StoreItemResult> MoveItemAsync(MoveItemRequest request)
    {
        Log.Warning("Cannot move item {ItemName}: forbidden", request.SourceName);
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }

    protected override Task<DavStatusCode> DeleteItemAsync(DeleteItemRequest request)
    {
        Log.Warning("Cannot delete item {ItemName}: forbidden", request.Name);
        return Task.FromResult(DavStatusCode.Forbidden);
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }
}

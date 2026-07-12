using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreIdsCollection(
    string name,
    string currentPath,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    LazyRarResolver lazyRarResolver
) : BaseStoreReadonlyCollection
{
    public override string Name => name;
    public override string UniqueKey => currentPath;
    public override DateTime CreatedAt => default;

    private const string Alphabet = "0123456789abcdef";

    private readonly string[] _currentPathParts = currentPath.Split(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries
    );

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        var (dir, ctx, db, usenet, config, lazy) =
            (request.Name, httpContext, dbClient, usenetClient, configManager, lazyRarResolver);
        if (_currentPathParts.Length < DavItem.IdPrefixLength)
        {
            if (request.Name.Length != 1) return null;
            if (!Alphabet.Contains(request.Name[0])) return null;
            return new DatabaseStoreIdsCollection(dir, Path.Join(currentPath, dir), ctx, db, usenet, config, lazy);
        }

        var item = await dbClient.GetFileById(request.Name).ConfigureAwait(false);
        return item == null ? null : new DatabaseStoreIdFile(item, ctx, dbClient, usenet, config, lazy);
    }

    protected override async Task<IStoreItem[]> GetAllItemsAsync(CancellationToken cancellationToken)
    {
        var (ctx, db, usenet, config, lazy) =
            (httpContext, dbClient, usenetClient, configManager, lazyRarResolver);
        if (_currentPathParts.Length < DavItem.IdPrefixLength)
            return Alphabet
                .Select(x => x.ToString())
                .Select(x => new DatabaseStoreIdsCollection(x, Path.Join(currentPath, x), ctx, db, usenet, config, lazy))
                .Select(x => x as IStoreItem)
                .ToArray();

        var idPrefix = string.Join("", _currentPathParts);
        return (await dbClient.GetFilesByIdPrefix(idPrefix).ConfigureAwait(false))
            .Select(x => new DatabaseStoreIdFile(x, ctx, db, usenet, config, lazy))
            .Select(x => x as IStoreItem)
            .ToArray();
    }
}

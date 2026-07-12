using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Extensions;

public static class DavItemExtensions
{
    private static readonly HashSet<Guid> Protected =
    [
        DavItem.Root.Id,
        DavItem.SymlinkFolder.Id,
        DavItem.ContentFolder.Id,
        DavItem.NzbFolder.Id,
        DavItem.IdsFolder.Id,
    ];

    private static readonly HashSet<Guid> CategoryParents =
    [
        DavItem.ContentFolder.Id,
        DavItem.NzbFolder.Id,
    ];

    public static bool IsProtected(this DavItem item)
    {
        if (Protected.Contains(item.Id)) return true;
        if (item.ParentId.HasValue && CategoryParents.Contains(item.ParentId.Value)) return true;
        return false;
    }
}

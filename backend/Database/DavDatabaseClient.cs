using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseClient(DavDatabaseContext ctx)
{
    public DavDatabaseContext Ctx => ctx;

    // file
    public Task<DavItem?> GetFileById(string id)
    {
        var guid = Guid.Parse(id);
        return ctx.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == guid);
    }

    public Task<List<DavItem>> GetFilesByIdPrefix(string prefix)
    {
        return ctx.Items
            .AsNoTracking()
            .Where(i => i.IdPrefix == prefix)
            .Where(i => i.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync();
    }

    // directory
    public Task<List<DavItem>> GetDirectoryChildrenAsync(Guid dirId, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking().Where(x => x.ParentId == dirId).ToListAsync(ct);
    }

    public Task<DavItem?> GetDirectoryChildAsync(Guid dirId, string childName, CancellationToken ct = default)
    {
        return ctx.Items.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ParentId == dirId && x.Name == childName, ct);
    }

    public async Task<long> GetRecursiveSize(Guid dirId, CancellationToken ct = default)
    {
        if (dirId == DavItem.Root.Id)
        {
            return await Ctx.Items.SumAsync(x => x.FileSize, ct).ConfigureAwait(false) ?? 0;
        }

        const string sql = @"
            WITH RECURSIVE RecursiveChildren AS (
                SELECT Id, FileSize
                FROM DavItems
                WHERE ParentId = @parentId

                UNION ALL

                SELECT d.Id, d.FileSize
                FROM DavItems d
                INNER JOIN RecursiveChildren rc ON d.ParentId = rc.Id
            )
            SELECT IFNULL(SUM(FileSize), 0)
            FROM RecursiveChildren;
        ";
        var connection = Ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@parentId";
        parameter.Value = dirId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    // usenet files
    public async Task<DavNzbFile?> GetDavNzbFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavNzbFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.NzbFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavRarFile?> GetDavRarFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavRarFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.RarFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    public async Task<DavMultipartFile?> GetDavMultipartFileAsync(DavItem davItem, CancellationToken ct = default)
    {
        // attempt to read from blob-store
        var blobId = davItem.FileBlobId;
        if (blobId.HasValue)
        {
            var blob = await BlobStore.ReadBlob<DavMultipartFile>(blobId.Value);
            if (blob is not null) return blob;
        }

        // read from database
        return await ctx.MultipartFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == davItem.Id, ct)
            .ConfigureAwait(false);
    }

    // queue
    public async Task<(QueueItem? queueItem, Stream? queueNzbStream)> GetTopQueueItem
    (
        CancellationToken ct = default
    )
    {
        // read queue item from database
        var nowTime = DateTime.Now;
        var queueItem = await Ctx.QueueItems
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Where(q => q.PauseUntil == null || nowTime >= q.PauseUntil)
            .Skip(0)
            .Take(1)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // attempt to read nzb contents from blob-store.
        var queueNzbStream = queueItem != null
            ? BlobStore.ReadBlob(queueItem.Id)
            : null;

        // otherwise, read nzb contents from database.
        if (queueItem != null && queueNzbStream == null)
        {
            var queueNzbContents = await Ctx.QueueNzbContents
                .FirstOrDefaultAsync(q => q.Id == queueItem.Id, ct)
                .ConfigureAwait(false);

            queueNzbStream = queueNzbContents != null
                ? new MemoryStream(Encoding.UTF8.GetBytes(queueNzbContents.NzbContents))
                : null;
        }

        // return
        return (queueItem, queueNzbStream);
    }

    public Task<QueueItem[]> GetQueueItems
    (
        string? category,
        int start = 0,
        int limit = int.MaxValue,
        CancellationToken ct = default
    )
    {
        var queueItems = category != null
            ? Ctx.QueueItems.Where(q => q.Category == category)
            : Ctx.QueueItems;
        return queueItems
            .AsNoTracking()
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Skip(start)
            .Take(limit)
            .ToArrayAsync(cancellationToken: ct);
    }

    public Task<int> GetQueueItemsCount(string? category, CancellationToken ct = default)
    {
        var queueItems = category != null
            ? Ctx.QueueItems.Where(q => q.Category == category)
            : Ctx.QueueItems;
        return queueItems.CountAsync(cancellationToken: ct);
    }

    public async Task RemoveQueueItemsAsync(List<Guid> ids, CancellationToken ct = default)
    {
        // Capture group keys before delete so we can cascade-clean orphaned
        // watchdog attempts whose only link was via the now-gone queue item.
        var groupKeys = await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id) && x.ContentGroupKey != null)
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        await Ctx.QueueItems
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await CascadeWatchdogEntriesAsync(ids, groupKeys, ct).ConfigureAwait(false);
    }

    // Delete watchdog attempts that were tied to the deleted queue/history items.
    // - QueueItemId match: direct link (queue-processor flow).
    // - ContentGroupKey match: orphaned group — no remaining queue or history item
    //   still references it. Skips groups that other items still reference so we
    //   don't nuke unrelated history when only one of several queue items is removed.
    //
    // excludeHistoryIds: history rows that are tracked for removal but not yet
    // committed; they'd otherwise appear "still referenced" and block cleanup.
    private async Task CascadeWatchdogEntriesAsync(
        List<Guid> queueItemIds,
        List<string> contentGroupKeys,
        CancellationToken ct,
        List<Guid>? excludeHistoryIds = null)
    {
        if (queueItemIds.Count > 0)
        {
            await Ctx.WatchdogEntries
                .Where(x => x.QueueItemId != null && queueItemIds.Contains(x.QueueItemId!.Value))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        if (contentGroupKeys.Count == 0) return;

        var stillReferencedInQueue = await Ctx.QueueItems
            .Where(x => x.ContentGroupKey != null && contentGroupKeys.Contains(x.ContentGroupKey!))
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var historyQuery = Ctx.HistoryItems
            .Where(x => x.ContentGroupKey != null && contentGroupKeys.Contains(x.ContentGroupKey!));
        if (excludeHistoryIds is { Count: > 0 })
            historyQuery = historyQuery.Where(x => !excludeHistoryIds.Contains(x.Id));

        var stillReferencedInHistory = await historyQuery
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var stillReferenced = new HashSet<string>(stillReferencedInQueue);
        stillReferenced.UnionWith(stillReferencedInHistory);

        var orphanedKeys = contentGroupKeys.Where(k => !stillReferenced.Contains(k)).ToList();
        if (orphanedKeys.Count == 0) return;

        await Ctx.WatchdogEntries
            .Where(x => x.ContentGroupKey != null && orphanedKeys.Contains(x.ContentGroupKey!))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // history
    public async Task<HistoryItem?> GetHistoryItemAsync(string id)
    {
        return await Ctx.HistoryItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == Guid.Parse(id)).ConfigureAwait(false);
    }

    public async Task RemoveHistoryItemsAsync(List<Guid> ids, bool deleteFiles, CancellationToken ct = default)
    {
        // Capture group keys before delete so we can cascade-clean orphaned watchdog
        // attempts below. Done up front because the deleteFiles=false path doesn't
        // load the HistoryItem rows otherwise.
        var groupKeys = await Ctx.HistoryItems
            .Where(x => ids.Contains(x.Id) && x.ContentGroupKey != null)
            .Select(x => x.ContentGroupKey!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        if (deleteFiles)
        {
            var results = await (
                from h in Ctx.HistoryItems
                where ids.Contains(h.Id)
                join d in Ctx.Items on h.DownloadDirId equals d.Id into items
                from d in items.DefaultIfEmpty()
                select new { HistoryItem = h, DavItem = d }
            ).ToListAsync(ct).ConfigureAwait(false);

            var historyItems = results.Select(r => r.HistoryItem).ToList();
            var davItems = results.Where(r => r.DavItem != null).Select(r => r.DavItem!).ToList();
            Ctx.Items.RemoveRange(davItems);
            Ctx.HistoryItems.RemoveRange(historyItems);
            Ctx.HistoryCleanupItems.AddRange(historyItems.Select(x => new HistoryCleanupItem
            {
                Id = x.Id,
                DeleteMountedFiles = deleteFiles
            }));
        }
        else
        {
            // Only remove ids that actually exist. Stub entities for stale ids make EF emit
            // zero-row deletes and roll back the entire batch with a concurrency exception.
            var existing = await Ctx.HistoryItems
                .Where(h => ids.Contains(h.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Ctx.HistoryItems.RemoveRange(existing);
            Ctx.HistoryCleanupItems.AddRange(existing.Select(x => new HistoryCleanupItem
            {
                Id = x.Id,
                DeleteMountedFiles = deleteFiles
            }));
        }

        await CascadeWatchdogEntriesAsync(
            queueItemIds: [],
            contentGroupKeys: groupKeys,
            ct: ct,
            excludeHistoryIds: ids).ConfigureAwait(false);
    }

    private class FileSizeResult
    {
        public long TotalSize { get; init; }
    }

    // health check
    public async Task<List<HealthCheckStat>> GetHealthCheckStatsAsync
    (
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default
    )
    {
        return await Ctx.HealthCheckStats
            .AsNoTracking()
            .Where(h => h.DateStartInclusive >= from && h.DateStartInclusive <= to)
            .GroupBy(h => new { h.Result, h.RepairStatus })
            .Select(g => new HealthCheckStat
            {
                Result = g.Key.Result,
                RepairStatus = g.Key.RepairStatus,
                Count = g.Select(r => r.Count).Sum(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // completed-symlinks
    public async Task<List<DavItem>> GetCompletedSymlinkCategoryChildren(string category,
        CancellationToken ct = default)
    {
        var query = from historyItem in Ctx.HistoryItems
            .AsNoTracking()
                    where historyItem.Category == category
                          && historyItem.DownloadStatus == HistoryItem.DownloadStatusOption.Completed
                          && historyItem.DownloadDirId != null
                    join davItem in Ctx.Items.AsNoTracking() on historyItem.DownloadDirId equals davItem.Id
                    where davItem.Type == DavItem.ItemType.Directory
                    select davItem;
        return await query.Distinct().ToListAsync(ct).ConfigureAwait(false);
    }
}

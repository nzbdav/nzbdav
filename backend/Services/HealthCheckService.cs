using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// This service monitors for health checks
/// </summary>
public class HealthCheckService : BackgroundService
{
    private const int MaximumMissingSegmentIds = 100_000;
    private readonly ConfigManager _configManager;
    private readonly INntpClient _usenetClient;
    private readonly WebsocketManager _websocketManager;
    private readonly BenchmarkGate _benchmarkGate;

    private static readonly HashSet<string> _missingSegmentIds = [];
    private static readonly Queue<string> _missingSegmentOrder = [];

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager,
        BenchmarkGate benchmarkGate
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;
        _benchmarkGate = benchmarkGate;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when provider settings change, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers") &&
                !configEventArgs.ChangedConfig.ContainsKey("usenet.host")) return;
            lock (_missingSegmentIds)
            {
                _missingSegmentIds.Clear();
                _missingSegmentOrder.Clear();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // pause verification while a connection speed-test is running
                if (_benchmarkGate.IsPaused)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // if the repair-job is disabled, then don't do anything
                if (!_configManager.IsRepairJobEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // get concurrency (capped to avoid saturating the NNTP pool)
                var concurrency = _configManager.GetHealthCheckConcurrency();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                // get the davItem to health-check
                await using var dbContext = new DavDatabaseContext();
                var dbClient = new DavDatabaseClient(dbContext);
                var currentDateTime = DateTimeOffset.UtcNow;
                var davItem = await GetHealthCheckQueueItems(dbClient)
                    .Where(x => x.NextHealthCheck == null || x.NextHealthCheck < currentDateTime)
                    .FirstOrDefaultAsync(cts.Token).ConfigureAwait(false);

                // if there is no item to health-check, don't do anything
                if (davItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                    continue;
                }

                // perform the health check
                await PerformHealthCheck(davItem, dbClient, concurrency, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                // OperationCanceledException is expected on sigterm
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error performing background health checks: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public static IOrderedQueryable<DavItem> GetHealthCheckQueueItems(DavDatabaseClient dbClient)
    {
        // Non-null NextHealthCheck first (includes urgent UnixEpoch from dynamic repair),
        // then ascending NextHealthCheck, then newest releases.
        return GetHealthCheckQueueItemsQuery(dbClient)
            .OrderBy(x => x.NextHealthCheck == null ? 1 : 0)
            .ThenBy(x => x.NextHealthCheck)
            .ThenByDescending(x => x.ReleaseDate)
            .ThenBy(x => x.Id);
    }

    public static IQueryable<DavItem> GetHealthCheckQueueItemsQuery(DavDatabaseClient dbClient)
    {
        return dbClient.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Where(x => x.HistoryItemId == null);
    }

    private async Task PerformHealthCheck
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        int concurrency,
        CancellationToken ct
    )
    {
        // Urgent sentinel set by ExceptionMiddleware when streaming hits a missing article.
        // Streaming already confirmed a BODY miss across providers — skip STAT-only recheck
        // (STAT can pass while BODY returns 430; see nzbdav-dev#209) and repair immediately.
        var isUrgentRepair = davItem.NextHealthCheck == DateTimeOffset.UnixEpoch;
        if (isUrgentRepair)
        {
            Log.Information("Performing urgent dynamic repair for {FilePath}", davItem.Path);
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // update the release date, if null
            var segments = await GetAllSegments(davItem, dbClient, ct).ConfigureAwait(false);
            if (davItem.ReleaseDate == null) await UpdateReleaseDate(davItem, segments, ct).ConfigureAwait(false);

            // sample large files to reduce NNTP load while keeping head/tail/stride coverage
            var totalSegments = segments.Count;
            var sampled = SampleSegments(segments);

            // setup progress tracking
            var progressHook = new Progress<int>();
            var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(200));
            progressHook.ProgressChanged += (_, progress) =>
            {
                var message = $"{davItem.Id}|{progress}";
                debounce(() => _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, message));
            };

            // perform health check
            var progress = progressHook.ToPercentage(sampled.Count);
            await _usenetClient.CheckAllSegmentsAsync(sampled, concurrency, progress, ct).ConfigureAwait(false);
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // update the database
            davItem.LastHealthCheck = DateTimeOffset.UtcNow;
            davItem.NextHealthCheck = davItem.ReleaseDate + 2 * (davItem.LastHealthCheck - davItem.ReleaseDate);
            var healthyMessage = sampled.Count < totalSegments
                ? $"File is healthy (sampled {sampled.Count}/{totalSegments} segments)."
                : "File is healthy.";
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Healthy,
                RepairStatus = HealthCheckResult.RepairAction.None,
                Message = healthyMessage
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            if (FilenameUtil.IsImportantFileType(davItem.Name))
            {
                lock (_missingSegmentIds)
                {
                    if (_missingSegmentIds.Add(e.SegmentId))
                        _missingSegmentOrder.Enqueue(e.SegmentId);
                    while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                        _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
                }
            }

            // when usenet article is missing, perform repairs
            await Repair(davItem, dbClient, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// For files with more than 4000 segments, returns a stratified sample:
    /// first 100, last 100, and evenly-spaced middle segments (~4000 total).
    /// Small files are checked in full.
    /// </summary>
    public static List<string> SampleSegments(List<string> segments)
    {
        const int threshold = 4000;
        if (segments.Count <= threshold) return segments;

        const int headCount = 100;
        const int tailCount = 100;
        const int strideTarget = 4000;

        var result = new HashSet<int>();

        for (var i = 0; i < Math.Min(headCount, segments.Count); i++)
            result.Add(i);

        for (var i = Math.Max(0, segments.Count - tailCount); i < segments.Count; i++)
            result.Add(i);

        var stride = Math.Max(1, segments.Count / strideTarget);
        for (var i = 0; i < segments.Count; i += stride)
            result.Add(i);

        return result.OrderBy(i => i).Select(i => segments[i]).ToList();
    }

    private async Task UpdateReleaseDate(DavItem davItem, List<string> segments, CancellationToken ct)
    {
        var firstSegmentId = StringUtil.EmptyToNull(segments.FirstOrDefault());
        if (firstSegmentId == null) return;
        var articleHeadersResponse = await _usenetClient.HeadAsync(firstSegmentId, ct).ConfigureAwait(false);
        var articleHeaders = articleHeadersResponse.ArticleHeaders!;
        davItem.ReleaseDate = articleHeaders.Date;
    }

    private async Task<List<string>> GetAllSegments(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            return nzbFile?.SegmentIds?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return rarFile?.RarParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return multipartFile?.Metadata?.FileParts?.SelectMany(x => x.SegmentIds)?.ToList() ?? [];
        }

        return [];
    }

    private async Task Repair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned,
            // then we can simply delete it.
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null)
            {
                dbClient.Ctx.Items.Remove(davItem);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.Deleted,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";

            // Track whether we could fully determine ownership. If any instance was unreachable,
            // errored, or returned an unusable root folder, a later "no owner found" is unreliable
            // and we must not delete a link one of those instances may own. This is independent of
            // ownerConfirmedOrphan below, which is a definite answer and deletes regardless.
            var anInstanceFailed = false;
            var ownerConfirmedOrphan = false;

            foreach (var arrClient in _configManager.GetArrConfig().GetArrClients())
            {
                List<ArrRootFolder> rootFolders;
                try
                {
                    rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    anInstanceFailed = true;
                    Log.Warning("Health-check repair: could not query root folders from {Host}: {Message}",
                                arrClient.Host, e.Message);
                    continue;
                }

                // Skip null/empty paths: StartsWith(null) throws, and StartsWith("") matches everything.
                // A null/empty path is a malformed response we can't rule this instance in or out
                // with, so if nothing else matches, treat it like an unreachable instance rather
                // than falling through to a delete this instance may not have sanctioned.
                if (!rootFolders.Any(x => !string.IsNullOrEmpty(x.Path) && symlinkOrStrmPath.StartsWith(x.Path)))
                {
                    if (rootFolders.Any(x => string.IsNullOrEmpty(x.Path))) anInstanceFailed = true;
                    continue;
                }

                bool removedAndSearched;
                try
                {
                    removedAndSearched = await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    anInstanceFailed = true;
                    Log.Warning("Health-check repair: remove-and-search failed on {Host}: {Message}",
                                arrClient.Host, e.Message);
                    continue;
                }

                if (removedAndSearched)
                {
                    dbClient.Ctx.Items.Remove(davItem);
                    dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                    {
                        Id = Guid.NewGuid(),
                        DavItemId = davItem.Id,
                        Path = davItem.Path,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Result = HealthCheckResult.HealthResult.Unhealthy,
                        RepairStatus = HealthCheckResult.RepairAction.Repaired,
                        Message = string.Join(" ", [
                            "File had missing articles.",
                            $"Corresponding {linkType} found within Library Dir.",
                            "Triggered new Arr search."
                        ])
                    }));
                    await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    return;
                }

                // The owning instance was reached and reports no corresponding media-item, so this
                // link is a confirmed orphan. Record that ownership was determined and break; the
                // delete below then proceeds even if some other instance was unreachable.
                ownerConfirmedOrphan = true;
                break;
            }

            // Ownership indeterminate (an instance could not be reached or fully queried, and no
            // instance confirmed the file as an orphan): don't delete a link it may own. Defer like
            // the catch below so the item isn't re-selected every scan cycle while the instance is down.
            if (anInstanceFailed && !ownerConfirmedOrphan)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
                {
                    Id = Guid.NewGuid(),
                    DavItemId = davItem.Id,
                    Path = davItem.Path,
                    CreatedAt = utcNow,
                    Result = HealthCheckResult.HealthResult.Unhealthy,
                    RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                    Message = string.Join(" ", [
                        "File had missing articles.",
                        $"Corresponding {linkType} found within Library Dir,",
                        "but at least one Arr instance could not be reached or fully queried, so ownership",
                        "of the file could not be determined. Leaving the file in place rather than deleting it."
                    ])
                }));
                await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);
            dbClient.Ctx.Items.Remove(davItem);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.Deleted,
                Message = string.Join(" ", [
                    "File had missing articles.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "Could not find corresponding Radarr/Sonarr media-item to trigger a new search.",
                    $"Deleted the webdav-file and {linkType}."
                ])
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = utcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                RepairStatus = HealthCheckResult.RepairAction.ActionNeeded,
                Message = $"Error performing file repair: {e.Message}"
            }));
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private HealthCheckResult SendStatus(HealthCheckResult result)
    {
        _ = _websocketManager.SendMessage
        (
            WebsocketTopic.HealthItemStatus,
            $"{result.DavItemId}|{(int)result.Result}|{(int)result.RepairStatus}"
        );
        return result;
    }

    public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
                if (_missingSegmentIds.Contains(segmentId))
                    throw new UsenetArticleNotFoundException(segmentId);
        }
    }
}

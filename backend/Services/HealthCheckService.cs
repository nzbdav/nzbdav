using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
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
    private readonly StreamingFailureTracker _failureTracker;

    private static readonly HashSet<string> _missingSegmentIds = [];
    private static readonly Queue<string> _missingSegmentOrder = [];

    public HealthCheckService
    (
        ConfigManager configManager,
        UsenetStreamingClient usenetClient,
        WebsocketManager websocketManager,
        BenchmarkGate benchmarkGate,
        StreamingFailureTracker failureTracker
    )
    {
        _configManager = configManager;
        _usenetClient = usenetClient;
        _websocketManager = websocketManager;
        _benchmarkGate = benchmarkGate;
        _failureTracker = failureTracker;

        _configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // when provider settings change, clear the missing segments cache
            if (!configEventArgs.ChangedConfig.ContainsKey(ConfigKeys.UsenetProviders)) return;
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
                if (e.TryGetKnownErrorMessage(out var reason))
                {
                    Log.Warning("Background health check deferred. Reason: {Reason}", reason);
                    Log.Debug(e, "Background health check known failure stack");
                }
                else
                {
                    Log.Error(e, "Unexpected error performing background health checks: {Message}", e.Message);
                }

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
            await HandleUrgentRepair(davItem, dbClient, ct).ConfigureAwait(false);
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
            if (_configManager.IsPipeliningEnabled())
            {
                await _usenetClient.CheckAllSegmentsPipelinedAsync(
                        sampled,
                        _configManager.GetPipeliningDepth(),
                        concurrency,
                        progress,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await _usenetClient.CheckAllSegmentsAsync(sampled, concurrency, progress, ct)
                    .ConfigureAwait(false);
            }
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");

            // update the database.
            // the next check is scheduled so the interval doubles with the item's age since release.
            // clamp to a minimum interval: a null release-date (zero-segment item) or a future-dated
            // article header would otherwise schedule the item in the past and hot-loop the service.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = ComputeNextHealthCheck(davItem.ReleaseDate, utcNow);
            _failureTracker.ClearFailure(davItem.Id);
            var healthyMessage = sampled.Count < totalSegments
                ? $"File is healthy (sampled {sampled.Count}/{totalSegments} segments)."
                : "File is healthy.";
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Healthy,
                HealthCheckResult.RepairAction.None,
                healthyMessage, ct).ConfigureAwait(false);
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
        catch (UsenetUnexpectedResponseException e)
        {
            // Connection-level STAT failures (e.g. buffered 400 goodbye) must not trigger
            // repair or leave NextHealthCheck unset — defer and surface ActionNeeded.
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Unexpected NNTP response during health check: {e.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e.IsTransientTransportException())
        {
            // STAT/read timeouts and socket/IO failures must not dump stacks or trigger Arr repair —
            // defer and surface ActionNeeded with a single human-readable Warning.
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|100");
            _ = _websocketManager.SendMessage(WebsocketTopic.HealthItemProgress, $"{davItem.Id}|done");
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            e.TryGetKnownErrorMessage(out var reason);
            Log.Warning(
                "NNTP transport failure during health check for {Path}. Deferred next check. Reason: {Reason}",
                davItem.Path, reason);
            Log.Debug(e, "Health check transport failure stack for {Path}", davItem.Path);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                FormatTransportFailureHealthMessage(reason), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Health-result message for deferred NNTP transport failures (timeouts, socket/IO).
    /// </summary>
    internal static string FormatTransportFailureHealthMessage(string reason) =>
        $"NNTP transport failure during health check: {reason}";

    /// <summary>
    /// Schedules the next health check so the interval doubles with the item's age since release,
    /// floored at one hour from <paramref name="utcNow"/> so null or future release dates cannot
    /// schedule the item in the past and hot-loop the service.
    /// </summary>
    public static DateTimeOffset ComputeNextHealthCheck(DateTimeOffset? releaseDate, DateTimeOffset utcNow)
    {
        var minimumNextHealthCheck = utcNow + TimeSpan.FromHours(1);
        var nextHealthCheck = releaseDate + 2 * (utcNow - releaseDate);
        return nextHealthCheck == null || nextHealthCheck < minimumNextHealthCheck
            ? minimumNextHealthCheck
            : nextHealthCheck.Value;
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

    /// <summary>
    /// How an urgent (streaming-triggered) repair should proceed given auto-remove settings.
    /// </summary>
    public enum UrgentRepairDisposition
    {
        /// <summary>Call Repair() with today's behavior (Arr search or orphan delete).</summary>
        RepairNormally,
        /// <summary>Clear the urgent flag and wait for more streaming failures.</summary>
        Defer,
        /// <summary>Force the repair-delete path even for library-linked items.</summary>
        ForceDelete,
    }

    /// <summary>
    /// Decides urgent-repair disposition from failure count and auto-remove config.
    /// Threshold 0 disables the feature (identical to today's immediate Repair).
    /// </summary>
    public static UrgentRepairDisposition GetUrgentRepairDisposition(
        int threshold,
        int failureCount,
        bool hasLibraryLink,
        bool autoRemoveUnlinkedOnly)
    {
        if (threshold <= 0)
            return UrgentRepairDisposition.RepairNormally;

        if (failureCount < threshold)
        {
            // Library-linked + unlinked-only: prefer Arr remove-and-search immediately.
            // Unlinked (and aggressive linked) wait until the threshold before deleting.
            if (hasLibraryLink && autoRemoveUnlinkedOnly)
                return UrgentRepairDisposition.RepairNormally;
            return UrgentRepairDisposition.Defer;
        }

        if (hasLibraryLink && autoRemoveUnlinkedOnly)
            return UrgentRepairDisposition.RepairNormally;

        return UrgentRepairDisposition.ForceDelete;
    }

    /// <summary>
    /// Outcome of consulting Arr instances for a library-linked unhealthy item.
    /// </summary>
    public enum ArrLinkedRepairDecision
    {
        /// <summary>An Arr instance owned the file and remove-and-search succeeded.</summary>
        RemoveAndSearchSucceeded,
        /// <summary>
        /// At least one Arr instance was unreachable/unusable and no instance confirmed the link
        /// as an orphan — leave the DavItem in place.
        /// </summary>
        DeferUnreachable,
        /// <summary>Ownership was fully determined: no Arr media-item; safe to delete.</summary>
        DeleteConfirmedOrphan,
    }

    /// <summary>
    /// Consults Arr clients to decide whether a library-linked unhealthy item should trigger
    /// remove-and-search, be deferred while an instance is down, or be deleted as a confirmed orphan.
    /// Extracted so the unreachable-instance fail-safe can be unit-tested without a full Repair harness.
    /// </summary>
    internal static async Task<ArrLinkedRepairDecision> DecideArrLinkedRepairAsync(
        IEnumerable<ArrClient> arrClients,
        string symlinkOrStrmPath,
        CancellationToken ct)
    {
        // Track whether we could fully determine ownership. If any instance was unreachable,
        // errored, or returned an unusable root folder, a later "no owner found" is unreliable
        // and we must not delete a link one of those instances may own. This is independent of
        // ownerConfirmedOrphan below, which is a definite answer and deletes regardless.
        var anInstanceFailed = false;
        var ownerConfirmedOrphan = false;

        foreach (var arrClient in arrClients)
        {
            ct.ThrowIfCancellationRequested();

            List<ArrRootFolder> rootFolders;
            try
            {
                rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                anInstanceFailed = true;
                Log.Warning(e, "Health-check repair: could not query root folders from {Host}",
                            arrClient.Host);
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
                Log.Warning(e, "Health-check repair: remove-and-search failed on {Host}",
                            arrClient.Host);
                continue;
            }

            if (removedAndSearched)
                return ArrLinkedRepairDecision.RemoveAndSearchSucceeded;

            // The owning instance was reached and reports no corresponding media-item, so this
            // link is a confirmed orphan. Record that ownership was determined and break; the
            // delete below then proceeds even if some other instance was unreachable.
            ownerConfirmedOrphan = true;
            break;
        }

        if (anInstanceFailed && !ownerConfirmedOrphan)
            return ArrLinkedRepairDecision.DeferUnreachable;

        return ArrLinkedRepairDecision.DeleteConfirmedOrphan;
    }

    private async Task HandleUrgentRepair(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        var threshold = _configManager.GetAutoRemoveAfterFailures();
        var failureCount = _failureTracker.GetFailureCount(davItem.Id);
        var unlinkedOnly = _configManager.IsAutoRemoveUnlinkedOnly();
        var hasLibraryLink = OrganizedLinksUtil.GetLink(davItem, _configManager) != null;
        var disposition = GetUrgentRepairDisposition(threshold, failureCount, hasLibraryLink, unlinkedOnly);

        if (disposition == UrgentRepairDisposition.Defer)
        {
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromHours(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                string.Join(" ", [
                    "File had missing articles during streaming.",
                    $"Streaming failure count: {failureCount}/{threshold}.",
                    "Auto-remove deferred until the failure threshold is reached."
                ]), ct).ConfigureAwait(false);
            return;
        }

        await Repair(
            davItem,
            dbClient,
            ct,
            forceDelete: disposition == UrgentRepairDisposition.ForceDelete,
            streamingFailureCount: failureCount).ConfigureAwait(false);
    }

    private async Task Repair(
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct,
        bool forceDelete = false,
        int? streamingFailureCount = null)
    {
        try
        {
            // if the file pattern has been marked as ignored,
            // then don't bother trying to repair it. We can simply delete it.
            var blocklistedFiles = _configManager.GetBlocklistedFiles();
            if (BlocklistedFilePostProcessor.MatchesAnyPattern(davItem.Name, blocklistedFiles))
            {
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "missing articles; filename matches blocklist pattern");
                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    string.Join(" ", [
                        "File had missing articles.",
                        "Filename pattern is marked in settings as an ignored (unwanted) file.",
                        "Deleted file."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is unlinked/orphaned, or auto-remove force-delete is requested,
            // then we can simply delete it (and the library link when present).
            var symlinkOrStrmPath = OrganizedLinksUtil.GetLink(davItem, _configManager);
            if (symlinkOrStrmPath == null || forceDelete)
            {
                if (forceDelete && symlinkOrStrmPath != null)
                    await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);

                var auditReason = forceDelete
                    ? streamingFailureCount is > 0
                        ? $"missing articles; auto-removed after repeated streaming failures (count={streamingFailureCount})"
                        : "missing articles; auto-removed after repeated streaming failures"
                    : "missing articles; orphaned (no library symlink/strm)";
                DeletionAuditLog.Record("health-repair", davItem, auditReason);

                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);

                var failureNote = streamingFailureCount is > 0
                    ? $" Streaming failure count: {streamingFailureCount}."
                    : "";
                string deleteMessage;
                if (forceDelete && symlinkOrStrmPath != null)
                {
                    var forceDeleteLinkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
                    deleteMessage = string.Join(" ", [
                        "File had missing articles.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        $"Deleted the webdav-file and {forceDeleteLinkType}."
                    ]);
                }
                else if (forceDelete)
                {
                    deleteMessage = string.Join(" ", [
                        "File had missing articles.",
                        $"Auto-removed after repeated streaming failures.{failureNote}",
                        "Deleted file."
                    ]);
                }
                else
                {
                    deleteMessage = string.Join(" ", [
                        "File had missing articles.",
                        "Could not find corresponding symlink or strm-file within Library Dir.",
                        "Deleted file."
                    ]);
                }

                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Deleted,
                    deleteMessage, ct).ConfigureAwait(false);
                return;
            }

            // if the unhealthy item is linked within the organized media-library
            // then we must find the corresponding arr instance and trigger a new search.
            var linkType = symlinkOrStrmPath.ToLower().EndsWith("strm") ? "strm-file" : "symlink";
            var arrDecision = await DecideArrLinkedRepairAsync(
                _configManager.GetArrConfig().GetArrClients(),
                symlinkOrStrmPath,
                ct).ConfigureAwait(false);

            if (arrDecision == ArrLinkedRepairDecision.RemoveAndSearchSucceeded)
            {
                DeletionAuditLog.Record(
                    "health-repair",
                    davItem,
                    "missing articles; Arr remove-and-search triggered");
                dbClient.Ctx.Items.Remove(davItem);
                _failureTracker.ClearFailure(davItem.Id);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.Repaired,
                    string.Join(" ", [
                        "File had missing articles.",
                        $"Corresponding {linkType} found within Library Dir.",
                        "Triggered new Arr search."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // Ownership indeterminate (an instance could not be reached or fully queried, and no
            // instance confirmed the file as an orphan): don't delete a link it may own. Defer like
            // the catch below so the item isn't re-selected every scan cycle while the instance is down.
            if (arrDecision == ArrLinkedRepairDecision.DeferUnreachable)
            {
                var utcNow = DateTimeOffset.UtcNow;
                davItem.LastHealthCheck = utcNow;
                davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
                await RecordHealthResult(
                    dbClient, davItem,
                    HealthCheckResult.HealthResult.Unhealthy,
                    HealthCheckResult.RepairAction.ActionNeeded,
                    string.Join(" ", [
                        "File had missing articles.",
                        $"Corresponding {linkType} found within Library Dir,",
                        "but at least one Arr instance could not be reached or fully queried, so ownership",
                        "of the file could not be determined. Leaving the file in place rather than deleting it."
                    ]), ct).ConfigureAwait(false);
                return;
            }

            // if we could not find a corresponding arr instance
            // then we can delete both the item and the link-file.
            await Task.Run(() => File.Delete(symlinkOrStrmPath)).ConfigureAwait(false);
            DeletionAuditLog.Record(
                "health-repair",
                davItem,
                "missing articles; library link present but no Arr media-item (confirmed orphan)");
            dbClient.Ctx.Items.Remove(davItem);
            _failureTracker.ClearFailure(davItem.Id);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.Deleted,
                string.Join(" ", [
                    "File had missing articles.",
                    $"Corresponding {linkType} found within Library Dir.",
                    "Could not find corresponding Radarr/Sonarr media-item to trigger a new search.",
                    $"Deleted the webdav-file and {linkType}."
                ]), ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // if an error is encountered during repairs,
            // then mark the item as unhealthy, and check again in a day.
            var utcNow = DateTimeOffset.UtcNow;
            davItem.LastHealthCheck = utcNow;
            davItem.NextHealthCheck = utcNow + TimeSpan.FromDays(1);
            await RecordHealthResult(
                dbClient, davItem,
                HealthCheckResult.HealthResult.Unhealthy,
                HealthCheckResult.RepairAction.ActionNeeded,
                $"Error performing file repair: {e.Message}", ct).ConfigureAwait(false);
        }
    }


    private async Task RecordHealthResult
    (
        DavDatabaseClient dbClient,
        DavItem davItem,
        HealthCheckResult.HealthResult result,
        HealthCheckResult.RepairAction repairStatus,
        string message,
        CancellationToken ct
    )
    {
        dbClient.Ctx.HealthCheckResults.Add(SendStatus(new HealthCheckResult()
        {
            Id = Guid.NewGuid(),
            DavItemId = davItem.Id,
            Path = davItem.Path,
            CreatedAt = DateTimeOffset.UtcNow,
            Result = result,
            RepairStatus = repairStatus,
            Message = message
        }));
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
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

    public static void AddMissingSegmentIds(IEnumerable<string> segmentIds)
    {
        lock (_missingSegmentIds)
        {
            foreach (var segmentId in segmentIds)
            {
                if (_missingSegmentIds.Add(segmentId))
                    _missingSegmentOrder.Enqueue(segmentId);
                while (_missingSegmentIds.Count > MaximumMissingSegmentIds)
                    _missingSegmentIds.Remove(_missingSegmentOrder.Dequeue());
            }
        }
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

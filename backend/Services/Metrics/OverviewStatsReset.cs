using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Shared helpers for wiping overview/metrics statistics while preserving
/// per-provider data-cap gauges. Used by <c>ClearOverviewStatsController</c>.
/// </summary>
public static class OverviewStatsReset
{
    /// <summary>
    /// Batch size for chunked SegmentFetches deletes in the per-provider path.
    /// Exposed for tests so they can prove the loop drains without seeding
    /// 50k rows.
    /// </summary>
    internal static int SegmentFetchDeleteBatchSize { get; set; } = 50_000;

    /// <summary>
    /// Preserves the per-provider data-cap gauge across the wipe:
    /// gauge = live tracker bytes + BytesUsedOffset, so folding the current
    /// usage into the offset keeps the displayed value stable once the
    /// tracker and ProviderHourly are zeroed. When providerKey is given,
    /// only matching config entries are folded. Returns true when any
    /// provider changed.
    /// </summary>
    public static bool FoldUsageIntoOffsets(
        UsenetProviderConfig config, ProviderBytesTracker tracker, long nowMs, string? providerKey = null)
    {
        var changed = false;
        foreach (var provider in config.Providers)
        {
            if (provider.ProviderId == Guid.Empty) continue;
            if (providerKey != null && UsenetProviderIdentity.MetricsKey(provider) != providerKey) continue;
            var usage = ProviderUsageHelper.ComputeUsage(tracker, provider);
            if (usage == provider.BytesUsedOffset && provider.BytesUsedResetAt == nowMs) continue;
            provider.BytesUsedOffset = usage;
            provider.BytesUsedResetAt = nowMs;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Deletes every row from all metrics tables. Each unfiltered
    /// ExecuteDeleteAsync emits a plain "DELETE FROM t" which hits SQLite's
    /// truncate optimization, so this stays fast on multi-GB databases.
    /// Freed pages are reclaimed by the retention service's hourly
    /// incremental_vacuum; vacuuming here would be the slow part.
    /// Returns total rows deleted.
    /// </summary>
    public static async Task<int> WipeAsync(MetricsDbContext db, CancellationToken ct)
    {
        var deleted = 0;
        deleted += await db.SegmentFetches.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.ReadSessions.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.MetricEvents.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.ThroughputMinutes.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.ProviderMinutes.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.ProviderHourly.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.FailoverMisses.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.FailoverHourly.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.CatalogueDaily.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Deletes rows attributable to one provider. Global rollups
    /// (ThroughputMinutes, ReadSessions, MetricEvents, CatalogueDaily) are
    /// left intact because their rows cannot be split by provider.
    /// Deletes commit per statement (and per batch for SegmentFetches), so a
    /// cancelled run is harmless and re-running completes the remainder.
    /// </summary>
    public static async Task<int> WipeProviderAsync(MetricsDbContext db, string providerKey, CancellationToken ct)
    {
        // SegmentFetches is the only potentially huge provider-keyed table
        // (24h of raw rows). Chunked deletes keep each write transaction
        // short so concurrent MetricsWriter flushes never exhaust their
        // 5s busy_timeout behind us.
        var deleted = 0;
        var batchSize = Math.Max(1, SegmentFetchDeleteBatchSize);
        int batch;
        do
        {
            batch = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM SegmentFetches WHERE rowid IN
                    (SELECT rowid FROM SegmentFetches WHERE Provider = {0} LIMIT {1})
                """,
                new object[] { providerKey, batchSize }, ct).ConfigureAwait(false);
            deleted += batch;
        } while (batch > 0);

        deleted += await db.ProviderMinutes
            .Where(x => x.Provider == providerKey).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.ProviderHourly
            .Where(x => x.Provider == providerKey).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.FailoverMisses
            .Where(x => x.FromProvider == providerKey || x.ToProvider == providerKey)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        deleted += await db.FailoverHourly
            .Where(x => x.FromProvider == providerKey || x.ToProvider == providerKey)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return deleted;
    }
}

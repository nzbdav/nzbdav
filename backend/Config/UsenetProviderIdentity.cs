using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Config;

/// <summary>
/// Ensures each configured Usenet account has a stable <see cref="UsenetProviderConfig.ConnectionDetails.ProviderId"/>
/// and remaps legacy host-keyed metrics rows onto those ids once at startup.
/// </summary>
public static class UsenetProviderIdentity
{
    public static string MetricsKey(Guid providerId) => providerId.ToString("N");

    public static string MetricsKey(UsenetProviderConfig.ConnectionDetails provider)
    {
        if (provider.ProviderId == Guid.Empty)
            throw new InvalidOperationException("ProviderId must be assigned before use as a metrics key.");
        return MetricsKey(provider.ProviderId);
    }

    /// <summary>
    /// Assigns missing ProviderIds and persists them to the main config DB. Must run
    /// after config load, before UsenetStreamingClient is constructed. Never throws:
    /// startup must not fail over metrics keying, and the connection pool self-heals
    /// missing ids at construction time.
    /// </summary>
    public static async Task EnsureAsync(ConfigManager configManager, CancellationToken ct = default)
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var assigned = EnsureProviderIds(providerConfig);
        if (!assigned) return;

        try
        {
            await SaveProvidersAsync(configManager, providerConfig, ct).ConfigureAwait(false);
            Log.Information("Assigned and persisted ProviderId for {Count} usenet provider(s).",
                providerConfig.Providers.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Failed to persist assigned usenet ProviderIds; continuing with in-memory ids. " +
                "Metrics keys may not be stable across restarts until a save succeeds.");
        }
    }

    /// <summary>
    /// Assigns a Guid to every provider missing one. Returns true when any id was created.
    /// </summary>
    public static bool EnsureProviderIds(UsenetProviderConfig config)
    {
        var assigned = false;
        foreach (var provider in config.Providers)
        {
            if (provider.ProviderId != Guid.Empty) continue;
            provider.ProviderId = Guid.NewGuid();
            assigned = true;
        }
        return assigned;
    }

    /// <summary>
    /// When saving usenet.providers, preserve ProviderIds from the stored config for
    /// entries that omit them (match by Host+Port+User). Generates a new Guid when
    /// no match exists. Mutates <paramref name="incoming"/> in place.
    /// </summary>
    public static void NormalizeProviderIdsOnSave(
        UsenetProviderConfig incoming,
        UsenetProviderConfig? existing)
    {
        var unusedExisting = existing?.Providers
            .Where(p => p.ProviderId != Guid.Empty)
            .ToList() ?? [];

        foreach (var provider in incoming.Providers)
        {
            if (provider.ProviderId != Guid.Empty) continue;

            var matchIndex = unusedExisting.FindIndex(p =>
                string.Equals(p.Host, provider.Host, StringComparison.OrdinalIgnoreCase)
                && p.Port == provider.Port
                && string.Equals(p.User, provider.User, StringComparison.Ordinal));

            if (matchIndex >= 0)
            {
                provider.ProviderId = unusedExisting[matchIndex].ProviderId;
                unusedExisting.RemoveAt(matchIndex);
            }
            else
            {
                provider.ProviderId = Guid.NewGuid();
            }
        }
    }

    /// <summary>
    /// Persists the full usenet.providers config item and refreshes
    /// <see cref="ConfigManager"/>. Used after assigning ProviderIds and after
    /// folding usage into BytesUsedOffset during an overview-stats reset.
    /// </summary>
    public static async Task SaveProvidersAsync(
        ConfigManager configManager,
        UsenetProviderConfig providerConfig,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(providerConfig);
        await using var db = new DavDatabaseContext();
        var item = await db.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == ConfigKeys.UsenetProviders, ct)
            .ConfigureAwait(false);
        if (item == null)
        {
            item = new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = json,
            };
            db.ConfigItems.Add(item);
        }
        else
        {
            item.ConfigValue = json;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        configManager.UpdateValues([item]);
    }

    public static async Task RemapHostKeyedMetricsAsync(
        UsenetProviderConfig config,
        CancellationToken ct = default)
    {
        await using var db = new MetricsDbContext();
        await RemapHostKeyedMetricsAsync(config, db, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remaps legacy host-keyed metrics onto ProviderId keys. When multiple config
    /// entries share a host, only the first inherits the historical rows.
    ///
    /// Designed to run in the background after startup: it never throws, and each
    /// merge commits in its own short transaction so an interrupted run (shutdown,
    /// container kill) resumes where it left off instead of restarting from zero.
    /// Raw SegmentFetches rows are intentionally not remapped — they expire within
    /// 24 hours and rewriting them is what made large databases stall at startup.
    /// </summary>
    public static async Task RemapHostKeyedMetricsAsync(
        UsenetProviderConfig config,
        MetricsDbContext db,
        CancellationToken ct = default)
    {
        if (config.Providers.Count == 0) return;

        var seenHosts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var remapped = 0;
            foreach (var provider in config.Providers)
            {
                var host = provider.Host;
                if (string.IsNullOrEmpty(host) || provider.ProviderId == Guid.Empty)
                    continue;
                if (!seenHosts.Add(host))
                    continue;

                var metricsKey = MetricsKey(provider);
                var hasAnyHostKeyed = await db.ProviderHourly
                        .AnyAsync(x => x.Provider == host, ct).ConfigureAwait(false)
                    || await db.ProviderMinutes
                        .AnyAsync(x => x.Provider == host, ct).ConfigureAwait(false)
                    || await db.FailoverMisses
                        .AnyAsync(x => x.FromProvider == host || x.ToProvider == host, ct)
                        .ConfigureAwait(false)
                    || await db.FailoverHourly
                        .AnyAsync(x => x.FromProvider == host || x.ToProvider == host, ct)
                        .ConfigureAwait(false);
                if (!hasAnyHostKeyed) continue;

                await MergeProviderMinutesAsync(db, host, metricsKey, ct).ConfigureAwait(false);
                await MergeProviderHourlyAsync(db, host, metricsKey, ct).ConfigureAwait(false);
                await RemapFailoverMissesAsync(db, host, metricsKey, ct).ConfigureAwait(false);
                await MergeFailoverHourlyAsync(db, host, metricsKey, ct).ConfigureAwait(false);
                remapped++;
            }

            if (remapped > 0)
                Log.Information("Remapped host-keyed metrics rows onto ProviderId for {Count} provider(s).", remapped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Information("Host-keyed metrics remap interrupted by shutdown; it will resume on next startup.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remap host-keyed provider metrics; continuing with ProviderId keys going forward.");
        }
    }

    /// <summary>
    /// Runs a merge-and-delete pair atomically so a mid-run interruption can never
    /// double-count rows when the remap is retried on the next startup.
    /// </summary>
    private static async Task ExecuteAtomicallyAsync(
        MetricsDbContext db, CancellationToken ct, Func<Task> work)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await work().ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static Task MergeProviderMinutesAsync(
        MetricsDbContext db, string host, string metricsKey, CancellationToken ct)
    {
        return ExecuteAtomicallyAsync(db, ct, async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderMinutes (Minute, Provider, Articles, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, Hist)
                SELECT Minute, {0}, Articles, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, Hist
                FROM ProviderMinutes WHERE Provider = {1}
                ON CONFLICT(Minute, Provider) DO UPDATE SET
                    Articles = ProviderMinutes.Articles + excluded.Articles,
                    BytesFetched = ProviderMinutes.BytesFetched + excluded.BytesFetched,
                    Misses = ProviderMinutes.Misses + excluded.Misses,
                    Errors = ProviderMinutes.Errors + excluded.Errors,
                    Retries = ProviderMinutes.Retries + excluded.Retries,
                    FailoverSaves = ProviderMinutes.FailoverSaves + excluded.FailoverSaves,
                    SumDurationMs = ProviderMinutes.SumDurationMs + excluded.SumDurationMs;
                """,
                new object[] { metricsKey, host }, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ProviderMinutes WHERE Provider = {0}",
                new object[] { host }, ct).ConfigureAwait(false);
        });
    }

    private static Task MergeProviderHourlyAsync(
        MetricsDbContext db, string host, string metricsKey, CancellationToken ct)
    {
        return ExecuteAtomicallyAsync(db, ct, async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ProviderHourly (Hour, Provider, Articles, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, P95DurationMs)
                SELECT Hour, {0}, Articles, BytesFetched, Misses, Errors, Retries, FailoverSaves, SumDurationMs, P95DurationMs
                FROM ProviderHourly WHERE Provider = {1}
                ON CONFLICT(Hour, Provider) DO UPDATE SET
                    Articles = ProviderHourly.Articles + excluded.Articles,
                    BytesFetched = ProviderHourly.BytesFetched + excluded.BytesFetched,
                    Misses = ProviderHourly.Misses + excluded.Misses,
                    Errors = ProviderHourly.Errors + excluded.Errors,
                    Retries = ProviderHourly.Retries + excluded.Retries,
                    FailoverSaves = ProviderHourly.FailoverSaves + excluded.FailoverSaves,
                    SumDurationMs = ProviderHourly.SumDurationMs + excluded.SumDurationMs;
                """,
                new object[] { metricsKey, host }, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ProviderHourly WHERE Provider = {0}",
                new object[] { host }, ct).ConfigureAwait(false);
        });
    }

    private static async Task RemapFailoverMissesAsync(
        MetricsDbContext db, string host, string metricsKey, CancellationToken ct)
    {
        // Single UPDATEs are naturally resumable: rerunning after an interruption
        // simply matches fewer (or zero) rows.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE FailoverMisses SET FromProvider = {0} WHERE FromProvider = {1}",
            new object[] { metricsKey, host }, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE FailoverMisses SET ToProvider = {0} WHERE ToProvider = {1}",
            new object[] { metricsKey, host }, ct).ConfigureAwait(false);
    }

    private static async Task MergeFailoverHourlyAsync(
        MetricsDbContext db, string host, string metricsKey, CancellationToken ct)
    {
        // Remap FromProvider first, then ToProvider, merging on conflict.
        await ExecuteAtomicallyAsync(db, ct, async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
                SELECT Hour, {0}, ToProvider, Reason, Count
                FROM FailoverHourly WHERE FromProvider = {1}
                ON CONFLICT(Hour, FromProvider, ToProvider, Reason) DO UPDATE SET
                    Count = FailoverHourly.Count + excluded.Count;
                """,
                new object[] { metricsKey, host }, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM FailoverHourly WHERE FromProvider = {0}",
                new object[] { host }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await ExecuteAtomicallyAsync(db, ct, async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO FailoverHourly (Hour, FromProvider, ToProvider, Reason, Count)
                SELECT Hour, FromProvider, {0}, Reason, Count
                FROM FailoverHourly WHERE ToProvider = {1}
                ON CONFLICT(Hour, FromProvider, ToProvider, Reason) DO UPDATE SET
                    Count = FailoverHourly.Count + excluded.Count;
                """,
                new object[] { metricsKey, host }, ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM FailoverHourly WHERE ToProvider = {0}",
                new object[] { host }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

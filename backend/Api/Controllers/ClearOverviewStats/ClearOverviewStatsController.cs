using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.ClearOverviewStats;

[ApiController]
[Route("api/clear-overview-stats")]
public class ClearOverviewStatsController(
    ConfigManager configManager,
    MetricsWriter metricsWriter,
    ProviderBytesTracker bytesTracker
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var request = new ClearOverviewStatsRequest(HttpContext);
        var providerKey = request.ProviderKey;

        // Validate the key against configured providers so a stale UI cannot
        // silently no-op (deleted providers require a full reset instead).
        var providerConfig = configManager.GetUsenetProviderConfig();
        if (providerKey != null && !providerConfig.Providers.Any(p =>
                p.ProviderId != Guid.Empty && UsenetProviderIdentity.MetricsKey(p) == providerKey))
            throw new BadHttpRequestException("Unknown provider");

        // 1. Preserve data-cap gauges before the tracker and ProviderHourly are zeroed.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (OverviewStatsReset.FoldUsageIntoOffsets(providerConfig, bytesTracker, nowMs, providerKey))
            await UsenetProviderIdentity.SaveProvidersAsync(configManager, providerConfig, ct)
                .ConfigureAwait(false);

        // 2. Drop unflushed rows so they don't reappear after the wipe.
        if (providerKey == null) metricsWriter.DiscardQueuedAndResetStats();
        else metricsWriter.DiscardQueuedForProvider(providerKey);

        // 3. Wipe metrics tables (all, or provider-keyed rows only).
        await using var db = new MetricsDbContext();
        var deletedRows = providerKey == null
            ? await OverviewStatsReset.WipeAsync(db, ct).ConfigureAwait(false)
            : await OverviewStatsReset.WipeProviderAsync(db, providerKey, ct).ConfigureAwait(false);

        // 4. Zero in-memory lifetime counters and pending minute buckets.
        if (providerKey == null) bytesTracker.ResetCounters();
        else bytesTracker.ResetProvider(providerKey);

        return Ok(new ClearOverviewStatsResponse { Status = true, DeletedRows = deletedRows });
    }
}

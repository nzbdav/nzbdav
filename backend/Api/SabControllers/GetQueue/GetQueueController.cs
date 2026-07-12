using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get in progress item
        var (inProgressQueueItem, progressPercentage) = queueManager.GetInProgressQueueItem();

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items
        var getQueueItemsTask = dbClient.GetQueueItems(request.Category, request.Start, request.Limit, ct);
        var queueItems = (await getQueueItemsTask.ConfigureAwait(false))
            .Where(x => x.Id != inProgressQueueItem?.Id)
            .ToArray();

        // hosts of every configured Usenet provider — used to show idle providers
        // alongside active ones for the in-progress download
        var configuredProviders = configManager.GetUsenetProviderConfig().Providers;
        var configuredHosts = configuredProviders
            .Select(p => p.Host)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct()
            .ToList();
        var nicknamesByHost = configuredProviders
            .Where(p => !string.IsNullOrWhiteSpace(p.Nickname))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Nickname, StringComparer.OrdinalIgnoreCase);

        // get slots
        var slots = queueItems
            .Prepend(request is { Start: 0, Limit: > 0 } ? inProgressQueueItem : null)
            .Where(queueItem => queueItem != null)
            .Select((queueItem, index) =>
            {
                var isInProgress = queueItem == inProgressQueueItem;
                var percentage = (isInProgress ? progressPercentage : 0)!.Value;
                var status = isInProgress ? "Downloading" : "Queued";
                var providerUsage = providerUsageTracker.Snapshot(queueItem!.Id);
                if (isInProgress && configuredHosts.Count > 0)
                {
                    var merged = new Dictionary<string, long>();
                    foreach (var host in configuredHosts) merged[host] = 0;
                    foreach (var kv in providerUsage) merged[kv.Key] = kv.Value;
                    providerUsage = merged;
                }
                return GetQueueResponse.QueueSlot.FromQueueItem(queueItem!, index, percentage, status, providerUsage, nicknamesByHost);
            })
            .ToList();

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = false,
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}

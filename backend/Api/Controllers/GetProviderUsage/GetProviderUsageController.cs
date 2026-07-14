using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

[ApiController]
[Route("api/get-provider-usage")]
public class GetProviderUsageController(
    ConfigManager configManager,
    ProviderBytesTracker bytesTracker
) : BaseApiController
{
    private async Task<GetProviderUsageResponse> GetUsageAsync()
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var recentHoursByKey = await ProviderUsageHelper
            .ReadRecentHoursAsync(providerConfig.Providers
                .Where(p => p.ProviderId != Guid.Empty)
                .Select(UsenetProviderIdentity.MetricsKey))
            .ConfigureAwait(false);

        var items = providerConfig.Providers
            .Select((provider, index) =>
            {
                var used = ProviderUsageHelper.ComputeUsage(bytesTracker, provider);
                List<(long Hour, long Bytes)>? recentHours = null;
                if (provider.ProviderId != Guid.Empty)
                    recentHoursByKey.TryGetValue(UsenetProviderIdentity.MetricsKey(provider), out recentHours);
                var (bytesPerDay, daysRemaining) = ProviderUsageHelper.ComputeBurnRate(provider, used, recentHours);
                return new GetProviderUsageResponse.ProviderUsageItem
                {
                    Index = index,
                    Host = provider.Host,
                    Nickname = provider.Nickname,
                    BytesUsed = used,
                    ByteLimit = provider.ByteLimit,
                    OverLimit = ProviderUsageHelper.IsOverLimit(bytesTracker, provider),
                    BytesPerDay = bytesPerDay,
                    DaysRemaining = daysRemaining,
                };
            })
            .ToList();

        return new GetProviderUsageResponse
        {
            Status = true,
            Providers = items,
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var response = await GetUsageAsync().ConfigureAwait(false);
        return Ok(response);
    }
}

using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.ClearOverviewStats;

public class ClearOverviewStatsRequest
{
    /// <summary>Metrics key (ProviderId "N" format) or null for a full reset.</summary>
    public string? ProviderKey { get; }

    public ClearOverviewStatsRequest(HttpContext context)
    {
        var raw = context.GetQueryParam("provider");
        ProviderKey = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}

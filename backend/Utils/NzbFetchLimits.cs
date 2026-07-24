namespace NzbWebDAV.Utils;

/// <summary>Shared hard caps for buffered NZB HTTP responses and their in-memory caches.</summary>
public static class NzbFetchLimits
{
    /// <summary>Maximum NZB response body size accepted from an indexer (50 MiB).</summary>
    public const long MaxResponseBytes = 50L * 1024 * 1024;

    /// <summary>Aggregate byte budget for <see cref="Services.NzbFetchCoalescer"/> success cache.</summary>
    public const long CoalescerMaxCacheBytes = 256L * 1024 * 1024;

    /// <summary>Maximum entries retained by <see cref="Services.NzbFetchCoalescer"/>.</summary>
    public const int CoalescerMaxCacheEntries = 64;

    /// <summary>Aggregate byte budget for non-null NZB bodies in <see cref="Services.PreflightCache"/>.</summary>
    public const long PreflightMaxCacheBytes = 256L * 1024 * 1024;

    /// <summary>Maximum entries retained by <see cref="Services.PreflightCache"/>.</summary>
    public const int PreflightMaxCacheEntries = 128;
}

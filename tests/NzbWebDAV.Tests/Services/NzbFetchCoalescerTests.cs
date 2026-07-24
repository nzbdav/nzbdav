using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class NzbFetchCoalescerTests
{
    [Fact]
    public async Task GetOrFetchAsync_CoalescesSameKeyAndCachesSuccess()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 1024, maxCacheEntries: 8);
        var calls = 0;
        var payload = new byte[] { 1, 2, 3 };

        Task<byte[]?> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<byte[]?>(payload);
        }

        var first = await coalescer.GetOrFetchAsync("http://nzb/a", null, false, Fetch, CancellationToken.None);
        var second = await coalescer.GetOrFetchAsync("http://nzb/a", null, false, Fetch, CancellationToken.None);

        Assert.Same(payload, first);
        Assert.Same(payload, second);
        Assert.Equal(1, calls);
        Assert.Equal(3, coalescer.CurrentCachedBytes);
        Assert.Equal(1, coalescer.CachedEntryCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_DoesNotCacheNull()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 1024, maxCacheEntries: 8);
        var calls = 0;

        var first = await coalescer.GetOrFetchAsync(
            "http://nzb/miss", null, false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<byte[]?>(null);
            },
            CancellationToken.None);

        var second = await coalescer.GetOrFetchAsync(
            "http://nzb/miss", null, false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<byte[]?>(null);
            },
            CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, calls);
        Assert.Equal(0, coalescer.CurrentCachedBytes);
        Assert.Equal(0, coalescer.CachedEntryCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_EvictsOldestWhenEntryBudgetExceeded()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 1024, maxCacheEntries: 2);
        await coalescer.GetOrFetchAsync("http://nzb/1", null, false, _ => Task.FromResult<byte[]?>([1]), CancellationToken.None);
        await Task.Delay(5);
        await coalescer.GetOrFetchAsync("http://nzb/2", null, false, _ => Task.FromResult<byte[]?>([2]), CancellationToken.None);
        await Task.Delay(5);
        await coalescer.GetOrFetchAsync("http://nzb/3", null, false, _ => Task.FromResult<byte[]?>([3]), CancellationToken.None);

        Assert.Equal(2, coalescer.CachedEntryCount);
        Assert.Equal(2, coalescer.CurrentCachedBytes);

        var calls = 0;
        var again = await coalescer.GetOrFetchAsync(
            "http://nzb/1", null, false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<byte[]?>([1]);
            },
            CancellationToken.None);

        Assert.Equal(new byte[] { 1 }, again);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrFetchAsync_EvictsUntilByteBudgetAllowsAdmit()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 10, maxCacheEntries: 8);
        await coalescer.GetOrFetchAsync("http://nzb/a", null, false, _ => Task.FromResult<byte[]?>(new byte[6]), CancellationToken.None);
        await coalescer.GetOrFetchAsync("http://nzb/b", null, false, _ => Task.FromResult<byte[]?>(new byte[6]), CancellationToken.None);

        Assert.True(coalescer.CurrentCachedBytes <= 10);
        Assert.Equal(1, coalescer.CachedEntryCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_DoesNotCacheWhenSingleEntryExceedsBudget()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 4, maxCacheEntries: 8);
        var payload = new byte[8];

        var result = await coalescer.GetOrFetchAsync(
            "http://nzb/big", null, false,
            _ => Task.FromResult<byte[]?>(payload),
            CancellationToken.None);

        Assert.Same(payload, result);
        Assert.Equal(0, coalescer.CachedEntryCount);
        Assert.Equal(0, coalescer.CurrentCachedBytes);
    }

    [Fact]
    public async Task GetOrFetchAsync_ConcurrentUniqueUrlsNeverExceedBudget()
    {
        var coalescer = new NzbFetchCoalescer(maxCacheBytes: 100, maxCacheEntries: 50);
        var tasks = Enumerable.Range(0, 40)
            .Select(i => coalescer.GetOrFetchAsync(
                $"http://nzb/{i}", null, false,
                _ => Task.FromResult<byte[]?>(new byte[10]),
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.True(coalescer.CurrentCachedBytes <= 100);
        Assert.True(coalescer.CachedEntryCount <= 50);
        Assert.True(coalescer.CachedEntryCount * 10L >= coalescer.CurrentCachedBytes);
    }

    [Fact]
    public async Task GetOrFetchAsync_ExpiresAndDecrementsAccountingExactlyOnce()
    {
        var coalescer = new NzbFetchCoalescer(
            maxCacheBytes: 1024,
            maxCacheEntries: 8,
            cacheTtl: TimeSpan.FromMilliseconds(30));

        await coalescer.GetOrFetchAsync(
            "http://nzb/ttl", null, false,
            _ => Task.FromResult<byte[]?>(new byte[5]),
            CancellationToken.None);
        Assert.Equal(5, coalescer.CurrentCachedBytes);

        await Task.Delay(50);

        var calls = 0;
        var refreshed = await coalescer.GetOrFetchAsync(
            "http://nzb/ttl", null, false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<byte[]?>(new byte[5]);
            },
            CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal(1, calls);
        Assert.Equal(5, coalescer.CurrentCachedBytes);
        Assert.Equal(1, coalescer.CachedEntryCount);
    }
}

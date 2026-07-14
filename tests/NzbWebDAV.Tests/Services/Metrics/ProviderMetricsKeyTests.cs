using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services.Metrics;

public class ProviderMetricsKeyTests
{
    [Fact]
    public void Tracker_IsolatesSameHostAccountsByProviderKey()
    {
        var tracker = new ProviderBytesTracker();
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");

        tracker.Add(a, 1000);
        tracker.Add(b, 250);

        Assert.Equal(1000, tracker.GetLifetime(a));
        Assert.Equal(250, tracker.GetLifetime(b));
        Assert.Equal(1250, tracker.LifetimeAll);
    }

    [Fact]
    public void ComputeUsage_AndIsOverLimit_AreIndependentPerProviderId()
    {
        var tracker = new ProviderBytesTracker();
        var providerA = MakeProvider("news.example.com", "user-a", byteLimit: 1000);
        var providerB = MakeProvider("news.example.com", "user-b", byteLimit: 1000);
        var keyA = UsenetProviderIdentity.MetricsKey(providerA);
        var keyB = UsenetProviderIdentity.MetricsKey(providerB);

        tracker.SetLifetime(keyA, 960); // over 95% effective limit
        tracker.SetLifetime(keyB, 100);

        Assert.Equal(960, ProviderUsageHelper.ComputeUsage(tracker, providerA));
        Assert.Equal(100, ProviderUsageHelper.ComputeUsage(tracker, providerB));
        Assert.True(ProviderUsageHelper.IsOverLimit(tracker, providerA));
        Assert.False(ProviderUsageHelper.IsOverLimit(tracker, providerB));
    }

    [Fact]
    public void EnsureProviderIds_AssignsMissingGuidsOnly()
    {
        var existing = Guid.NewGuid();
        var config = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider("news.example.com", "a", providerId: existing),
                MakeProvider("news.example.com", "b", providerId: Guid.Empty),
            ],
        };

        Assert.True(UsenetProviderIdentity.EnsureProviderIds(config));
        Assert.Equal(existing, config.Providers[0].ProviderId);
        Assert.NotEqual(Guid.Empty, config.Providers[1].ProviderId);
        Assert.False(UsenetProviderIdentity.EnsureProviderIds(config));
    }

    [Fact]
    public void NormalizeProviderIdsOnSave_PreservesMatchAndCreatesMissing()
    {
        var existingId = Guid.NewGuid();
        var existing = new UsenetProviderConfig
        {
            Providers = [MakeProvider("news.example.com", "alice", port: 563, providerId: existingId)],
        };
        var incoming = new UsenetProviderConfig
        {
            Providers =
            [
                MakeProvider("news.example.com", "alice", port: 563, providerId: Guid.Empty),
                MakeProvider("news.example.com", "bob", port: 563, providerId: Guid.Empty),
            ],
        };

        UsenetProviderIdentity.NormalizeProviderIdsOnSave(incoming, existing);

        Assert.Equal(existingId, incoming.Providers[0].ProviderId);
        Assert.NotEqual(Guid.Empty, incoming.Providers[1].ProviderId);
        Assert.NotEqual(existingId, incoming.Providers[1].ProviderId);
    }

    [Fact]
    public async Task SeedTrackerAsync_SeedsEveryProviderWithoutHostDedup()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var providerA = MakeProvider("news.example.com", "a");
        var providerB = MakeProvider("news.example.com", "b");
        var keyA = UsenetProviderIdentity.MetricsKey(providerA);
        var keyB = UsenetProviderIdentity.MetricsKey(providerB);
        var hour = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        hour -= hour % 3_600_000;

        harness.Context.ProviderHourly.AddRange(
            new ProviderHourly { Hour = hour, Provider = keyA, BytesFetched = 111 },
            new ProviderHourly { Hour = hour, Provider = keyB, BytesFetched = 222 });
        await harness.Context.SaveChangesAsync();

        var tracker = new ProviderBytesTracker();
        await ProviderUsageHelper.SeedTrackerAsync(
            tracker,
            new UsenetProviderConfig { Providers = [providerA, providerB] },
            () => harness.CreateContext());

        Assert.Equal(111, tracker.GetLifetime(keyA));
        Assert.Equal(222, tracker.GetLifetime(keyB));
    }

    [Fact]
    public async Task Remap_AttributesHostRowsToFirstSameHostProvider()
    {
        await using var harness = await MetricsHarness.CreateAsync();
        var first = MakeProvider("news.example.com", "first");
        var second = MakeProvider("news.example.com", "second");
        var firstKey = UsenetProviderIdentity.MetricsKey(first);
        var secondKey = UsenetProviderIdentity.MetricsKey(second);
        var hour = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        hour -= hour % 3_600_000;

        harness.Context.ProviderHourly.Add(new ProviderHourly
        {
            Hour = hour,
            Provider = "news.example.com",
            BytesFetched = 5000,
            Articles = 10,
        });
        // Pre-existing id-keyed row in the same hour to exercise merge-on-conflict.
        harness.Context.ProviderHourly.Add(new ProviderHourly
        {
            Hour = hour,
            Provider = firstKey,
            BytesFetched = 100,
            Articles = 1,
        });
        await harness.Context.SaveChangesAsync();

        await UsenetProviderIdentity.RemapHostKeyedMetricsAsync(
            new UsenetProviderConfig { Providers = [first, second] },
            harness.Context);

        await using var verify = harness.CreateContext();
        var firstRow = await verify.ProviderHourly.SingleAsync(x => x.Provider == firstKey && x.Hour == hour);
        Assert.Equal(5100, firstRow.BytesFetched);
        Assert.Equal(11, firstRow.Articles);
        Assert.False(await verify.ProviderHourly.AnyAsync(x => x.Provider == "news.example.com"));
        Assert.False(await verify.ProviderHourly.AnyAsync(x => x.Provider == secondKey));
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(
        string host,
        string user,
        int port = 563,
        long? byteLimit = null,
        Guid? providerId = null)
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            ProviderId = providerId ?? Guid.NewGuid(),
            Type = ProviderType.Pooled,
            Host = host,
            Port = port,
            UseSsl = true,
            User = user,
            Pass = "pass",
            MaxConnections = 10,
            ByteLimit = byteLimit,
        };
    }

    private sealed class MetricsHarness : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly DbContextOptions<MetricsDbContext> _options;

        private MetricsHarness(string dir, DbContextOptions<MetricsDbContext> options, MetricsDbContext context)
        {
            _dir = dir;
            _options = options;
            Context = context;
        }

        public MetricsDbContext Context { get; }

        public MetricsDbContext CreateContext() => new(_options);

        public static async Task<MetricsHarness> CreateAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"nzbdav-metrics-key-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metrics.sqlite");
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            var context = new MetricsDbContext(options);
            await context.Database.MigrateAsync();
            return new MetricsHarness(dir, options, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}

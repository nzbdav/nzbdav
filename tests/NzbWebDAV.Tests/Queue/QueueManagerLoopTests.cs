using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Queue;

public class QueueManagerLoopTests
{
    [Fact]
    public async Task ProcessQueueAsync_BacksOffOnPersistentFetchErrors()
    {
        using var manager = CreateManager();
        var iterations = 0;
        manager.ErrorBackoffDelay = TimeSpan.FromSeconds(5);
        manager.GetTopQueueItemOverride = (_, _) =>
        {
            Interlocked.Increment(ref iterations);
            throw new InvalidOperationException("persistent db failure");
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await manager.ProcessQueueAsync(cts.Token);

        // Without backoff this would spin dozens of times; with 5s backoff, ≤2 in 8s.
        Assert.InRange(iterations, 1, 2);
    }

    [Fact]
    public async Task ProcessQueueAsync_WakesBeforeIdleDelayWhenPauseExpires()
    {
        using var manager = CreateManager();
        manager.IdleDelay = TimeSpan.FromSeconds(30);

        var pollTimes = new List<DateTime>();
        var pauseCalls = 0;
        manager.GetTopQueueItemOverride = (_, _) =>
        {
            lock (pollTimes) pollTimes.Add(DateTime.UtcNow);
            return Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));
        };
        manager.GetNextPauseUntilOverride = _ =>
        {
            var call = Interlocked.Increment(ref pauseCalls);
            return Task.FromResult<DateTime?>(
                call == 1 ? DateTime.Now.AddSeconds(2) : null);
        };

        using var cts = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(cts.Token);

        // Wait until we've seen a second top-item poll (pause-aware wake), then stop.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            lock (pollTimes)
            {
                if (pollTimes.Count >= 2) break;
            }
            await Task.Delay(50);
        }

        await cts.CancelAsync();
        await loop;

        DateTime first;
        DateTime second;
        lock (pollTimes)
        {
            Assert.True(pollTimes.Count >= 2, $"Expected ≥2 polls, got {pollTimes.Count}");
            first = pollTimes[0];
            second = pollTimes[1];
        }

        var gap = second - first;
        Assert.True(gap < TimeSpan.FromSeconds(5),
            $"Second poll should wake within ~5s of pause expiry, took {gap.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task ProcessQueueAsync_ExitsIdleSleepPromptlyOnShutdown()
    {
        using var manager = CreateManager();
        manager.IdleDelay = TimeSpan.FromMinutes(1);
        manager.GetTopQueueItemOverride = (_, _) =>
            Task.FromResult<(QueueItem? queueItem, Stream? queueNzbStream)>((null, null));

        using var cts = new CancellationTokenSource();
        var loop = manager.ProcessQueueAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        var completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(loop, completed);
        await loop; // observe completion / no fault
    }

    private static QueueManager CreateManager()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);

        var usenet = new UsenetStreamingClient(
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

        return new QueueManager(
            usenet,
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }
}

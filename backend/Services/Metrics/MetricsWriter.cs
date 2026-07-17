using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Buffered, asynchronous writer for the metrics database. Producers call the
/// non-blocking Record* methods from any thread; rows accumulate in lock-free
/// queues and a background loop flushes them in batches.
///
/// Flush triggers: every 5 s OR when any queue exceeds 1000 entries. All
/// inserts for one tick happen inside a single transaction so we pay one fsync
/// (relaxed by synchronous=NORMAL anyway) for the whole batch.
///
/// Drop policy: if a queue grows past MaxQueueLength (10 000) new entries are
/// dropped to protect the process. Drops are counted on the public Stats so
/// the dashboard can surface metric-system health.
/// </summary>
public class MetricsWriter : BackgroundService
{
    private const int FlushThreshold = 1000;
    private const int MaxQueueLength = 10_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<SegmentFetch> _fetches = new();
    private readonly ConcurrentQueue<MetricEvent> _events = new();
    private readonly ConcurrentQueue<ReadSession> _sessions = new();
    private readonly ConcurrentQueue<FailoverMiss> _failoverMisses = new();
    private readonly Func<MetricsDbContext> _contextFactory;

    private long _droppedFetches;
    private long _droppedEvents;
    private long _droppedSessions;
    private long _droppedFailoverMisses;
    private long _lastFlushLagMs;
    private long _lastSuccessfulFlushAtMs;
    private string? _lastFlushError;

    public MetricsWriter() : this(static () => new MetricsDbContext())
    {
    }

    internal MetricsWriter(Func<MetricsDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public MetricsStats Stats => new(
        QueuedFetches: _fetches.Count,
        QueuedEvents: _events.Count,
        QueuedSessions: _sessions.Count,
        QueuedFailoverMisses: _failoverMisses.Count,
        DroppedFetches: Interlocked.Read(ref _droppedFetches),
        DroppedEvents: Interlocked.Read(ref _droppedEvents),
        DroppedSessions: Interlocked.Read(ref _droppedSessions),
        DroppedFailoverMisses: Interlocked.Read(ref _droppedFailoverMisses),
        LastFlushLagMs: Interlocked.Read(ref _lastFlushLagMs),
        LastSuccessfulFlushAtMs: Interlocked.Read(ref _lastSuccessfulFlushAtMs),
        LastFlushError: Volatile.Read(ref _lastFlushError)
    );

    public void RecordFetch(SegmentFetch f)
    {
        if (_fetches.Count >= MaxQueueLength)
        {
            Interlocked.Increment(ref _droppedFetches);
            return;
        }
        _fetches.Enqueue(f);
    }

    public void RecordEvent(MetricEvent e)
    {
        if (_events.Count >= MaxQueueLength)
        {
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        _events.Enqueue(e);
    }

    public void RecordSession(ReadSession s)
    {
        if (_sessions.Count >= MaxQueueLength)
        {
            Interlocked.Increment(ref _droppedSessions);
            return;
        }
        _sessions.Enqueue(s);
    }

    public void RecordFailoverMiss(FailoverMiss m)
    {
        if (_failoverMisses.Count >= MaxQueueLength)
        {
            Interlocked.Increment(ref _droppedFailoverMisses);
            return;
        }
        _failoverMisses.Enqueue(m);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForFlushAsync(stoppingToken).ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MetricsWriter flush failed");
            }
        }

        // Best-effort drain on shutdown so we don't lose the trailing batch.
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Debug(ex, "MetricsWriter final flush failed"); }
    }

    private async Task WaitForFlushAsync(CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow + FlushInterval;
        while (DateTime.UtcNow < deadline)
        {
            if (_fetches.Count >= FlushThreshold ||
                _events.Count >= FlushThreshold ||
                _sessions.Count >= FlushThreshold ||
                _failoverMisses.Count >= FlushThreshold)
                return;
            await Task.Delay(100, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync()
    {
        var fetches = Drain(_fetches);
        var events = Drain(_events);
        var sessions = Drain(_sessions);
        var failoverMisses = Drain(_failoverMisses);
        if (fetches.Count == 0 && events.Count == 0 && sessions.Count == 0 && failoverMisses.Count == 0) return;

        var started = DateTime.UtcNow;
        try
        {
            await using var db = _contextFactory();
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

            if (fetches.Count > 0) db.SegmentFetches.AddRange(fetches);
            if (events.Count > 0) db.MetricEvents.AddRange(events);
            if (sessions.Count > 0) db.ReadSessions.AddRange(sessions);
            if (failoverMisses.Count > 0) db.FailoverMisses.AddRange(failoverMisses);

            await db.SaveChangesAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);

            var completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _lastFlushLagMs, (long)(DateTime.UtcNow - started).TotalMilliseconds);
            Interlocked.Exchange(ref _lastSuccessfulFlushAtMs, completed);
            Interlocked.Exchange(ref _lastFlushError, null);
        }
        catch (Exception ex)
        {
            Requeue(_fetches, fetches);
            Requeue(_events, events);
            Requeue(_sessions, sessions);
            Requeue(_failoverMisses, failoverMisses);
            Interlocked.Exchange(ref _lastFlushError, ex.GetBaseException().Message);
            throw;
        }
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> q)
    {
        var list = new List<T>(Math.Min(q.Count, FlushThreshold * 2));
        while (q.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    private static void Requeue<T>(ConcurrentQueue<T> queue, IEnumerable<T> items)
    {
        foreach (var item in items)
            queue.Enqueue(item);
    }

    internal Task FlushNowAsync() => FlushAsync();

    /// <summary>
    /// Drops all queued-but-unflushed rows and zeroes the drop/flush health
    /// counters. Used by the overview-stats reset so stale rows don't reappear
    /// on the next flush tick.
    /// </summary>
    public void DiscardQueuedAndResetStats()
    {
        while (_fetches.TryDequeue(out _)) { }
        while (_events.TryDequeue(out _)) { }
        while (_sessions.TryDequeue(out _)) { }
        while (_failoverMisses.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _droppedFetches, 0);
        Interlocked.Exchange(ref _droppedEvents, 0);
        Interlocked.Exchange(ref _droppedSessions, 0);
        Interlocked.Exchange(ref _droppedFailoverMisses, 0);
        Volatile.Write(ref _lastFlushError, null);
    }

    /// <summary>
    /// Drops queued rows belonging to one provider (fetches and failover misses;
    /// events and sessions are not provider-keyed). Drop counters are untouched.
    /// </summary>
    public void DiscardQueuedForProvider(string providerKey)
    {
        FilterQueue(_fetches, f => !string.Equals(f.Provider, providerKey, StringComparison.Ordinal));
        FilterQueue(_failoverMisses, m =>
            !string.Equals(m.FromProvider, providerKey, StringComparison.Ordinal)
            && !string.Equals(m.ToProvider, providerKey, StringComparison.Ordinal));
    }

    private static void FilterQueue<T>(ConcurrentQueue<T> queue, Func<T, bool> keep)
    {
        // One pass over the current length: concurrent enqueues during the loop
        // are newer entries and are simply kept.
        var count = queue.Count;
        for (var i = 0; i < count; i++)
        {
            if (!queue.TryDequeue(out var item)) break;
            if (keep(item)) queue.Enqueue(item);
        }
    }

    public record MetricsStats(
        int QueuedFetches,
        int QueuedEvents,
        int QueuedSessions,
        int QueuedFailoverMisses,
        long DroppedFetches,
        long DroppedEvents,
        long DroppedSessions,
        long DroppedFailoverMisses,
        long LastFlushLagMs,
        long LastSuccessfulFlushAtMs,
        string? LastFlushError
    );
}

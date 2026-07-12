using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for delegating NNTP commands to a connection pool.
///   * The connection pool enforces a maximum number of allowed connections
///   * When a connection is available, the NNTP command executes immediately
///   * When a connection is not available, the NNTP command waits until a connection becomes available.
///   * When multiple commands are awaiting a connection,
///     then BODY/ARTICLE commands have higher priority than STAT/HEAD/DATE commands.
/// </summary>
/// <param name="connectionPool"></param>
/// <param name="type"></param>
/// <param name="circuitBreaker"></param>
/// <param name="providerName"></param>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class MultiConnectionNntpClient(
    ConnectionPool<INntpClient> connectionPool,
    ProviderType type,
    ProviderCircuitBreaker circuitBreaker,
    string providerName,
    long? byteLimit = null,
    long bytesUsedOffset = 0,
    int priority = 0,
    int? pipeliningDepth = null
) : NntpClient
{
    public ProviderType ProviderType { get; } = type;
    public int Priority { get; } = priority;
    public string Host { get; } = providerName;

    private static readonly ConcurrentDictionary<string, int> TimeoutCounts = new();
    private static long _lastTimeoutFlushTicks = DateTime.UtcNow.Ticks;

    private static void IncrementTimeoutCount(string provider)
    {
        TimeoutCounts.AddOrUpdate(provider, 1, (_, existing) => existing + 1);
        MaybeFlushTimeoutCounts();
    }

    private static void MaybeFlushTimeoutCounts()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastTimeoutFlushTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(60).Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastTimeoutFlushTicks, nowTicks, last) != last)
            return;

        foreach (var key in TimeoutCounts.Keys)
        {
            if (TimeoutCounts.TryRemove(key, out var count) && count > 0)
                Log.Warning("[{ProviderName}] {Count} NNTP timeouts in the last 60 seconds", key, count);
        }
    }

    public int? ConfiguredPipeliningDepth { get; } = pipeliningDepth;
    // null or non-positive = uncapped. Routing reads these to decide whether
    // this provider should be skipped when it has exhausted its block.
    public long? ByteLimit { get; } = byteLimit;
    public long BytesUsedOffset { get; } = bytesUsedOffset;
    public bool IsTripped => circuitBreaker.IsTripped;
    public int LiveConnections => connectionPool.LiveConnections;
    public int IdleConnections => connectionPool.IdleConnections;
    public int ActiveConnections => connectionPool.ActiveConnections;
    public int AvailableConnections => connectionPool.AvailableConnections;

    private int _pendingSelections;
    public int PendingSelections => Volatile.Read(ref _pendingSelections);
    public void ReservePending() => Interlocked.Increment(ref _pendingSelections);
    public void ReleasePending() => Interlocked.Decrement(ref _pendingSelections);

    public bool HasSpareConnection => AvailableConnections - PendingSelections > 0;

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "STAT",
            SemaphorePriority.Low,
            (connection, _) => connection.StatAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "HEAD",
            SemaphorePriority.Low,
            (connection, _) => connection.HeadAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
    {
        return RunWithConnection(
            "DATE",
            SemaphorePriority.Low,
            (connection, _) => connection.DateAsync(ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        var retryCount = 1;
        while (true)
        {
            ConnectionLock<INntpClient>? connectionLock = null;
            var deferredCallback = new DeferredArticleBodyCallback();
            try
            {
                connectionLock = await connectionPool
                    .GetConnectionLockAsync(SemaphorePriority.High, ct)
                    .ConfigureAwait(false);
                var batch = await connectionLock.Connection.DecodedBodiesAsync(
                    segmentIds, deferredCallback.Invoke, ct).ConfigureAwait(false);

                var callbackInvoked = 0;
                deferredCallback.Activate(OnConnectionReadyAgain);
                return batch;

                void OnConnectionReadyAgain(ArticleBodyResult result)
                {
                    if (Interlocked.Exchange(ref callbackInvoked, 1) != 0) return;
                    switch (result)
                    {
                        case ArticleBodyResult.Retrieved:
                        case ArticleBodyResult.NotFound:
                            circuitBreaker.RecordSuccess();
                            break;
                        case ArticleBodyResult.Cancelled:
                            break;
                        default:
                            circuitBreaker.RecordFailure();
                            LogException(connectionLock.Replace);
                            break;
                    }

                    LogException(connectionLock.Dispose);
                    LogException(() => onConnectionReadyAgain?.Invoke(result));
                }
            }
            catch (Exception e) when (e.IsCancellationException(ct))
            {
                deferredCallback.Discard();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                deferredCallback.Discard();
                var wasReused = connectionLock?.WasReused ?? false;
                if (!wasReused) circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());

                // A pooled connection may have been closed server-side while idle;
                // its failure says nothing about provider health. Drain and retry.
                if (wasReused)
                {
                    Log.Debug(e,
                        "Pooled connection for provider {Provider} failed pipelined NNTP BODY commands. Retrying with another connection.",
                        providerName);
                    continue;
                }

                if (retryCount > 0)
                {
                    Log.Debug(e,
                        "Error executing pipelined NNTP BODY commands for provider {Provider}. Retrying with a new connection.",
                        providerName);
                    retryCount--;
                    continue;
                }

                Log.Warning(e,
                    "Error executing pipelined NNTP BODY commands for provider {Provider}.",
                    providerName);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
        }
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    private async Task<T> RunWithConnection<T>
    (
        string name,
        SemaphorePriority priority,
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> command,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct,
        int retryCount = 1
    ) where T : UsenetResponse
    {
        while (true)
        {
            ConnectionLock<INntpClient>? connectionLock = null;
            try
            {
                connectionLock = await connectionPool.GetConnectionLockAsync(priority, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsCancellationException(ct))
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    Log.Debug(e, "Error getting connection-lock for provider {Provider}. Retrying with a new connection.", providerName);
                    retryCount--;
                    continue;
                }

                Log.Warning(e, "Error getting connection-lock for provider {Provider}.", providerName);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            T? result;
            var deferredCallback = new DeferredArticleBodyCallback();
            try
            {
                result = await command(connectionLock.Connection, deferredCallback.Invoke)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsCancellationException(ct))
            {
                deferredCallback.Discard();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException _))
            {
                deferredCallback.Discard();
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (name is "BODY" or "ARTICLE" && e.TryGetCausingException(out TimeoutException _))
            {
                // Read-timeout on BODY/ARTICLE means the provider stopped responding
                // mid-command. A fresh socket to the same provider is unlikely to fare
                // any better, and burning another timeout retrying here just doubles
                // the wait before MultiProviderNntpClient can fall over to the next
                // provider. Replace the socket (the read may have left partial bytes
                // on the wire) and propagate so the outer provider loop moves on.
                IncrementTimeoutCount(providerName);
                deferredCallback.Discard();
                circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                deferredCallback.Discard();
                var wasReused = connectionLock?.WasReused ?? false;
                if (!wasReused) circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());

                // A pooled connection may have been closed server-side while idle;
                // its failure says nothing about provider health. Drain and retry.
                if (wasReused)
                {
                    Log.Debug(e, "Pooled connection for provider {Provider} failed nntp {Command} command. Retrying with another connection.", providerName, name);
                    continue;
                }

                if (retryCount > 0)
                {
                    Log.Debug(e, "Error executing nntp {Command} command for provider {Provider}. Retrying with a new connection.", name, providerName);
                    retryCount--;
                    continue;
                }

                Log.Warning(e, "Error executing nntp {Command} command for provider {Provider}.", name, providerName);
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            // stat, head, and date
            if (name is "STAT" or "HEAD" or "DATE")
            {
                circuitBreaker.RecordSuccess();
                deferredCallback.Discard();
                LogException(() => connectionLock?.Dispose());
            }

            // body and article
            else if ((result?.Success ?? false) == false)
            {
                circuitBreaker.RecordSuccess();
                deferredCallback.Discard();
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
            }
            else
            {
                var callbackInvoked = 0;
                deferredCallback.Activate(articleBodyResult =>
                {
                    if (Interlocked.Exchange(ref callbackInvoked, 1) != 0) return;

                    if (articleBodyResult == ArticleBodyResult.NotRetrieved)
                    {
                        circuitBreaker.RecordFailure();
                        LogException(() => connectionLock?.Replace());
                    }
                    else if (articleBodyResult == ArticleBodyResult.Retrieved)
                    {
                        circuitBreaker.RecordSuccess();
                    }

                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(articleBodyResult));
                });
            }

            return result!;
        }
    }

    public override IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.StatsPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    private async IAsyncEnumerable<T> RunPipelinedAsync<T>(
        Func<INntpClient, IAsyncEnumerable<T>> batchFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var priority = GetDownloadPriority(cancellationToken);
        var connectionLock = await connectionPool.GetConnectionLockAsync(priority, cancellationToken).ConfigureAwait(false);
        var completed = false;
        try
        {
            await using var enumerator = batchFactory(connectionLock.Connection)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                T current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        completed = true;
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception e) when (!e.IsCancellationException())
                {
                    circuitBreaker.RecordFailure();
                    connectionLock.Replace();
                    throw;
                }

                circuitBreaker.RecordSuccess();
                yield return current;
            }
        }
        finally
        {
            if (!completed) connectionLock.Replace();
            connectionLock.Dispose();
        }
    }

    private static SemaphorePriority GetDownloadPriority(CancellationToken ct)
    {
        return ct.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
    }

    private static void LogException(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled exception");
        }
    }

    public override void Dispose()
    {
        connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}

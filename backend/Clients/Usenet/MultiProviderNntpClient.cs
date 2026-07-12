using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker? usageTracker = null,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    Func<bool>? cascadeEnabled = null
) : NntpClient
{
    private readonly ProviderUsageTracker _usageTracker = usageTracker ?? new ProviderUsageTracker();
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();
    internal static Guid? CurrentReadSessionId => ReadSessionScope.Value;

    /// <summary>
    /// Tag the current async flow with a read-session id so SegmentFetch rows
    /// emitted while fulfilling this read can be correlated back to the session.
    /// Disposing the returned scope restores the previous value.
    /// </summary>
    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        return new ScopeReleaser(() => ReadSessionScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    // Per-call attribution. Caller (e.g. PlaybackFastVerifier) sets a mutable
    // holder on AttributionContext BEFORE invoking; we read it inside the call and
    // mutate Host on a non-"missing" response. AsyncLocal reliably flows the holder
    // reference DOWN to us; mutating its property is then visible to the caller via
    // their reference (which sidesteps AsyncLocal's child→parent non-propagation).
    public sealed class ResponderAttribution { public string? Host; }
    public static readonly AsyncLocal<ResponderAttribution?> AttributionContext = new();

    private readonly object _selectLock = new();

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return await RunStreamingFromPoolWithBackup(
            (provider, callback) =>
                provider.DecodedBodyAsync(segmentId, callback, cancellationToken),
            UsenetResponseType.ArticleRetrievedBodyFollows,
            onConnectionReadyAgain,
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        for (var providerIndex = 0; providerIndex < orderedProviders.Count; providerIndex++)
        {
            var provider = orderedProviders[providerIndex];
            var deferredCallback = new DeferredArticleBodyCallback();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primaryBatch = await provider.DecodedBodiesAsync(
                    segmentIds, deferredCallback.Invoke, cancellationToken).ConfigureAwait(false);
                var coordinator = new BatchCallbackCoordinator(
                    primaryBatch.Responses.Count, onConnectionReadyAgain);
                deferredCallback.Activate(coordinator.CompleteTransfer);
                var fallbackProviders = orderedProviders
                    .Skip(providerIndex + 1)
                    .ToArray();
                var responses =
                    new Task<UsenetDecodedBodyResponse>[primaryBatch.Responses.Count];
                Task previousFallbackCompletion = Task.CompletedTask;
                for (var index = 0; index < responses.Length; index++)
                {
                    var fallbackCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    responses[index] = ResolveBatchResponseAsync(
                        primaryBatch.Responses[index],
                        segmentIds[index],
                        provider,
                        fallbackProviders,
                        previousFallbackCompletion,
                        fallbackCompletion,
                        coordinator,
                        cancellationToken);
                    previousFallbackCompletion = fallbackCompletion.Task;
                }
                return new UsenetDecodedBodyBatch { Responses = responses };
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                deferredCallback.Discard();
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch
            {
                deferredCallback.Discard();
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        InvokeCompletionCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private async Task<UsenetDecodedBodyResponse> ResolveBatchResponseAsync(
        Task<UsenetDecodedBodyResponse> primaryResponse,
        SegmentId segmentId,
        MultiConnectionNntpClient primaryProvider,
        IReadOnlyList<MultiConnectionNntpClient> fallbackProviders,
        Task previousFallbackCompletion,
        TaskCompletionSource fallbackCompletion,
        BatchCallbackCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var fallbackCompletionOwnedByTransfer = false;
        var primaryStopwatch = Stopwatch.StartNew();
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        try
        {
            UsenetDecodedBodyResponse? response = null;
            ExceptionDispatchInfo? lastException = null;
            try
            {
                response = await primaryResponse.ConfigureAwait(false);
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                primaryStopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(primaryProvider.Host, reason, primaryStopwatch.ElapsedMilliseconds, 0);
                (priorMisses ??= []).Add((primaryProvider.Host, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }

            if (response?.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                primaryStopwatch.Stop();
                _usageTracker.RecordSuccess(primaryProvider.Host);
                RecordFetch(primaryProvider.Host, SegmentFetch.FetchStatus.Ok,
                    primaryStopwatch.ElapsedMilliseconds, 0);
                return WrapStreamForByteCounting(response, primaryProvider.Host);
            }

            if (response?.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
            {
                primaryStopwatch.Stop();
                RecordFetch(primaryProvider.Host, SegmentFetch.FetchStatus.Missing,
                    primaryStopwatch.ElapsedMilliseconds, 0);
                (priorMisses ??= []).Add((primaryProvider.Host, SegmentFetch.FetchStatus.Missing));
            }

            // Retry the primary provider once before falling back. Even a clean 430 can
            // be transient when a provider routes requests across different spool nodes.
            // Anything else (a faulted response task, or a stale connection's buffered
            // goodbye line such as "400 idle timeout") remains a connection-level failure.
            IReadOnlyList<MultiConnectionNntpClient> retryProviders =
                [primaryProvider, .. fallbackProviders];
            if (response?.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
            {
                if (response != null)
                {
                    lastException = ExceptionDispatchInfo.Capture(
                        new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage));
                }
            }

            await previousFallbackCompletion.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var provider in retryProviders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coordinator.AddTransfer();
                var deferredCallback = new DeferredArticleBodyCallback();
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    response = await provider.DecodedBodyAsync(
                        segmentId, deferredCallback.Invoke, cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    var responseType = response.ResponseType;
                    if (responseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                    {
                        _usageTracker.RecordSuccess(provider.Host);
                        RecordFetch(provider.Host, SegmentFetch.FetchStatus.Ok,
                            stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0);
                        if (priorMisses is { Count: > 0 })
                        {
                            _usageTracker.RecordFailoverSave();
                            RecordFailoverMisses(priorMisses, provider.Host);
                        }
                        response = WrapStreamForByteCounting(response, provider.Host);
                        fallbackCompletionOwnedByTransfer = true;
                        deferredCallback.Activate(result =>
                        {
                            try
                            {
                                coordinator.CompleteTransfer(result);
                            }
                            finally
                            {
                                fallbackCompletion.TrySetResult();
                            }
                        });
                    }
                    else
                    {
                        RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing,
                            stopwatch.ElapsedMilliseconds, priorMisses?.Count ?? 0);
                        (priorMisses ??= []).Add((provider.Host, SegmentFetch.FetchStatus.Missing));
                        deferredCallback.Discard();
                        coordinator.CompleteAttempt();
                    }

                    lastException = null;
                }
                catch (Exception e) when (!e.IsCancellationException(cancellationToken))
                {
                    stopwatch.Stop();
                    var reason = ClassifyException(e);
                    RecordFetch(provider.Host, reason, stopwatch.ElapsedMilliseconds,
                        priorMisses?.Count ?? 0);
                    (priorMisses ??= []).Add((provider.Host, reason));
                    deferredCallback.Discard();
                    coordinator.CompleteAttempt();
                    lastException = ExceptionDispatchInfo.Capture(e);
                    continue;
                }
                catch
                {
                    deferredCallback.Discard();
                    coordinator.CompleteAttempt();
                    throw;
                }

                if (response.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    return response;
                }
            }

            lastException?.Throw();
            throw new UsenetArticleNotFoundException(segmentId, response?.ResponseMessage);
        }
        catch
        {
            coordinator.MarkResolutionFailure();
            throw;
        }
        finally
        {
            if (!fallbackCompletionOwnedByTransfer)
            {
                fallbackCompletion.TrySetResult();
            }

            coordinator.CompleteDecision();
        }
    }

    private sealed class BatchCallbackCoordinator(
        int responseCount,
        Action<ArticleBodyResult>? callback)
    {
        private int _remaining = responseCount + 1;
        private int _transportFailed;
        private int _resolutionFailed;
        private int _callbackInvoked;

        public void AddTransfer()
        {
            Interlocked.Increment(ref _remaining);
        }

        public void CompleteTransfer(ArticleBodyResult result)
        {
            if (result == ArticleBodyResult.NotRetrieved)
            {
                Volatile.Write(ref _transportFailed, 1);
            }
            else if (result == ArticleBodyResult.Cancelled)
            {
                MarkResolutionFailure();
            }

            CompleteOne();
        }

        public void CompleteDecision()
        {
            CompleteOne();
        }

        public void CompleteAttempt()
        {
            CompleteOne();
        }

        public void MarkResolutionFailure()
        {
            Volatile.Write(ref _resolutionFailed, 1);
        }

        private void CompleteOne()
        {
            if (Interlocked.Decrement(ref _remaining) != 0 ||
                Interlocked.Exchange(ref _callbackInvoked, 1) != 0)
            {
                return;
            }

            InvokeCompletionCallback(
                callback,
                Volatile.Read(ref _transportFailed) == 0 &&
                Volatile.Read(ref _resolutionFailed) == 0
                    ? ArticleBodyResult.Retrieved
                    : ArticleBodyResult.NotRetrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return await RunStreamingFromPoolWithBackup(
            (provider, callback) =>
                provider.DecodedArticleAsync(segmentId, callback, cancellationToken),
            UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            onConnectionReadyAgain,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunStreamingFromPoolWithBackup<T>(
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> task,
        UsenetResponseType successResponseType,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
        where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        for (var index = 0; index < orderedProviders.Count; index++)
        {
            var provider = orderedProviders[index];
            var deferredCallback = new DeferredArticleBodyCallback();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await task(provider, deferredCallback.Invoke)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                if (result.ResponseType == successResponseType)
                {
                    if (attribution != null) attribution.Host = provider.Host;
                    _usageTracker.RecordSuccess(provider.Host);
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Ok,
                        stopwatch.ElapsedMilliseconds, index);
                    if (index > 0)
                    {
                        _usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, provider.Host);
                    }
                    result = WrapStreamForByteCounting(result, provider.Host);
                    deferredCallback.Activate(onConnectionReadyAgain ?? (_ => { }));
                    return result;
                }

                deferredCallback.Discard();
                if (result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId &&
                    index < orderedProviders.Count - 1)
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing,
                        stopwatch.ElapsedMilliseconds, index);
                    (priorMisses ??= []).Add((provider.Host, SegmentFetch.FetchStatus.Missing));
                    continue;
                }

                RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing,
                    stopwatch.ElapsedMilliseconds, index);
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                return result;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.Host, reason, stopwatch.ElapsedMilliseconds, index);
                (priorMisses ??= []).Add((provider.Host, reason));
                deferredCallback.Discard();
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch
            {
                deferredCallback.Discard();
                InvokeCompletionCallback(
                    onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
                throw;
            }
        }

        InvokeCompletionCallback(onConnectionReadyAgain, ArticleBodyResult.NotRetrieved);
        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var failedProvider = orderedProviders[i - 1];
                var msg = lastException.SourceException.Message;
                Log.Information(
                    "Provider {FailedProvider} error: {ErrorMessage}. Falling back to {NextProvider}",
                    failedProvider.Host,
                    msg,
                    provider.Host);
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    (priorMisses ??= new()).Add((provider.Host, SegmentFetch.FetchStatus.Missing));
                    continue;
                }

                // attribute the response to this provider, unless it was a "missing" hit
                // from the last provider (in which case nobody actually answered).
                if (attribution != null && result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    attribution.Host = provider.Host;

                // record per-queue-item attribution only for bytes-bearing responses (BODY/ARTICLE).
                if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse
                    && result.ResponseType is UsenetResponseType.ArticleRetrievedBodyFollows
                                          or UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    _usageTracker.RecordSuccess(provider.Host);
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                    if (i > 0)
                    {
                        _usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.Host);
                    }
                    result = WrapStreamForByteCounting(result, provider.Host);
                }
                else
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                }

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException(cancellationToken))
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.Host, reason, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Host, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (lastException is not null)
        {
            Log.Warning(
                "All providers exhausted. Last error from {Provider}: {ErrorMessage}",
                orderedProviders[^1].Host,
                lastException.SourceException.Message);
            lastException.Throw();
        }
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        if (metricsWriter == null) return;
        metricsWriter.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = host,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        });
    }

    private void RecordFailoverMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses,
        string rescuer)
    {
        if (metricsWriter == null || priorMisses == null) return;
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (from, reason) in priorMisses)
        {
            metricsWriter.RecordFailoverMiss(new FailoverMiss
            {
                At = at,
                FromProvider = from,
                ToProvider = rescuer,
                Reason = reason,
            });
        }
    }

    private T WrapStreamForByteCounting<T>(T result, string host) where T : UsenetResponse
    {
        if (bytesTracker == null) return result;
        return result switch
        {
            UsenetDecodedBodyResponse b
                => (T)(object)(b with { Stream = new CountingYencStream(b.Stream, bytesTracker, host) }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with { Stream = new CountingYencStream(a.Stream, bytesTracker, host) }),
            _ => result,
        };
    }

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private List<MultiConnectionNntpClient> SelectOrderedProviders(out MultiConnectionNntpClient? reserved)
    {
        lock (_selectLock)
        {
            var enabled = providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => !IsOverLimit(x))
                .ToList();

            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            var pool = healthy.Count > 0 ? healthy : enabled;

            var byTier = pool.OrderBy(x => x.ProviderType);
            var prioritized = cascadeEnabled?.Invoke() == true
                ? byTier.ThenBy(EffectivePriority)
                : byTier;
            var ordered = prioritized
                .ThenByDescending(x => GetRemainingBytes(x))
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    private static int EffectivePriority(MultiConnectionNntpClient provider)
    {
        const int saturationDemotion = 1 << 20;
        return provider.Priority + (provider.HasSpareConnection ? 0 : saturationDemotion);
    }

    private double EstimatedDeliveryScore(MultiConnectionNntpClient provider)
    {
        var inFlight = provider.ActiveConnections + provider.PendingSelections + 1;
        var bytesPerMs = bytesTracker?.GetBytesPerMs(provider.Host) ?? 0d;
        return bytesPerMs > 0 ? inFlight / bytesPerMs : inFlight;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    private long GetRemainingBytes(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return long.MaxValue;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
        return Math.Max(0, limit.Value - used);
    }

    private static int ResolveDepth(MultiConnectionNntpClient primary, int fallbackDepth)
    {
        return primary.ConfiguredPipeliningDepth is int d and > 0
            ? Math.Clamp(d, 1, 64)
            : fallbackDepth;
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);

        await foreach (var result in primary.StatsPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);
        var stopwatch = Stopwatch.StartNew();

        await foreach (var result in primary.DecodedBodiesPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            stopwatch.Stop();
            if (result.Found)
            {
                _usageTracker.RecordSuccess(primary.Host);
                RecordFetch(primary.Host, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, 0);
                yield return WrapPipelinedBody(result, primary.Host);
            }
            else
            {
                RecordFetch(primary.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, 0);
                yield return result;
            }
            stopwatch.Restart();
        }
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);
        var stopwatch = Stopwatch.StartNew();

        await foreach (var result in primary.DecodedArticlesPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            stopwatch.Stop();
            if (result.Found)
            {
                _usageTracker.RecordSuccess(primary.Host);
                RecordFetch(primary.Host, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, 0);
                yield return WrapPipelinedArticle(result, primary.Host);
            }
            else
            {
                RecordFetch(primary.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, 0);
                yield return result;
            }
            stopwatch.Restart();
        }
    }

    private PipelinedBodyResult WrapPipelinedBody(PipelinedBodyResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
    }

    private PipelinedArticleResult WrapPipelinedArticle(PipelinedArticleResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
    }

    private static void InvokeCompletionCallback(
        Action<ArticleBodyResult>? callback,
        ArticleBodyResult result)
    {
        try
        {
            callback?.Invoke(result);
        }
        catch (Exception e)
        {
            Log.Warning(e, "NNTP completion callback failed");
        }
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int BodyPipelineBatchSize = 4;
    private const int MaxBodyRetries = 2;
    private const int MaxCorruptionRetries = 3;
    private const int MaxConsecutiveZeroFills = 3;

    private readonly Memory<string> _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly INntpClient _usenetClient;
    private readonly long _expectedSegmentSize;
    private readonly bool _failFastOnFirstSegment;
    private readonly string _fileName;
    private readonly Channel<Task<SegmentDownloadResult>> _streamTasks;
    private readonly int _bodyPipelineBatchSize;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly long? _readBudget;
    private Stream? _stream;
    private int _consecutiveZeroFills;
    private bool _disposed;

    public static Stream Create(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null)
    {
        return Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            expectedSegmentSize: 0,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            cancellationToken,
            fileName,
            readBudget,
            segmentFallbacks);
    }

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName = null,
        long? readBudget = null,
        string[][]? segmentFallbacks = null
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(
                segmentIds, usenetClient, expectedSegmentSize, fileName, segmentFallbacks)
            : new MultiSegmentStream(
                segmentIds,
                usenetClient,
                articleBufferSize,
                expectedSegmentSize,
                failFastOnFirstSegment,
                usePipelinedBodyRequests,
                cancellationToken,
                fileName,
                readBudget,
                segmentFallbacks);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken,
        string? fileName,
        long? readBudget,
        string[][]? segmentFallbacks
    )
    {
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _usenetClient = usenetClient;
        _expectedSegmentSize = expectedSegmentSize;
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
        _readBudget = readBudget ?? NzbWebDAV.WebDav.Requests.RangeContext.GetReadBudget();
        _bodyPipelineBatchSize = Math.Min(BodyPipelineBatchSize, articleBufferSize);
        _streamTasks = Channel.CreateBounded<Task<SegmentDownloadResult>>(articleBufferSize);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = DownloadSegments(usePipelinedBodyRequests, _cts.Token);
    }

    private async Task DownloadSegments(
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            if (usePipelinedBodyRequests)
                await DownloadPipelinedSegments(cancellationToken).ConfigureAwait(false);
            else
                await DownloadIndividualSegments(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _streamTasks.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _streamTasks.Writer.TryComplete(exception);
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }

        return;
    }

    private async Task DownloadPipelinedSegments(CancellationToken cancellationToken)
    {
        var segmentsEnqueued = 0;
        for (var batchStart = 0; batchStart < _segmentIds.Length;)
        {
            if (ShouldStopPrefetch(segmentsEnqueued))
                break;

            var batchCount = Math.Min(
                _bodyPipelineBatchSize, _segmentIds.Length - batchStart);
            var segmentIds = new SegmentId[batchCount];
            for (var index = 0; index < batchCount; index++)
            {
                segmentIds[index] = _segmentIds.Span[batchStart + index];
            }

            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
            var connection = await _usenetClient.AcquireExclusiveConnectionAsync(
                segmentIds, cancellationToken);
            var batch = await _usenetClient.DecodedBodiesAsync(
                segmentIds, connection, cancellationToken).ConfigureAwait(false);
            var streamTasks = batch.Responses
                .Select((response, index) => DownloadBatchSegment(
                    response,
                    segmentIds[index],
                    segmentIndex: batchStart + index,
                    isFirstSegment: batchStart + index == 0,
                    cancellationToken))
                .ToArray();

            var responseIndex = 0;
            try
            {
                for (; responseIndex < streamTasks.Length; responseIndex++)
                {
                    await _streamTasks.Writer.WriteAsync(
                        streamTasks[responseIndex], cancellationToken);
                    segmentsEnqueued++;
                }
            }
            catch
            {
                for (; responseIndex < streamTasks.Length; responseIndex++)
                {
                    _ = DisposeStreamAsync(streamTasks[responseIndex]);
                }

                throw;
            }

            batchStart += batchCount;
        }
    }

    private async Task DownloadIndividualSegments(CancellationToken cancellationToken)
    {
        for (var index = 0; index < _segmentIds.Length; index++)
        {
            if (ShouldStopPrefetch(index))
                break;

            var segmentId = _segmentIds.Span[index];
            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
            var connection = await _usenetClient.AcquireExclusiveConnectionAsync(
                segmentId, cancellationToken);
            var streamTask = DownloadSegment(
                segmentId, index, connection, isFirstSegment: index == 0, cancellationToken);
            try
            {
                await _streamTasks.Writer.WriteAsync(streamTask, cancellationToken);
            }
            catch
            {
                _ = DisposeStreamAsync(streamTask);
                throw;
            }
        }
    }

    /// <summary>
    /// Stop enqueueing once estimated fetched bytes cover the read budget plus
    /// one segment of slack for yEnc size variance. Null budget = unbounded.
    /// </summary>
    private bool ShouldStopPrefetch(int segmentsEnqueued)
    {
        if (_readBudget is null || _expectedSegmentSize <= 0)
            return false;
        return segmentsEnqueued * _expectedSegmentSize >= _readBudget.Value + _expectedSegmentSize;
    }

    private async Task<SegmentDownloadResult> DownloadSegment(
        string segmentId,
        int segmentIndex,
        UsenetExclusiveConnection exclusiveConnection,
        bool isFirstSegment,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bodyResponse = attempt == 0
                    ? await _usenetClient
                        .DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken)
                        .ConfigureAwait(false)
                    : await _usenetClient
                        .DecodedBodyAsync(segmentId, cancellationToken)
                        .ConfigureAwait(false);

                var stream = await DrainSegmentAsync(bodyResponse.Stream!, cancellationToken).ConfigureAwait(false);
                return SegmentDownloadResult.Success(stream);
            }
            catch (UsenetArticleNotFoundException e)
            {
                var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                    .ConfigureAwait(false);
                if (fallback is not null)
                    return SegmentDownloadResult.Success(fallback);

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    e.LogWarningKnownOrStack(
                        "First article {SegmentId} missing on all providers at playback start while reading {FileName}. " +
                        "Failing the stream so the player surfaces an error.",
                        segmentId, _fileName);
                    throw;
                }

                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers while reading {FileName}. Zero-filling {Bytes} bytes to keep playback alive.",
                    e.SegmentId,
                    e);
            }
            catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= MaxCorruptionRetries)
                    throw;

                Log.Debug(
                    e,
                    "Corrupt segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                    segmentId,
                    e.ProviderKey,
                    attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxBodyRetries)
                {
                    Log.Debug(e, "Transient failure fetching segment {SegmentId} (attempt {Attempt}). Retrying.",
                        segmentId, attempt + 1);
                    if (MultiProviderNntpClient.CurrentReadSessionId is { } retrySession)
                        StreamTrace.TryRetry(retrySession, segmentId, attempt + 1, e.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    e.LogWarningKnownOrStack(
                        "Segment {SegmentId} unavailable at playback start after {Attempts} attempts while reading {FileName}. " +
                        "Failing the stream so the player surfaces an error.",
                        segmentId, attempt + 1, _fileName);
                    throw;
                }

                return ZeroFillSegment(
                    "Segment {SegmentId} unavailable after retries while reading {FileName}. Zero-filling {Bytes} bytes to keep playback alive.",
                    segmentId, e);
            }
        }
    }

    private async Task<SegmentDownloadResult> DownloadBatchSegment(
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        int segmentIndex,
        bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            var stream = await DrainSegmentAsync(response.Stream!, cancellationToken).ConfigureAwait(false);
            return SegmentDownloadResult.Success(stream);
        }
        catch (UsenetArticleNotFoundException e)
        {
            var fallback = await TryFallbackSegmentsAsync(segmentIndex, cancellationToken)
                .ConfigureAwait(false);
            if (fallback is not null)
                return SegmentDownloadResult.Success(fallback);

            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Article {SegmentId} missing on all providers while reading {FileName}. Zero-filling {Bytes} bytes to keep playback alive.",
                e.SegmentId,
                e);
        }
        catch (UsenetCorruptArticleException e) when (!cancellationToken.IsCancellationRequested)
        {
            var stream = await RetryCorruptSegmentAsync(
                    segmentId, e, cancellationToken)
                .ConfigureAwait(false);
            return SegmentDownloadResult.Success(stream);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Segment {SegmentId} unavailable while reading {FileName}. Zero-filling {Bytes} bytes to keep playback alive.",
                segmentId, e);
        }
    }

    private async Task<Stream> RetryCorruptSegmentAsync(
        string segmentId,
        UsenetCorruptArticleException initialFailure,
        CancellationToken cancellationToken)
    {
        var failure = initialFailure;
        for (var attempt = 1; attempt <= MaxCorruptionRetries; attempt++)
        {
            Log.Debug(
                failure,
                "Corrupt pipelined segment {SegmentId} from provider {Provider}; retrying to allow provider failover (attempt {Attempt}).",
                segmentId,
                failure.ProviderKey,
                attempt);
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var response = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken)
                    .ConfigureAwait(false);
                return await DrainSegmentAsync(response.Stream!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UsenetCorruptArticleException exception)
            {
                failure = exception;
            }
        }

        ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing a corrupt segment failure.");
    }

    /// <summary>
    /// Try alternate MessageIds for a missing primary segment. Each BODY
    /// attempt completes its callback exactly once via DecodedBodyAsync.
    /// </summary>
    private async Task<Stream?> TryFallbackSegmentsAsync(
        int segmentIndex,
        CancellationToken cancellationToken)
    {
        var fallbacks = GetFallbacks(segmentIndex);
        if (fallbacks.Length == 0) return null;

        foreach (var fallbackId in fallbacks)
        {
            try
            {
                var bodyResponse = await _usenetClient
                    .DecodedBodyAsync(fallbackId, cancellationToken)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Segment {PrimaryIndex} recovered via fallback MessageId {FallbackId} while reading {FileName}.",
                    segmentIndex, fallbackId, _fileName);
                return await DrainSegmentAsync(bodyResponse.Stream!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException)
            {
                // Try the next alternate MessageId.
            }
        }

        return null;
    }

    private string[] GetFallbacks(int segmentIndex)
    {
        if (_segmentFallbacks is null ||
            segmentIndex < 0 ||
            segmentIndex >= _segmentFallbacks.Length)
            return [];

        return _segmentFallbacks[segmentIndex] ?? [];
    }

    private static async Task DisposeStreamAsync(Task<SegmentDownloadResult> streamTask)
    {
        try
        {
            var result = await streamTask.ConfigureAwait(false);
            await using var stream = result.Stream;
        }
        catch
        {
            // The producer owns reporting download failures.
        }
    }

    private SegmentDownloadResult ZeroFillSegment(
        string messageTemplate,
        string segmentId,
        Exception exception)
    {
        var fill = _expectedSegmentSize > 0 ? _expectedSegmentSize : 1;
        return SegmentDownloadResult.ZeroFill(
            new MemoryStream(new byte[fill], writable: false),
            messageTemplate,
            segmentId,
            fill,
            exception);
    }

    private async Task<Stream> DrainSegmentAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            var capacity = _expectedSegmentSize is > 0 and <= int.MaxValue ? (int)_expectedSegmentSize : 0;
            var buffer = new MemoryStream(capacity);
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return buffer;
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_streamTasks.Reader.TryRead(out var streamTask)) return 0;
                var result = await streamTask;
                _stream = AcceptSegment(result);
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }
    }

    private Stream AcceptSegment(SegmentDownloadResult result)
    {
        if (!result.IsZeroFill)
        {
            _consecutiveZeroFills = 0;
            return result.Stream;
        }

        _consecutiveZeroFills++;
        ZeroFillLogLimiter.Write(
            result.MessageTemplate!,
            result.SegmentId!,
            _fileName,
            result.Bytes,
            result.Failure);
        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, result.SegmentId!, result.Bytes);

        if (_consecutiveZeroFills < MaxConsecutiveZeroFills)
            return result.Stream;

        result.Stream.Dispose();
        _cts.Cancel();
        ExceptionDispatchInfo.Capture(result.Failure!).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing a zero-fill failure.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _stream?.Dispose();
        _streamTasks.Writer.TryComplete();

        // ensure that streams that were never read from the channel get disposed
        while (_streamTasks.Reader.TryRead(out var streamTask))
            _ = DisposeStreamAsync(streamTask);

        base.Dispose();
    }

    private sealed record SegmentDownloadResult(
        Stream Stream,
        string? MessageTemplate = null,
        string? SegmentId = null,
        long Bytes = 0,
        Exception? Failure = null)
    {
        public bool IsZeroFill => Failure is not null;

        public static SegmentDownloadResult Success(Stream stream) => new(stream);

        public static SegmentDownloadResult ZeroFill(
            Stream stream,
            string messageTemplate,
            string segmentId,
            long bytes,
            Exception failure) =>
            new(stream, messageTemplate, segmentId, bytes, failure);
    }
}

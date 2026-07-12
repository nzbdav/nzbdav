using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private const int BodyPipelineBatchSize = 4;
    private const int MaxBodyRetries = 2;

    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly long _expectedSegmentSize;
    private readonly bool _failFastOnFirstSegment;
    private readonly Channel<Task<Stream>> _streamTasks;
    private readonly int _bodyPipelineBatchSize;
    private readonly ContextualCancellationTokenSource _cts;
    private Stream? _stream;
    private bool _disposed;

    public static Stream Create(
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken)
    {
        return Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            expectedSegmentSize: 0,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests,
            cancellationToken);
    }

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken
    )
    {
        return articleBufferSize == 0
            ? new UnbufferedMultiSegmentStream(segmentIds, usenetClient, expectedSegmentSize)
            : new MultiSegmentStream(
                segmentIds,
                usenetClient,
                articleBufferSize,
                expectedSegmentSize,
                failFastOnFirstSegment,
                usePipelinedBodyRequests,
                cancellationToken);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        bool usePipelinedBodyRequests,
        CancellationToken cancellationToken
    )
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _expectedSegmentSize = expectedSegmentSize;
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _bodyPipelineBatchSize = Math.Min(BodyPipelineBatchSize, articleBufferSize);
        _streamTasks = Channel.CreateBounded<Task<Stream>>(articleBufferSize);
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
        for (var batchStart = 0; batchStart < _segmentIds.Length;)
        {
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
            var segmentId = _segmentIds.Span[index];
            await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
            var connection = await _usenetClient.AcquireExclusiveConnectionAsync(
                segmentId, cancellationToken);
            var streamTask = DownloadSegment(
                segmentId, connection, isFirstSegment: index == 0, cancellationToken);
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

    private async Task<Stream> DownloadSegment(
        string segmentId,
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

                return await DrainSegmentAsync(bodyResponse.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException e)
            {
                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    Log.Warning(e, "First article {SegmentId} missing on all providers at playback start. " +
                                   "Failing the stream so the player surfaces an error.", segmentId);
                    throw;
                }

                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                    e.SegmentId);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxBodyRetries)
                {
                    Log.Debug(e, "Transient failure fetching segment {SegmentId} (attempt {Attempt}). Retrying.",
                        segmentId, attempt + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    Log.Warning(e, "Segment {SegmentId} unavailable at playback start after {Attempts} attempts. " +
                                   "Failing the stream so the player surfaces an error.", segmentId, attempt + 1);
                    throw;
                }

                return ZeroFillSegment(
                    "Segment {SegmentId} unavailable after retries. Zero-filling {Bytes} bytes to keep playback alive.",
                    segmentId, e);
            }
        }
    }

    private async Task<Stream> DownloadBatchSegment(
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            return await DrainSegmentAsync(response.Stream, cancellationToken).ConfigureAwait(false);
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                e.SegmentId);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (_failFastOnFirstSegment && isFirstSegment) throw;
            return ZeroFillSegment(
                "Segment {SegmentId} unavailable. Zero-filling {Bytes} bytes to keep playback alive.",
                segmentId, e);
        }
    }

    private static async Task DisposeStreamAsync(Task<Stream> streamTask)
    {
        try
        {
            await using var stream = await streamTask.ConfigureAwait(false);
        }
        catch
        {
            // The producer owns reporting download failures.
        }
    }

    private Stream ZeroFillSegment(string messageTemplate, string segmentId, Exception? exception = null)
    {
        var fill = _expectedSegmentSize > 0 ? _expectedSegmentSize : 1;
        if (exception == null) Log.Warning(messageTemplate, segmentId, fill);
        else Log.Warning(exception, messageTemplate, segmentId, fill);
        return new MemoryStream(new byte[fill], writable: false);
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
                _stream = await streamTask;
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }
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
}

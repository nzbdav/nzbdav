using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Abstract base class for NNTP clients with default implementations of utility methods.
/// </summary>
public abstract class NntpClient : INntpClient
{
    public virtual int PipeliningDepth => 0;

    public abstract Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    public abstract Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    public abstract Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    public abstract void Dispose();

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support acquiring exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        if (segmentIds.Count == 0)
        {
            throw new ArgumentException("At least one segment ID is required.", nameof(segmentIds));
        }

        return AcquireExclusiveConnectionAsync(segmentIds[0], cancellationToken);
    }

    public virtual Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodyAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodiesAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedArticleAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        var decodedBodyResponse = await DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
        await using var stream = decodedBodyResponse.Stream;
        var headers = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
        return headers!;
    }

    public virtual async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return 0;
        var headers = await GetYencHeadersAsync(file.Segments[^1].MessageId, ct).ConfigureAwait(false);
        file.Segments[^1].ByteRange = LongRange.FromStartAndSize(headers.PartOffset, headers.PartSize);
        return headers!.PartOffset + headers!.PartSize;
    }

    public virtual async Task<NzbFileStream> GetFileStream(
        NzbFile nzbFile,
        int articleBufferSize,
        CancellationToken ct,
        bool usePipelinedBodyRequests = true)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await GetFileSizeAsync(nzbFile, ct).ConfigureAwait(false);
        return new NzbFileStream(
            segmentIds,
            fileSize,
            this,
            articleBufferSize,
            nzbFile.GetSegmentByteRanges(),
            usePipelinedBodyRequests);
    }

    public virtual NzbFileStream GetFileStream(
        NzbFile nzbFile,
        long fileSize,
        int articleBufferSize,
        bool usePipelinedBodyRequests = true)
    {
        return new NzbFileStream(
            nzbFile.GetSegmentIds(),
            fileSize,
            this,
            articleBufferSize,
            nzbFile.GetSegmentByteRanges(),
            usePipelinedBodyRequests
        );
    }

    public virtual NzbFileStream GetFileStream(
        string[] segmentIds,
        long fileSize,
        int articleBufferSize,
        LongRange[]? segmentByteRanges = null,
        bool usePipelinedBodyRequests = true)
    {
        return new NzbFileStream(
            segmentIds,
            fileSize,
            this,
            articleBufferSize,
            segmentByteRanges,
            usePipelinedBodyRequests);
    }

    public virtual async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        using var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = childCt.Token;

        var tasks = segmentIds
            .Select(async segmentId => (
                SegmentId: segmentId,
                Result: await StatAsync(segmentId, token).ConfigureAwait(false)
            ))
            .WithConcurrencyAsync(concurrency, token);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            progress?.Report(++processed);
            if (task.Result.ResponseType == UsenetResponseType.ArticleExists) continue;
            await childCt.CancelAsync().ConfigureAwait(false);

            // Only a clean 430 proves the article is missing; any other response
            // (e.g. a stale connection's goodbye line) must not fail the health check.
            if (task.Result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                throw new UsenetArticleNotFoundException(task.SegmentId, task.Result.ResponseMessage);
            throw new UsenetUnexpectedResponseException(task.SegmentId, task.Result.ResponseMessage);
        }
    }

    public virtual async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var segmentId in segmentIds)
        {
            var response = await StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            yield return new PipelinedStatResult
            {
                SegmentId = segmentId,
                Exists = response.ResponseType == UsenetResponseType.ArticleExists,
            };
        }
    }

    public virtual async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) yield break;

        depth = Math.Max(1, depth);
        for (var batchStart = 0; batchStart < segmentIds.Count; batchStart += depth)
        {
            var batchSize = Math.Min(depth, segmentIds.Count - batchStart);
            var batchIds = new SegmentId[batchSize];
            for (var index = 0; index < batchSize; index++)
                batchIds[index] = segmentIds[batchStart + index];

            var batch = await DecodedBodiesAsync(
                batchIds, onConnectionReadyAgain: null, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < batch.Responses.Count; index++)
            {
                var segmentId = segmentIds[batchStart + index];
                yield return await MapPipelinedBodyResultAsync(
                    batch.Responses[index], segmentId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public virtual async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var body in DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new PipelinedArticleResult
            {
                SegmentId = body.SegmentId,
                Found = body.Found,
                Stream = body.Stream,
                ArticleHeaders = null,
            };
        }
    }

    private static async Task<PipelinedBodyResult> MapPipelinedBodyResultAsync
    (
        Task<UsenetDecodedBodyResponse> responseTask,
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
            {
                return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = false,
                    Stream = null,
                };
            }

            if (response.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                throw new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage);
            }

            return new PipelinedBodyResult
            {
                SegmentId = segmentId,
                Found = true,
                Stream = response.Stream,
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return new PipelinedBodyResult
            {
                SegmentId = segmentId,
                Found = false,
                Stream = null,
            };
        }
    }

    public virtual async Task CheckAllSegmentsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        var processed = 0;
        var anyMissing = false;
        await foreach (var result in StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(++processed);
            if (result.Exists) continue;
            anyMissing = true;
            break;
        }

        if (anyMissing)
            await CheckAllSegmentsAsync(segmentIds, fallbackConcurrency, progress, cancellationToken).ConfigureAwait(false);
    }
}

using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using UsenetSharp.Clients;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This class has four responsibilities that differ from the underlying UsenetClient implementation
///   1. throw `CouldNotConnectToUsenetException` after any connection error.
///   2. throw `CouldNotLoginToUsenetException` after any login error.
///   3. Provide yenc-decoded data for articles retrieved through article/body commands.
///   4. throw `UsenetArticleNotFound` when articles do not exist, within article/body/head commands.
/// </summary>
public class BaseNntpClient : NntpClient
{
    private readonly UsenetClient _client = new();

    public override async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(host, port, useSsl, cancellationToken);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            const string message = "Could not connect to usenet host. Check connection settings.";
            throw new CouldNotConnectToUsenetException(message, e);
        }
    }

    public override async Task<UsenetResponse> AuthenticateAsync
    (
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken);
            if (!response.Success)
            {
                var message = $"Could not login to usenet host: {response.ResponseMessage}";
                throw new CouldNotLoginToUsenetException(message);
            }

            return response;
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            throw new CouldNotLoginToUsenetException("Could not login to usenet host.", e);
        }
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return _client.StatAsync(segmentId, cancellationToken);
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken);

        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw CreateArticleFetchException(segmentId, headResponse);

        return new UsenetHeadResponse()
        {
            SegmentId = headResponse.SegmentId,
            ResponseCode = headResponse.ResponseCode,
            ResponseMessage = headResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!
        };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var bodyResponse = await _client.DecodedBodyAsync(
            segmentId, onConnectionReadyAgain, cancellationToken);

        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw CreateArticleFetchException(segmentId, bodyResponse);

        return new UsenetDecodedBodyResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            Stream = bodyResponse.Stream!,
        };
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        return _client.DecodedBodiesAsync(
            segmentIds, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var articleResponse = await _client.ArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (articleResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            throw CreateArticleFetchException(segmentId, articleResponse);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = articleResponse.SegmentId,
            ResponseCode = articleResponse.ResponseCode,
            ResponseMessage = articleResponse.ResponseMessage,
            ArticleHeaders = articleResponse.ArticleHeaders!,
            Stream = new YencStream(articleResponse.Stream!),
        };
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    /// <summary>
    /// Only a clean 430 means the article is missing. Anything else (e.g. a
    /// buffered "400 idle timeout" from a connection the server already closed)
    /// is a connection-level problem and must be retryable.
    /// </summary>
    private static Exception CreateArticleFetchException(SegmentId segmentId, UsenetResponse response)
    {
        return response.ResponseType == UsenetResponseType.NoArticleWithThatMessageId
            ? new UsenetArticleNotFoundException(segmentId, response.ResponseMessage)
            : new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage);
    }

    public override void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

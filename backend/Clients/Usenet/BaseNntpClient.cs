using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using UsenetSharp.Clients;
using UsenetSharp.Models;

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
    private readonly IUsenetClient _client;

    public BaseNntpClient() : this(new UsenetClient(new UsenetClientOptions
    {
        CrcValidation = YencCrcValidationMode.WhenPresent,
    }))
    {
    }

    /// <summary>Test seam for injecting a scripted underlying client.</summary>
    internal BaseNntpClient(IUsenetClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

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

    public override async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        segmentId = PrepareSegmentId(segmentId);
        var response = await _client.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);

        // 223 (exists) and definitive-missing (430 / provider 451) are valid STAT
        // outcomes and are conveyed via the response object — callers check
        // ResponseType / IsDefinitiveMissing. Connection/session-level codes
        // (buffered 400 goodbye, 480 auth, 5xx) must not be treated as article verdicts.
        if (response.ResponseType == UsenetResponseType.ArticleExists ||
            UsenetArticleAvailability.IsDefinitiveMissing(response))
        {
            return response;
        }

        throw CreateConnectionLevelException(segmentId, response);
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        segmentId = PrepareSegmentId(segmentId);
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
        segmentId = PrepareSegmentId(segmentId);
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
            PrepareSegmentIds(segmentIds), onConnectionReadyAgain, cancellationToken);
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
        segmentId = PrepareSegmentId(segmentId);
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw CreateArticleFetchException(segmentId, headResponse);

        // UsenetSharp validates CRCs on decoded BODY responses. Pair HEAD + BODY
        // instead of locally decoding ARTICLE so first-segment metadata probes get
        // the same corruption detection as normal streaming.
        var bodyResponse = await _client.DecodedBodyAsync(
            segmentId, onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw CreateArticleFetchException(segmentId, bodyResponse);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!,
            Stream = bodyResponse.Stream!,
        };
    }

    private static SegmentId[] PrepareSegmentIds(IReadOnlyList<SegmentId> segmentIds)
    {
        var prepared = new SegmentId[segmentIds.Count];
        for (var index = 0; index < segmentIds.Count; index++)
            prepared[index] = PrepareSegmentId(segmentIds[index]);
        return prepared;
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    /// <summary>
    /// Definitive missing (430 / provider 451) means the article is gone.
    /// Anything else (e.g. a buffered "400 idle timeout" from a connection the
    /// server already closed) is a connection-level problem and must be retryable.
    /// </summary>
    private static Exception CreateArticleFetchException(SegmentId segmentId, UsenetResponse response)
    {
        return UsenetArticleAvailability.IsDefinitiveMissing(response)
            ? new UsenetArticleNotFoundException(segmentId, response.ResponseMessage)
            : CreateConnectionLevelException(segmentId, response);
    }

    private static Exception CreateConnectionLevelException(SegmentId segmentId, UsenetResponse response)
    {
        if (response.ResponseCode == 480)
        {
            return new UsenetUnexpectedResponseException(
                segmentId,
                "Provider requires authentication but no credentials are configured");
        }

        return new UsenetUnexpectedResponseException(segmentId, response.ResponseMessage);
    }

    public override void Dispose()
    {
        if (_client is IAsyncDisposable asyncDisposable)
        {
            // UsenetSharp sends a best-effort QUIT only from DisposeAsync.
            // Pool disposal already runs off the request path, so wait here to
            // release strict providers' connection slots before reconnecting.
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

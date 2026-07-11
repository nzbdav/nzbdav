using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiProviderNntpClientTests
{
    [Fact]
    public async Task BatchResponse_WithUnexpectedResponse_RetriesOnSameProvider()
    {
        // A stale pooled connection surfaces the server's buffered goodbye line
        // (e.g. "400 idle timeout") as the batch response. The segment must be
        // retried on the same provider instead of being reported missing.
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_WithCleanNotFound_RetriesOnSameProvider()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularResponseCode = 222,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(1, connection.SingularRequests);
    }

    [Fact]
    public async Task BatchResponse_WithUnexpectedResponse_ThrowsRetryableWhenRetriesFail()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 400,
            SingularException = segmentId =>
                new UsenetUnexpectedResponseException(segmentId, "400 idle timeout"),
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);

        // A connection-level failure must surface as retryable,
        // never as a (permanent) missing article.
        var exception = await Assert.ThrowsAsync<UsenetUnexpectedResponseException>(
            () => batch.Responses[0]);
        Assert.IsAssignableFrom<RetryableDownloadException>(exception);
    }

    [Fact]
    public async Task BatchSetup_WithStaleCancellation_RetriesOnAnotherConnection()
    {
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            BatchException = requestNumber => requestNumber == 1
                ? new TaskCanceledException("Cancellation recorded by an earlier request.")
                : null,
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        var batch = await client.DecodedBodiesAsync(
            ["segment"], onConnectionReadyAgain: null, CancellationToken.None);
        var response = await batch.Responses[0];

        Assert.Equal(UsenetResponseType.ArticleRetrievedBodyFollows, response.ResponseType);
        Assert.Equal(2, connection.BatchRequests);
    }

    [Fact]
    public async Task BatchSetup_WithCurrentRequestCancellation_DoesNotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new ScriptedNntpClient
        {
            BatchResponseCode = 222,
            BatchException = _ =>
            {
                cancellation.Cancel();
                return new TaskCanceledException("Current request was cancelled.");
            },
        };
        using var client = new MultiProviderNntpClient([CreateProvider(connection)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DecodedBodiesAsync(
                ["segment"], onConnectionReadyAgain: null, cancellation.Token));
        Assert.Equal(1, connection.BatchRequests);
    }

    private static MultiConnectionNntpClient CreateProvider(INntpClient connection)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections: 1, _ => ValueTask.FromResult(connection));
        return new MultiConnectionNntpClient(
            pool, ProviderType.Pooled, new ProviderCircuitBreaker("test"), "test");
    }

    private sealed class ScriptedNntpClient : NntpClient
    {
        public required int BatchResponseCode { get; init; }
        public int SingularResponseCode { get; init; } = 222;
        public Func<int, Exception?>? BatchException { get; init; }
        public Func<string, Exception>? SingularException { get; init; }
        public int BatchRequests { get; private set; }
        public int SingularRequests { get; private set; }

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BatchRequests++;
            var exception = BatchException?.Invoke(BatchRequests);
            if (exception != null)
                throw exception;

            var responses = segmentIds
                .Select(segmentId => Task.FromResult(CreateResponse(segmentId, BatchResponseCode)))
                .ToArray();
            onConnectionReadyAgain?.Invoke(BatchResponseCode switch
            {
                (int)UsenetResponseType.ArticleRetrievedBodyFollows => ArticleBodyResult.Retrieved,
                (int)UsenetResponseType.NoArticleWithThatMessageId => ArticleBodyResult.NotFound,
                _ => ArticleBodyResult.NotRetrieved,
            });
            return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            SingularRequests++;
            if (SingularException != null)
                throw SingularException(segmentId.ToString());

            var response = CreateResponse(segmentId, SingularResponseCode);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(response);
        }

        private static UsenetDecodedBodyResponse CreateResponse(SegmentId segmentId, int responseCode)
        {
            var success = responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows;
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId.ToString(),
                ResponseCode = responseCode,
                ResponseMessage = $"{responseCode} scripted response",
                Stream = success ? new YencStream(new MemoryStream([], writable: false)) : null,
            };
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}

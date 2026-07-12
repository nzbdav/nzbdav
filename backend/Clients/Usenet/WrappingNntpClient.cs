using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class WrappingNntpClient(INntpClient usenetClient) : NntpClient
{
    private INntpClient _usenetClient = usenetClient;

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        _usenetClient.ConnectAsync(host, port, useSsl, cancellationToken);

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        _usenetClient.AuthenticateAsync(user, pass, cancellationToken);

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.StatAsync(segmentId, cancellationToken);

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.HeadAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, cancellationToken);

    public override Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken) =>
        _usenetClient.DateAsync(cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesAsync(segmentIds, onConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

    public override int PipeliningDepth => _usenetClient.PipeliningDepth;

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
        _usenetClient.AcquireExclusiveConnectionAsync(segmentIds, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds, UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesAsync(segmentIds, exclusiveConnection, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken);


    public override IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.StatsPipelinedAsync(segmentIds, depth, cancellationToken);

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken);

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken) =>
        _usenetClient.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken);

    protected void ReplaceUnderlyingClient(INntpClient usenetClient)
    {
        var old = _usenetClient;
        _usenetClient = usenetClient;
        if (old is IDisposable disposable)
            disposable.Dispose();
    }

    public override void Dispose()
    {
        _usenetClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

namespace NzbWebDAV.Exceptions;

public sealed class UsenetCorruptArticleException(
    string segmentId,
    string providerKey,
    Exception innerException)
    : RetryableDownloadException(
        $"Provider {providerKey} returned corrupt yEnc data for segment {segmentId}.",
        innerException)
{
    public string SegmentId { get; } = segmentId;
    public string ProviderKey { get; } = providerKey;
}

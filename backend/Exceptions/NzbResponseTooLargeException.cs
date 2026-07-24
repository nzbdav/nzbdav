namespace NzbWebDAV.Exceptions;

public sealed class NzbResponseTooLargeException(long maxBytes, long? contentLength = null)
    : Exception(FormatMessage(maxBytes, contentLength))
{
    public long MaxBytes { get; } = maxBytes;
    public long? ContentLength { get; } = contentLength;

    private static string FormatMessage(long maxBytes, long? contentLength) =>
        contentLength is { } declared
            ? $"NZB response Content-Length {declared} exceeds the {maxBytes}-byte limit."
            : $"NZB response exceeded the {maxBytes}-byte limit while streaming.";
}

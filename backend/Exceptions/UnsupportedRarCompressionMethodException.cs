namespace NzbWebDAV.Exceptions;

public class UnsupportedRarCompressionMethodException(string message) : NonRetryableDownloadException(message)
{
}

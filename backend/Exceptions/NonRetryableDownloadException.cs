namespace NzbWebDAV.Exceptions;

public class NonRetryableDownloadException(string message) : Exception(message)
{
}

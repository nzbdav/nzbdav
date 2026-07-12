namespace NzbWebDAV.Exceptions;

public class NoVideoFilesFoundException(string message) : NonRetryableDownloadException(message)
{
}

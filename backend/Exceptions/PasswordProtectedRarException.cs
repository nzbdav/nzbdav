namespace NzbWebDAV.Exceptions;

public class PasswordProtectedRarException(string message) : NonRetryableDownloadException(message)
{
}

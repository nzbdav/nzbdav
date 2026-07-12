namespace NzbWebDAV.WebDav.Requests;

public class UploadFromStreamRequest
{
    public required Stream Source { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

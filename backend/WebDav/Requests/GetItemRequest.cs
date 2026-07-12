namespace NzbWebDAV.WebDav.Requests;

public class GetItemRequest
{
    public required string Name { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

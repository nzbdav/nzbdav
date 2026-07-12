namespace NzbWebDAV.WebDav.Requests;

public class DeleteItemRequest
{
    public required string Name { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

namespace NzbWebDAV.WebDav.Requests;

public class CreateItemRequest
{
    public required string Name { get; init; }
    public required Stream Stream { get; init; }
    public required bool Overwrite { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

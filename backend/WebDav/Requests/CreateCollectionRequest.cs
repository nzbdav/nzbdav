namespace NzbWebDAV.WebDav.Requests;

public class CreateCollectionRequest
{
    public required string Name { get; init; }
    public required bool Overwrite { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

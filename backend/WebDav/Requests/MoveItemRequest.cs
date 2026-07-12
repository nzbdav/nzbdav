using NWebDav.Server.Stores;

namespace NzbWebDAV.WebDav.Requests;

public class MoveItemRequest
{
    public required string SourceName { get; init; }
    public required IStoreCollection Destination { get; init; }
    public required string DestinationName { get; init; }
    public required bool Overwrite { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

using NWebDav.Server.Stores;

namespace NzbWebDAV.WebDav.Requests;

public class CopyRequest
{
    public required IStoreCollection Destination { get; init; }
    public required string Name { get; init; }
    public required bool Overwrite { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

using NWebDav.Server.Stores;

namespace NzbWebDAV.WebDav.Requests;

public class SupportsFastMoveRequest
{
    public required IStoreCollection Destination { get; init; }
    public required string DestinationName { get; init; }
    public required bool Overwrite { get; init; }
}

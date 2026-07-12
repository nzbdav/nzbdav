using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusResponse
{
    [JsonPropertyName("status")]
    public required FullStatusObject Status { get; init; }

    public class FullStatusObject
    {
        [JsonPropertyName("completedir")]
        public required string CompleteDir { get; init; }
    }
}

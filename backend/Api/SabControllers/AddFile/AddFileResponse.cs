using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileResponse : SabBaseResponse
{
    [JsonPropertyName("nzo_ids")]
    public List<string> NzoIds { get; set; } = [];
}

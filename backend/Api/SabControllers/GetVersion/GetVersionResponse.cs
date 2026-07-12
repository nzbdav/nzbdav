namespace NzbWebDAV.Api.SabControllers.GetVersion;

public class GetVersionResponse : SabBaseResponse
{
    public string Version { get; set; } = string.Empty;
}

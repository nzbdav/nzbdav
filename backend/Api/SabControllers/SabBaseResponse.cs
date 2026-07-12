namespace NzbWebDAV.Api.SabControllers;

public class SabBaseResponse
{
    public bool Status { get; set; } = true;
    public string? Error { get; set; }
}

namespace NzbWebDAV.Api.Controllers;

public class BaseApiResponse
{
    public bool Status { get; set; } = true;
    public string? Error { get; set; }
}

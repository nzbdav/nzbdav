namespace NzbWebDAV.Api.Controllers.Authenticate;

public class AuthenticateResponse : BaseApiResponse
{
    public bool Authenticated { get; init; }
}

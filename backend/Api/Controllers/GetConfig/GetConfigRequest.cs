using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.GetConfig;

public class GetConfigRequest
{
    public HashSet<string> ConfigKeys { get; init; }

    public GetConfigRequest(HttpContext context)
    {
        ConfigKeys = context.Request.Form["config-keys"]
            .Where(x => x is not null)
            .Select(x => x!)
            .ToHashSet();
    }
}

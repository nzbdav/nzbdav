using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetVersion;

public class GetVersionController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public const string Version = "4.5.1";
    protected override bool RequiresAuthentication => false;

    protected override Task<IActionResult> Handle()
    {
        // mimic sabnzbd version
        var version = new GetVersionResponse() { Version = Version };
        return Task.FromResult<IActionResult>(Ok(version));
    }
}

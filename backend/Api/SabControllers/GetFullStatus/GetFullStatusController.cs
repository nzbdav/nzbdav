using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetFullStatus;

public class GetFullStatusController(
    HttpContext httpContext,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        // mimic sabnzbd fullstatus
        var status = new GetFullStatusResponse()
        {
            Status = new GetFullStatusResponse.FullStatusObject()
            {
                CompleteDir = Path.Join(configManager.GetRcloneMountDir(), DavItem.SymlinkFolder.Name),
            }
        };

        return Task.FromResult<IActionResult>(Ok(status));
    }
}

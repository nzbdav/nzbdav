using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetConfig;

[ApiController]
[Route("api/get-config")]
public class GetConfigController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetConfigResponse> GetConfig(GetConfigRequest request)
    {
        var storedConfigItems = await dbClient.Ctx.ConfigItems
            .AsNoTracking()
            .Where(x => request.ConfigKeys.Contains(x.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var secretMasker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var configItems = storedConfigItems.Select(item => new ConfigItem
        {
            ConfigName = item.ConfigName,
            ConfigValue = secretMasker.MaskForResponse(item.ConfigName, item.ConfigValue)
        }).ToList();

        var response = new GetConfigResponse { ConfigItems = configItems };
        return response;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetConfigRequest(HttpContext);
        var response = await GetConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}

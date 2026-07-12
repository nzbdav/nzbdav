using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        var configNames = request.ConfigItems.Select(x => x.ConfigName).ToHashSet();
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var secretMasker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var resolvedItems = request.ConfigItems.Select(item =>
        {
            var existingValue = existingItemsDict.GetValueOrDefault(item.ConfigName)?.ConfigValue;
            var resolvedValue = secretMasker.ResolveForUpdate(
                item.ConfigName,
                item.ConfigValue,
                existingValue);

            if (item.ConfigName == "webdav.pass" &&
                !ConfigSecretMasker.IsMaskToken(item.ConfigValue))
                resolvedValue = PasswordUtil.Hash(resolvedValue);

            return new ConfigItem
            {
                ConfigName = item.ConfigName,
                ConfigValue = resolvedValue
            };
        }).ToList();

        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in resolvedItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                itemsToInsert.Add(item);
            }
        }

        dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        configManager.UpdateValues(resolvedItems);

        return new UpdateConfigResponse { Status = true };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        // 1. Retrieve all ConfigItems from the database that match the ConfigNames in the request
        var requestedItems = request.ConfigItems
            .GroupBy(x => x.ConfigName)
            .Select(x => x.Last())
            .ToList();
        var configNames = requestedItems.Select(x => x.ConfigName).ToHashSet();
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 2. Split the items into those that need to be updated and those that need to be inserted
        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in requestedItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                var normalizedItem = ConfigSecretRedactor.PreserveExistingSecrets(item, existingItem.ConfigValue);
                if (ConfigManager.AreConfigValuesEquivalent(
                        normalizedItem.ConfigName,
                        existingItem.ConfigValue,
                        normalizedItem.ConfigValue))
                    continue;

                existingItem.ConfigValue = normalizedItem.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                var normalizedItem = ConfigSecretRedactor.PreserveExistingSecrets(item, existingValue: null);
                if (ConfigManager.IsEffectivelyUnsetConfigValue(normalizedItem.ConfigName, normalizedItem.ConfigValue))
                    continue;

                itemsToInsert.Add(normalizedItem);
            }
        }

        // 3. Perform bulk insert and bulk update
        if (itemsToInsert.Count > 0)
            dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        if (itemsToUpdate.Count > 0)
            dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        // 4. Save changes in one call
        if (itemsToInsert.Count > 0 || itemsToUpdate.Count > 0)
            await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 5. Update the ConfigManager
        configManager.UpdateValues(itemsToInsert.Concat(itemsToUpdate).ToList());

        // return
        return new UpdateConfigResponse { Status = true };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}

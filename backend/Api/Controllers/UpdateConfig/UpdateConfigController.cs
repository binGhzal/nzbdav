using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private static readonly string[] RcloneCredentialTopologyConfigNames =
        ["rclone.host", "rclone.user", "rclone.fs", "rclone.pass"];

    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        if (request.ConfigItems.Any(item =>
                TransferV3ReservedConfigPolicy.IsReserved(item.ConfigName)))
            throw new BadHttpRequestException(TransferV3ReservedConfigPolicy.ReservedConfigMessage);

        // 1. Retrieve all ConfigItems from the database that match the ConfigNames in the request
        var requestedItems = request.ConfigItems
            .GroupBy(x => x.ConfigName)
            .Select(x => x.Last())
            .ToList();
        var requestedItemsDict = requestedItems.ToDictionary(x => x.ConfigName);
        var configNames = requestedItems.Select(x => x.ConfigName).ToHashSet();
        if (requestedItems.Any(item => IsRcloneCredentialTopologyConfig(item.ConfigName)))
            configNames.UnionWith(RcloneCredentialTopologyConfigNames);
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 2. Split the items into those that need to be updated and those that need to be inserted
        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        ValidateRcloneCredentialTopologyChange(requestedItemsDict, existingItemsDict);
        var normalizedItems = requestedItems
            .Select(item => PreserveExistingSecretsOrBadRequest(
                item,
                existingItemsDict.TryGetValue(item.ConfigName, out var existingItem)
                    ? existingItem.ConfigValue
                    : null))
            .ToList();
        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in normalizedItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                if (ConfigManager.AreConfigValuesEquivalent(
                        item.ConfigName,
                        existingItem.ConfigValue,
                        item.ConfigValue))
                    continue;

                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                if (ConfigManager.IsEffectivelyUnsetConfigValue(item.ConfigName, item.ConfigValue))
                    continue;

                itemsToInsert.Add(item);
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

    private static ConfigItem PreserveExistingSecretsOrBadRequest(ConfigItem item, string? existingValue)
    {
        try
        {
            return ConfigSecretRedactor.PreserveExistingSecrets(item, existingValue);
        }
        catch (ConfigSecretResolutionException exception)
        {
            throw new BadHttpRequestException(exception.Message);
        }
    }

    private static void ValidateRcloneCredentialTopologyChange(
        IReadOnlyDictionary<string, ConfigItem> requestedItems,
        IReadOnlyDictionary<string, ConfigItem> existingItems)
    {
        if (!existingItems.TryGetValue("rclone.pass", out var existingPassword)
            || ConfigManager.IsEffectivelyUnsetConfigValue("rclone.pass", existingPassword.ConfigValue))
        {
            return;
        }

        var topologyChanged = requestedItems
            .Where(item => IsRcloneCredentialTopologyConfig(item.Key))
            .Any(item =>
            {
                existingItems.TryGetValue(item.Key, out var existingTopologyItem);
                return !AreRcloneTopologyValuesEquivalent(
                    item.Key,
                    existingTopologyItem?.ConfigValue,
                    item.Value.ConfigValue);
            });
        if (!topologyChanged) return;

        if (!requestedItems.TryGetValue("rclone.pass", out var submittedPassword)
            || ConfigSecretRedactor.IsRedactedSecret(submittedPassword.ConfigValue))
        {
            throw new BadHttpRequestException(
                "Changing the rclone host, user, or VFS selector requires re-entering the password or explicitly clearing it.");
        }
    }

    private static bool IsRcloneCredentialTopologyConfig(string configName)
    {
        return configName is "rclone.host" or "rclone.user" or "rclone.fs";
    }

    private static bool AreRcloneTopologyValuesEquivalent(
        string configName,
        string? existingValue,
        string? submittedValue)
    {
        if (string.Equals(existingValue, submittedValue, StringComparison.Ordinal)) return true;
        if (ConfigManager.IsEffectivelyUnsetConfigValue(configName, existingValue)
            && ConfigManager.IsEffectivelyUnsetConfigValue(configName, submittedValue))
        {
            return true;
        }

        return configName switch
        {
            "rclone.host" => EndpointIdentity.AreEquivalent(existingValue, submittedValue),
            "rclone.user" => string.Equals(existingValue, submittedValue, StringComparison.Ordinal),
            "rclone.fs" => string.Equals(existingValue?.Trim(), submittedValue?.Trim(), StringComparison.Ordinal),
            _ => false
        };
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}

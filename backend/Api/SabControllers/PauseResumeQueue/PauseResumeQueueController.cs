using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.PauseResumeQueue;

public class PauseResumeQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    bool isPaused
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override async Task<IActionResult> Handle()
    {
        const string configName = "queue.paused";
        var configItem = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(x => x.ConfigName == configName, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (configItem == null)
        {
            configItem = new ConfigItem { ConfigName = configName, ConfigValue = isPaused.ToString().ToLower() };
            dbClient.Ctx.ConfigItems.Add(configItem);
        }
        else
        {
            configItem.ConfigValue = isPaused.ToString().ToLower();
        }

        await dbClient.Ctx.SaveChangesAsync(httpContext.RequestAborted).ConfigureAwait(false);
        configManager.UpdateValues([configItem]);
        queueManager.AwakenQueue();

        return Ok(new SabBaseResponse { Status = true });
    }
}

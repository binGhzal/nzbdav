using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Api.SabControllers.AddUrl;
using NzbWebDAV.Api.SabControllers.ChangeQueuePostProcessing;
using NzbWebDAV.Api.SabControllers.ChangeQueuePriority;
using NzbWebDAV.Api.SabControllers.GetCategories;
using NzbWebDAV.Api.SabControllers.GetConfig;
using NzbWebDAV.Api.SabControllers.GetFullStatus;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Api.SabControllers.GetScripts;
using NzbWebDAV.Api.SabControllers.GetServerStats;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Api.SabControllers.GetVersion;
using NzbWebDAV.Api.SabControllers.GetWarnings;
using NzbWebDAV.Api.SabControllers.PauseResumeQueue;
using NzbWebDAV.Api.SabControllers.PauseResumeQueueItem;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Api.SabControllers.RemoveFromQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers;

[ApiController]
[Route("api")]
public class SabApiController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    ActiveStreamTracker activeStreamTracker,
    HealthCheckService healthCheckService,
    MountStatusProvider mountStatusProvider
) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequests()
    {
        try
        {
            var controller = GetController();
            return await controller.HandleRequest().ConfigureAwait(false);
        }
        catch (BadHttpRequestException e)
        {
            return BadRequest(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (Exception e)
        {
            return StatusCode(500, new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
    }

    public BaseController GetController()
    {
        switch (HttpContext.GetRequestParam("mode"))
        {
            case "version":
                return new GetVersionController(
                    HttpContext, configManager);
            case "status":
                return new GetStatusController(
                    HttpContext, dbClient, configManager, queueManager, activeStreamTracker,
                    healthCheckService, mountStatusProvider);
            case "get_cats":
                return new GetCategoriesController(
                    HttpContext, configManager);
            case "get_scripts":
                return new GetScriptsController(
                    HttpContext, configManager);
            case "get_config":
                return new GetConfigController(
                    HttpContext, configManager);
            case "warnings":
                return new GetWarningsController(
                    HttpContext, configManager);
            case "server_stats":
                return new GetServerStatsController(
                    HttpContext, dbClient, configManager);
            case "fullstatus":
                return new GetFullStatusController(
                    HttpContext, dbClient, configManager, queueManager, activeStreamTracker,
                    healthCheckService, mountStatusProvider);
            case "pause":
                return new PauseResumeQueueController(
                    HttpContext, dbClient, queueManager, configManager, isPaused: true);
            case "resume":
                return new PauseResumeQueueController(
                    HttpContext, dbClient, queueManager, configManager, isPaused: false);
            case "addfile":
                return new AddFileController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "addurl":
                return new AddUrlController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);

            case "queue" when HttpContext.GetRequestParam("name") == "delete":
                return new RemoveFromQueueController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "queue" when HttpContext.GetRequestParam("name") == "pause":
                return new PauseResumeQueueItemController(
                    HttpContext, dbClient, queueManager, configManager, isPaused: true);
            case "queue" when HttpContext.GetRequestParam("name") == "resume":
                return new PauseResumeQueueItemController(
                    HttpContext, dbClient, queueManager, configManager, isPaused: false);
            case "queue" when HttpContext.GetRequestParam("name") == "priority":
                return new ChangeQueuePriorityController(
                    HttpContext, dbClient, queueManager, configManager);
            case "queue" when HttpContext.GetRequestParam("name") == "change_opts":
            case "change_opts":
                return new ChangeQueuePostProcessingController(
                    HttpContext, dbClient, configManager);
            case "queue":
                return new GetQueueController(
                    HttpContext, dbClient, queueManager, configManager);

            case "history" when HttpContext.GetRequestParam("name") == "delete":
                return new RemoveFromHistoryController(
                    HttpContext, dbClient, configManager, websocketManager);
            case "history":
                return new GetHistoryController(
                    HttpContext, dbClient, configManager);

            default:
                throw new BadHttpRequestException("Invalid mode");
        }
    }

    public abstract class BaseController(HttpContext httpContext, ConfigManager configManager) : ControllerBase
    {
        protected HttpContext RequestContext { get; } = httpContext;
        protected ConfigManager ConfigManager { get; } = configManager;

        public Task<IActionResult> HandleRequest()
        {
            if (RequiresAuthentication)
            {
                var apiKey = RequestContext.GetRequestApiKey();
                var isValidKey = apiKey?.IsAny(
                    ConfigManager.GetApiKey(),
                    EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY")
                );
                if (!isValidKey.HasValue)
                    throw new UnauthorizedAccessException("API Key Required");
                if (!isValidKey.Value)
                    throw new UnauthorizedAccessException("API Key Incorrect");
            }

            return Handle();
        }

        protected virtual bool RequiresAuthentication => true;
        protected abstract Task<IActionResult> Handle();
    }
}

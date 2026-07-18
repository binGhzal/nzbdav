using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.Arr;

[ApiController]
[Route("api/arr")]
public sealed class ArrOperationsController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ArrOperationsService arrOperationsService
) : ControllerBase
{
    [HttpGet("validation")]
    public async Task<IActionResult> GetValidation()
    {
        EnsureAuthorized();
        return Ok(await arrOperationsService
            .BuildValidationAsync(dbClient, HttpContext.RequestAborted)
            .ConfigureAwait(false));
    }

    [HttpGet("search-nudges")]
    public async Task<IActionResult> GetSearchNudges()
    {
        EnsureAuthorized();
        var limit = ParseLimit();
        var status = Request.Query["status"].FirstOrDefault();
        var app = Request.Query["app"].FirstOrDefault();
        var mode = Request.Query["mode"].FirstOrDefault();
        var command = Request.Query["command"].FirstOrDefault();
        var search = Request.Query["search"].FirstOrDefault();
        return Ok(new ArrSearchNudgeCommandsResponse
        {
            Commands = await arrOperationsService
                .GetSearchNudgeCommandsAsync(dbClient.Ctx, limit, status, app, mode, command, search, HttpContext.RequestAborted)
                .ConfigureAwait(false)
        });
    }

    [HttpPost("search-nudges/{id:guid}/retry")]
    public async Task<IActionResult> RetrySearchNudge(Guid id)
    {
        EnsureAuthorized();
        return Ok(await arrOperationsService
            .RetrySearchNudgeCommandAsync(dbClient.Ctx, id, HttpContext.RequestAborted)
            .ConfigureAwait(false));
    }

    [HttpPost("search-nudges/clear")]
    public async Task<IActionResult> ClearSearchNudges()
    {
        EnsureAuthorized();
        var status = Request.Query["status"].FirstOrDefault();
        var deleted = await arrOperationsService
            .ClearSearchNudgeCommandsAsync(dbClient.Ctx, status, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new { status = true, deleted });
    }

    [HttpGet("correlations")]
    public async Task<IActionResult> GetCorrelations()
    {
        EnsureAuthorized();
        var limit = ParseLimit();
        var search = Request.Query["search"].FirstOrDefault();
        var app = Request.Query["app"].FirstOrDefault();
        return Ok(new ArrCorrelationsResponse
        {
            Correlations = await arrOperationsService
                .GetCorrelationsAsync(dbClient.Ctx, limit, search, app, HttpContext.RequestAborted)
                .ConfigureAwait(false)
        });
    }

    [HttpPost("correlations")]
    public async Task<IActionResult> UpsertCorrelation([FromBody] ArrManualCorrelationRequest request)
    {
        EnsureAuthorized();
        var correlation = await arrOperationsService
            .UpsertManualCorrelationAsync(dbClient.Ctx, request, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new ArrCorrelationEnvelope { Correlation = correlation });
    }

    [HttpDelete("correlations/{id:guid}")]
    public async Task<IActionResult> DeleteCorrelation(Guid id)
    {
        EnsureAuthorized();
        await arrOperationsService
            .DeleteCorrelationAsync(dbClient.Ctx, id, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new { status = true });
    }

    [HttpPost("events/{app}")]
    public async Task<IActionResult> IngestEvent(string app)
    {
        EnsureAuthorized();
        var payload = await ReadPayloadAsync().ConfigureAwait(false);
        return Ok(await arrOperationsService
            .IngestCustomScriptEventAsync(dbClient.Ctx, app, payload, HttpContext.RequestAborted)
            .ConfigureAwait(false));
    }

    private void EnsureAuthorized()
    {
        var apiKey = HttpContext.GetProtocolRequestApiKey();
        if (apiKey == null)
            throw new UnauthorizedAccessException("API Key Required");
        if (!apiKey.IsAny(configManager.GetApiKey(), EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY")))
            throw new UnauthorizedAccessException("API Key Incorrect");
    }

    private int ParseLimit()
    {
        var raw = Request.Query["limit"].FirstOrDefault();
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 500) : 50;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadPayloadAsync()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            foreach (var (key, value) in form)
                values[key.ToLowerInvariant()] = value.ToString();
            return values;
        }

        if (Request.ContentLength is null or 0) return values;
        using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted)
            .ConfigureAwait(false);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name.ToLowerInvariant()] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            };
        }

        return values;
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Config;
using backend.Tests.Security;
using backend.Tests.Services;
using NzbWebDAV.Security;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class PublicFailureEnvelopeTests
{
    [Fact]
    public void SuccessResponsesOmitOptionalFailureMetadataAndFailuresHaveNoPublicConstructor()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var baseJson = JsonSerializer.Serialize(new BaseApiResponse { Status = true }, options);
        var sabJson = JsonSerializer.Serialize(new SabBaseResponse { Status = true }, options);

        Assert.DoesNotContain("code", baseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("correlation_id", baseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("code", sabJson, StringComparison.Ordinal);
        Assert.DoesNotContain("correlation_id", sabJson, StringComparison.Ordinal);
        Assert.Empty(typeof(PublicFailure).GetConstructors());
    }

    [Theory]
    [InlineData("bad-request", StatusCodes.Status400BadRequest, "invalid_request", "The request is invalid.")]
    [InlineData("unauthorized", StatusCodes.Status401Unauthorized, "authentication_required", "Authentication is required.")]
    [InlineData("internal", StatusCodes.Status500InternalServerError, "internal_error", "The request could not be completed.")]
    public async Task BaseApiFailuresUseBoundedStableEnvelope(
        string failureKind,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var controller = new ThrowingBaseController(failureKind)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateContext() }
        };

        var result = await controller.HandleApiRequest();

        AssertEnvelope(controller.HttpContext, result, expectedStatus, expectedCode, expectedMessage);
    }

    [Theory]
    [InlineData("bad-request", StatusCodes.Status400BadRequest, "invalid_request", "The request is invalid.")]
    [InlineData("unauthorized", StatusCodes.Status401Unauthorized, "authentication_required", "Authentication is required.")]
    [InlineData("internal", StatusCodes.Status500InternalServerError, "internal_error", "The request could not be completed.")]
    public async Task SabApiFailuresUseBoundedStableEnvelope(
        string failureKind,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var previousKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-internal-key");
            var context = CreateContext();
            context.Request.QueryString = failureKind == "bad-request"
                ? new QueryString($"?mode={Uri.EscapeDataString(PublicFailureCanary.Composite)}")
                : new QueryString("?mode=queue");
            if (failureKind == "internal")
                context.Request.Headers["x-api-key"] = "test-internal-key";
            var controller = CreateSabController();
            controller.ControllerContext = new ControllerContext { HttpContext = context };

            var result = await controller.HandleApiRequests();

            AssertEnvelope(context, result, expectedStatus, expectedCode, expectedMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousKey);
        }
    }

    private static SabApiController CreateSabController() => new(
        dbClient: null!,
        configManager: new ConfigManager(),
        queueManager: null!,
        websocketManager: null!,
        activeStreamTracker: null!,
        healthCheckService: null!,
        mountStatusProvider: null!,
        usenetClient: null!,
        arrDownloadReportService: null!,
        arrOperationsService: null!,
        nzbBlobIngestCoordinator: null!);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void AssertEnvelope(
        HttpContext context,
        IActionResult result,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var json = JsonSerializer.SerializeToElement(
            objectResult.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        PublicFailureCanary.AssertSafe(json.GetRawText());
        PublicFailureCanary.AssertSafe(context.Response.Headers.ToString());
        Assert.False(json.GetProperty("status").GetBoolean());
        Assert.Equal(expectedMessage, json.GetProperty("error").GetString());
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
        var correlationId = Assert.IsType<string>(json.GetProperty("correlation_id").GetString());
        Assert.Matches("^[0-9a-f]{32}$", correlationId);
        Assert.Equal(correlationId, context.Response.Headers["X-Correlation-ID"]);
    }

    private sealed class ThrowingBaseController(string failureKind) : BaseApiController
    {
        protected override bool RequiresAuthentication => false;

        protected override Task<IActionResult> HandleRequest()
        {
            throw failureKind switch
            {
                "bad-request" => new BadHttpRequestException(PublicFailureCanary.Composite),
                "unauthorized" => new UnauthorizedAccessException(PublicFailureCanary.Composite),
                _ => PublicFailureCanary.NestedException,
            };
        }
    }
}

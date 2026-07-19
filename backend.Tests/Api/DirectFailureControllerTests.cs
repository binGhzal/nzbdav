using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.GetDatabaseBackup;
using NzbWebDAV.Api.Controllers.Repair;
using NzbWebDAV.Database;
using NzbWebDAV.Security;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class DirectFailureControllerTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public DirectFailureControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DatabaseBackupDisabledReturnsFixedFailureEnvelope()
    {
        var previousEnabled = Environment.GetEnvironmentVariable(
            "DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT", null);
            var controller = new GetDatabaseBackupController();

            var result = await HandleAuthenticatedAsync(controller, HttpMethods.Get);

            AssertFailure(
                controller.HttpContext,
                result,
                StatusCodes.Status403Forbidden,
                "endpoint_disabled",
                "This endpoint is disabled.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT",
                previousEnabled);
        }
    }

    [Fact]
    public async Task DatabaseBackupMissingReturnsFixedFailureWithoutFilesystemPath()
    {
        var previousEnabled = Environment.GetEnvironmentVariable(
            "DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT");
        var previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT", "true");
            Environment.SetEnvironmentVariable(
                "CONFIG_PATH",
                Path.Combine(Path.GetTempPath(), $"nzbdav-missing-backup-{Guid.NewGuid():N}"));
            var controller = new GetDatabaseBackupController();

            var result = await HandleAuthenticatedAsync(controller, HttpMethods.Get);

            AssertFailure(
                controller.HttpContext,
                result,
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested resource was not found.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previousConfigPath);
            Environment.SetEnvironmentVariable(
                "DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT",
                previousEnabled);
        }
    }

    [Fact]
    public async Task CancelUnknownRepairRunReturnsFixedFailureEnvelope()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var controller = new CancelRepairRunController(
            new DavDatabaseClient(dbContext),
            new HistoryVisibilityNotifier(_fixture.CreateConfigManager(), new WebsocketManager()));
        var repairRunId = Guid.NewGuid();

        var result = await HandleAuthenticatedAsync(controller, HttpMethods.Post, repairRunId);

        AssertFailure(
            controller.HttpContext,
            result,
            StatusCodes.Status404NotFound,
            "resource_not_found",
            "The requested resource was not found.");
    }

    private static async Task<IActionResult> HandleAuthenticatedAsync(
        BaseApiController controller,
        string method,
        Guid? routeId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers["x-api-key"] = "test-api-key";
        if (routeId is { } id)
            context.Request.RouteValues["id"] = id.ToString();
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            return await controller.HandleApiRequest();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private static void AssertFailure(
        HttpContext context,
        IActionResult result,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var response = Assert.IsType<BaseApiResponse>(objectResult.Value);
        Assert.False(response.Status);
        Assert.Equal(expectedCode, response.Code);
        Assert.Equal(expectedMessage, response.Error);
        Assert.Matches("^[0-9a-f]{32}$", response.CorrelationId);
        Assert.Equal(response.CorrelationId, context.Response.Headers[PublicFailureContract.CorrelationHeaderName]);
        Assert.Equal(expectedCode, context.Response.Headers[PublicFailureContract.ErrorCodeHeaderName]);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.ConvertStrmToSymlinks;
using NzbWebDAV.Api.Controllers.Maintenance;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using backend.Tests.Services;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class MaintenanceRunControllerTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public MaintenanceRunControllerTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StartEndpoint_RequiresPostReturnsAcceptedAndConflictsWithActiveRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var executor = new NoOpMaintenanceTaskExecutor();
        using var service = new MaintenanceRunService(executor);
        var getController = new RecreateStrmFiles(service);

        var getResult = await HandleAuthenticatedAsync(getController, HttpMethods.Get);

        var methodNotAllowed = Assert.IsType<ObjectResult>(getResult);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, methodNotAllowed.StatusCode);

        var startController = new RecreateStrmFiles(service);
        var startResult = await HandleAuthenticatedAsync(startController, HttpMethods.Post);

        var accepted = Assert.IsType<AcceptedResult>(startResult);
        var acceptedResponse = Assert.IsType<MaintenanceRunResponse>(accepted.Value);
        Assert.Equal("queued", acceptedResponse.Run.Status);
        Assert.Equal("recreate-strm-files", acceptedResponse.Run.Kind);
        Assert.Equal($"/api/maintenance/runs/{acceptedResponse.Run.Id}", accepted.Location);
        Assert.Equal(0, executor.ExecutionCount);

        var detailController = new MaintenanceRunController(service);
        var detailResult = await HandleAuthenticatedAsync(
            detailController,
            HttpMethods.Get,
            acceptedResponse.Run.Id);
        var detail = Assert.IsType<MaintenanceRunResponse>(Assert.IsType<OkObjectResult>(detailResult).Value);
        Assert.Equal(acceptedResponse.Run.Id, detail.Run.Id);

        var conflictController = new ConvertStrmToSymlinks(service);
        var conflictResult = await HandleAuthenticatedAsync(conflictController, HttpMethods.Post);

        var conflict = Assert.IsType<ConflictObjectResult>(conflictResult);
        var conflictResponse = Assert.IsType<MaintenanceRunConflictResponse>(conflict.Value);
        Assert.Equal(acceptedResponse.Run.Id, conflictResponse.ActiveRun.Id);
    }

    [Fact]
    public async Task StatusAndHistoryEndpointsReturnDurableActiveAndFilteredLastRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        using var service = new MaintenanceRunService(new NoOpMaintenanceTaskExecutor());
        var started = await service.TryStartRunAsync(
            MaintenanceRunKind.RemoveUnlinkedFilesDryRun,
            "manual",
            CancellationToken.None);

        var statusController = new MaintenanceStatusController(service);
        var statusContext = CreateHttpContext(HttpMethods.Get, "?kind=remove-unlinked-files-dry-run");
        statusController.ControllerContext = new ControllerContext { HttpContext = statusContext };
        var statusResult = await HandleAuthenticatedAsync(statusController, statusContext);

        var status = Assert.IsType<MaintenanceStatusResponse>(Assert.IsType<OkObjectResult>(statusResult).Value);
        Assert.Equal(started.Run.Id, status.ActiveRun?.Id);
        Assert.Equal(started.Run.Id, status.LastRun?.Id);

        var runsController = new MaintenanceRunsController(service);
        var runsContext = CreateHttpContext(
            HttpMethods.Get,
            "?kind=remove-unlinked-files-dry-run&limit=10");
        runsController.ControllerContext = new ControllerContext { HttpContext = runsContext };
        var runsResult = await HandleAuthenticatedAsync(runsController, runsContext);

        var history = Assert.IsType<MaintenanceRunsResponse>(Assert.IsType<OkObjectResult>(runsResult).Value);
        var onlyRun = Assert.Single(history.Runs);
        Assert.Equal("remove-unlinked-files-dry-run", onlyRun.Kind);
    }

    [Fact]
    public async Task CancelEndpointCancelsQueuedRunAndReturnsNotFoundForUnknownRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        using var service = new MaintenanceRunService(new NoOpMaintenanceTaskExecutor());
        var started = await service.TryStartRunAsync(
            MaintenanceRunKind.RemoveUnlinkedFiles,
            "manual",
            CancellationToken.None);

        var cancelController = new CancelMaintenanceRunController(service);
        var cancelled = await HandleAuthenticatedAsync(
            cancelController,
            HttpMethods.Post,
            started.Run.Id);

        var cancelledResponse = Assert.IsType<MaintenanceRunResponse>(Assert.IsType<OkObjectResult>(cancelled).Value);
        Assert.Equal("cancelled", cancelledResponse.Run.Status);

        var missingController = new CancelMaintenanceRunController(service);
        var missing = await HandleAuthenticatedAsync(
            missingController,
            HttpMethods.Post,
            Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(missing);
    }

    private static async Task<IActionResult> HandleAuthenticatedAsync(
        BaseApiController controller,
        string method,
        Guid? routeId = null)
    {
        var context = CreateHttpContext(method);
        if (routeId.HasValue)
            context.Request.RouteValues["id"] = routeId.Value.ToString();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return await HandleAuthenticatedAsync(controller, context);
    }

    private static async Task<IActionResult> HandleAuthenticatedAsync(
        BaseApiController controller,
        DefaultHttpContext context)
    {
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");
            context.Request.Headers["x-api-key"] = "test-api-key";
            return await controller.HandleApiRequest();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    private static DefaultHttpContext CreateHttpContext(string method, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private sealed class NoOpMaintenanceTaskExecutor : IMaintenanceTaskExecutor
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task ExecuteAsync(
            MaintenanceRunKind kind,
            MaintenanceProgressReporter report,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            return Task.CompletedTask;
        }
    }
}

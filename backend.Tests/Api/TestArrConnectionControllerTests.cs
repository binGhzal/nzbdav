using backend.Tests.Security;
using backend.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.TestArrConnection;
using NzbWebDAV.Security;

namespace backend.Tests.Api;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class TestArrConnectionControllerTests
{
    [Fact]
    public async Task ConnectionFailureKeepsHttp200SemanticsAndUsesFixedDiagnostic()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-api-key"] = "test-api-key";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["host"] = "https://credential-marker:password@[invalid",
            ["apiKey"] = "provider-key-marker",
            ["type"] = "sonarr",
        });
        var controller = new TestArrConnectionController
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        var previous = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "test-api-key");

            var result = await controller.HandleApiRequest();

            var ok = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
            var response = Assert.IsType<TestArrConnectionResponse>(ok.Value);
            Assert.True(response.Status);
            Assert.False(response.Connected);
            PublicFailureCanary.AssertSafe(response.Error);
            Assert.Equal("ARR connection test failed.", response.Error);
            Assert.Equal("arr_connection_failure", response.Code);
            Assert.Matches("^[0-9a-f]{32}$", response.CorrelationId);
            Assert.Equal(
                response.CorrelationId,
                context.Response.Headers[PublicFailureContract.CorrelationHeaderName]);
            Assert.Equal(
                response.Code,
                context.Response.Headers[PublicFailureContract.ErrorCodeHeaderName]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previous);
        }
    }
}

using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

public class TestArrConnectionRequest
{
    public string Host { get; init; }
    public string ApiKey { get; init; }
    public string? Type { get; init; }

    public TestArrConnectionRequest(HttpContext context)
    {
        Host = context.Request.Form["host"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Arr host is required");

        ApiKey = context.Request.Form["apiKey"].FirstOrDefault()
                 ?? throw new BadHttpRequestException("Arr apiKey is required");

        Type = context.Request.Form["type"].FirstOrDefault();
    }
}

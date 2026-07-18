using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

public class TestRcloneConnectionRequest
{
    public string Host { get; init; }
    public string? User { get; init; }
    public string? Pass { get; init; }
    public string? Fs { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public TestRcloneConnectionRequest(HttpContext context)
    {
        Host = context.Request.Form["host"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Rclone host is required");

        User = context.Request.Form["user"].FirstOrDefault();
        Pass = context.Request.Form["pass"].FirstOrDefault();
        Fs = context.Request.Form["fs"].FirstOrDefault();
        CancellationToken = context.RequestAborted;
    }
}

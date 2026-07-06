using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Middlewares;

namespace backend.Tests.Middlewares;

public sealed class ExceptionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsyncConvertsBadHttpRequestExceptionToBadRequest()
    {
        var middleware = new ExceptionMiddleware(
            _ => throw new BadHttpRequestException("Invalid Range header."),
            new ConfigManager());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsyncConvertsDavItemFileNotFoundToNotFound()
    {
        var middleware = new ExceptionMiddleware(
            context =>
            {
                context.Items["DavItem"] = new DavItem
                {
                    Id = Guid.NewGuid(),
                    IdPrefix = "abcde",
                    CreatedAt = DateTime.UtcNow,
                    ParentId = DavItem.ContentFolder.Id,
                    Name = "Missing.mkv",
                    FileSize = 1024,
                    Type = DavItem.ItemType.UsenetFile,
                    SubType = DavItem.ItemSubType.NzbFile,
                    Path = "/content/Missing.mkv"
                };
                throw new FileNotFoundException("Could not find nzb file.");
            },
            new ConfigManager());
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }
}

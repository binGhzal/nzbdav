using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

[ApiController]
[Route("view/{*path}")]
public class GetWebdavItemController(IStore store, ConfigManager configManager) : ControllerBase
{
    public const int ResponseCopyBufferSize = 64 * 1024;

    private async Task<Stream> GetWebdavItem(GetWebdavItemRequest request)
    {
        var item = await store.GetItemAsync(CreateStoreUri(request.Item), HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (item is null) throw new BadHttpRequestException("The file does not exist.");
        if (item is IStoreCollection) throw new BadHttpRequestException("The file does not exist.");

        // disable compression so ranged streaming responses are not transformed
        Response.Headers["Content-Encoding"] = "identity";
        var isHeadRequest = HttpContext.Request.Method == HttpMethods.Head;

        // set the content-type and content-disposition headers
        Response.Headers["Content-Type"] = GetContentType(item.Name);
        Response.Headers["Content-Disposition"] = GetContentDisposition(item.Name, request.ShouldDownload);

        // disable compression so ranged streaming responses are not transformed
        Response.Headers["Content-Encoding"] = "identity";
        Response.Headers["Accept-Ranges"] = "bytes";

        if (isHeadRequest && item is BaseStoreItem storeItem)
        {
            SetHeadResponseHeaders(request, storeItem.FileSize);
            return Stream.Null;
        }

        // handle par2 preview
        if (Path.GetExtension(item.Name).ToLower() == ".par2" && configManager.IsPreviewPar2FilesEnabled())
            return await GetPar2PreviewStream(item).ConfigureAwait(false);

        ResolvedHttpRange? resolvedRange = null;
        long? knownFileSize = null;

        // Resolve against BaseStoreItem metadata before opening an expensive
        // Usenet stream. The stream constructor consumes RequestedRangeEnd to
        // bound initial prefetch, so publishing it after open is too late.
        if (item is BaseStoreItem knownSizeItem)
        {
            knownFileSize = knownSizeItem.FileSize;
            if (request.Range is not null)
            {
                if (!request.Range.TryResolve(knownFileSize.Value, out var range))
                {
                    Response.Headers["Content-Range"] = $"bytes */{knownFileSize.Value}";
                    Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    return Stream.Null;
                }

                resolvedRange = range;
                HttpContext.Items["RequestedRangeEnd"] = range.End;
                Response.ContentLength = range.Length;
            }
            else
            {
                Response.ContentLength = knownFileSize.Value;
            }
        }

        // get the file stream and set the file-size in header
        var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileSize = knownFileSize ?? stream.Length;

        if (request.Range is not null)
        {
            if (resolvedRange is null)
            {
                if (!request.Range.TryResolve(fileSize, out var fallbackRange))
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    Response.Headers["Content-Range"] = $"bytes */{fileSize}";
                    Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    return Stream.Null;
                }

                resolvedRange = fallbackRange;
            }

            var range = resolvedRange.Value;
            HttpContext.Items["RequestedRangeEnd"] = range.End;

            // seek
            stream.Seek(range.Start, SeekOrigin.Begin);
            stream = stream.LimitLength(range.Length);

            // set response headers
            Response.Headers["Content-Range"] = $"bytes {range.Start}-{range.End}/{fileSize}";
            Response.ContentLength = range.Length;
            Response.StatusCode = 206;
        }
        else
        {
            Response.ContentLength = fileSize;
        }

        return stream;
    }

    private void SetHeadResponseHeaders(GetWebdavItemRequest request, long fileSize)
    {
        if (request.Range is not null)
        {
            if (!request.Range.TryResolve(fileSize, out var range))
            {
                Response.Headers["Content-Range"] = $"bytes */{fileSize}";
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }

            HttpContext.Items["RequestedRangeEnd"] = range.End;
            Response.Headers["Content-Range"] = $"bytes {range.Start}-{range.End}/{fileSize}";
            Response.Headers["Content-Length"] = range.Length.ToString();
            Response.StatusCode = StatusCodes.Status206PartialContent;
            return;
        }

        Response.Headers["Content-Length"] = fileSize.ToString();
    }

    [HttpGet]
    public async Task HandleRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            await using var response = await GetWebdavItem(request);
            await CopyResponseBodyAsync(response, Response.Body, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    [HttpHead]
    public async Task HandleHeadRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            await using var response = await GetWebdavItem(request).ConfigureAwait(false);
            // HEAD: headers already set, body omitted
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    private static Uri CreateStoreUri(string itemPath)
    {
        var escapedPath = string.Join(
            '/',
            itemPath
                .TrimStart('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return new Uri($"http://localhost/{escapedPath}");
    }

    private static async Task CopyResponseBodyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ResponseCopyBufferSize);
        try
        {
            while (true)
            {
                var bytesRead = await source
                    .ReadAsync(buffer.AsMemory(0, ResponseCopyBufferSize), ct)
                    .ConfigureAwait(false);
                if (bytesRead == 0) return;

                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GetContentType(string item)
    {
        if (item == "README") return "text/plain";
        var extension = Path.GetExtension(item).ToLower();
        return extension == ".mkv" ? "video/webm"
            : extension == ".rclonelink" ? "text/plain"
            : extension == ".nfo" ? "text/plain"
            : ContentTypeUtil.GetContentType(Path.GetFileName(item));
    }

    private static string GetContentDisposition(string filename, bool shouldDownload)
    {
        // Remove control characters (header safety)
        filename = new string(filename.Where(c => !char.IsControl(c)).ToArray());

        // ASCII fallback for legacy clients
        var chars = filename.Select(c => (c >= 32 && c <= 126 && c != '"' && c != '\\' && c != ';') ? c : '_');
        var ascii = new string(chars.ToArray());

        // RFC 5987 UTF-8 filename
        var utf8 = Uri.EscapeDataString(filename);

        // return
        var type = shouldDownload ? "attachment" : "inline";
        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }

    private async Task<Stream> GetPar2PreviewStream(IStoreItem item)
    {
        Response.Headers.ContentType = "text/plain";
        await using var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileDescriptors = await Par2.ReadFileDescriptions(stream, HttpContext.RequestAborted).GetAllAsync()
            .ConfigureAwait(false);
        return new MemoryStream(Encoding.UTF8.GetBytes(fileDescriptors.ToIndentedJson()));
    }
}

using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Utils;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;

    public GetAndHeadHandlerPatch(IStore store)
    {
        _store = store;
    }
    
    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Determine if we are invoked as HEAD
        var isHeadRequest = request.Method == HttpMethods.Head;

        // Determine the requested range
        var range = HttpRangeHeader.Parse(request.Headers.Range.FirstOrDefault());
        ResolvedHttpRange? resolvedRange = null;

        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), httpContext.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        // Do not return the actual item data if ETag matches. This is metadata-only and should
        // not open expensive Usenet streams.
        if (etag != null && request.Headers.IfNoneMatch == etag)
        {
            response.ContentLength = 0;
            response.SetStatus(DavStatusCode.NotModified);
            return true;
        }

        // BaseStoreItem exposes an authoritative size without opening the backing
        // stream. Resolve ranges now so expensive Usenet streams receive the
        // bounded end hint at construction time, and invalid ranges never open.
        if (entry is BaseStoreItem storeItem)
        {
            response.SetStatus(DavStatusCode.Ok);
            response.Headers.AcceptRanges = "bytes";

            var length = storeItem.FileSize;
            if (range != null)
            {
                if (!range.TryResolve(length, out var resolved))
                {
                    response.Headers.ContentRange = $"bytes */{length}";
                    response.SetStatus((DavStatusCode)416);
                    return true;
                }

                httpContext.Items["RequestedRangeEnd"] = resolved.End;
                resolvedRange = resolved;
                response.Headers.ContentRange = $"bytes {resolved.Start}-{resolved.End}/{length}";
                response.SetStatus(DavStatusCode.PartialContent);
            }

            response.ContentLength = resolvedRange?.Length ?? length;
            if (isHeadRequest)
            {
                return true;
            }
        }

        // Stream the actual entry
        var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream != Stream.Null)
            {
                // Set the response
                response.SetStatus(resolvedRange == null
                    ? DavStatusCode.Ok
                    : DavStatusCode.PartialContent);

                // Set the expected content length
                try
                {
                    // We can only specify the Content-Length header if the
                    // length is known (this is typically true for seekable streams)
                    if (stream.CanSeek)
                    {
                        // Add a header that we accept ranges (bytes only)
                        response.Headers.AcceptRanges = "bytes";

                        // Non-NZBDav store items do not expose a size before open,
                        // so retain the stream-length fallback for those items.
                        if (entry is not BaseStoreItem)
                        {
                            var length = stream.Length;
                            if (range != null)
                            {
                                if (!range.TryResolve(length, out var resolved))
                                {
                                    response.Headers.ContentRange = $"bytes */{length}";
                                    response.SetStatus((DavStatusCode)416);
                                    return true;
                                }

                                resolvedRange = resolved;
                                httpContext.Items["RequestedRangeEnd"] = resolved.End;

                                // Write the range
                                response.Headers.ContentRange = $"bytes {resolved.Start}-{resolved.End}/{length}";

                                // A valid Range request should produce a partial-content response.
                                response.SetStatus(DavStatusCode.PartialContent);
                            }
                        }
                    }
                }
                catch (NotSupportedException)
                {
                    // If the content length is not supported, then we just skip it
                }

                // HEAD method doesn't require the actual item data
                if (!isHeadRequest)
                    await CopyToAsync(
                        stream,
                        response.Body,
                        resolvedRange?.Start ?? 0,
                        resolvedRange?.End,
                        httpContext.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                // Set the response
                response.SetStatus(DavStatusCode.NoContent);
            }
        }
        return true;
    }

    private async Task CopyToAsync(Stream src, Stream dest, long start, long? end, CancellationToken cancellationToken)
    {
        // Skip to the first offset
        if (start > 0)
        {
            // We prefer seeking instead of draining data
            if (!src.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");
            
            src.Seek(start, SeekOrigin.Begin);
        }

        // Determine the number of bytes to read
        var bytesToRead = end - start + 1 ?? long.MaxValue;

        // Read in 64KB blocks
        var buffer = new byte[64 * 1024];

        // Copy, until we don't get any data anymore
        while (bytesToRead > 0)
        {
            // Read the requested bytes into memory
            var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
            var bytesRead = await src.ReadAsync(buffer, 0, requestedBytes, cancellationToken).ConfigureAwait(false);

            // We're done, if we cannot read any data anymore
            if (bytesRead == 0)
            {
                if (end.HasValue)
                    throw new IOException($"Source stream ended before satisfying response range at offset {src.Position}.");
                return;
            }
            
            // Write the data to the destination stream
            await dest.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

            // Decrement the number of bytes left to read
            bytesToRead -= bytesRead;
        }
    }
}

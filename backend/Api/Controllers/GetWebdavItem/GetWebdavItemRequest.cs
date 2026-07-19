using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

public class GetWebdavItemRequest
{
    public string Item { get; init; }
    public HttpRangeHeader? Range { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    public long? RangeSuffixLength { get; init; }
    public bool ShouldDownload { get; init; }

    public GetWebdavItemRequest(HttpContext context)
    {
        // normalize path
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/")) path = path[1..];
        if (path.StartsWith("view")) path = path[4..];
        if (path.StartsWith("/")) path = path[1..];
        Item = path;

        // determine whether to download
        ShouldDownload = context.GetQueryParam("download")?.ToLower() == "true";

        // authenticate the downloadKey
        var downloadKey = context.Request.Query["downloadKey"];
        if (!VerifyDownloadKey(downloadKey, Item))
            throw new UnauthorizedAccessException("Invalid download key");

        // parse range header
        var rangeHeader = context.Request.Headers["Range"].FirstOrDefault() ?? "";
        Range = HttpRangeHeader.Parse(rangeHeader);
        RangeStart = Range?.Start;
        RangeEnd = Range?.End;
        RangeSuffixLength = Range?.SuffixLength;
    }

    private static bool VerifyDownloadKey(string? downloadKey, string path)
    {
        var apiKey = EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
        return downloadKey == GenerateDownloadKey(apiKey, path);
    }

    public static string GenerateDownloadKey(string apiKey, string path)
    {
        var input = $"{path}_{apiKey}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);
        var hash = Convert.ToHexStringLower(hashBytes);
        return hash;
    }
}

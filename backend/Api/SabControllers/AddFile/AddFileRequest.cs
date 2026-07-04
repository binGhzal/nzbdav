using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileRequest()
{
    public string FileName { get; init; } = null!;
    public string? ContentType { get; init; }
    public Stream NzbFileStream { get; init; } = null!;
    public string Category { get; init; } = null!;
    public string? ArchivePassword { get; init; }
    public QueueItem.PriorityOption Priority { get; init; }
    public QueueItem.PostProcessingOption PostProcessing { get; init; }
    public DateTime? PauseUntil { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<AddFileRequest> New(HttpContext context, ConfigManager configManager)
    {
        var file =
            context.Request.Form.Files["nzbFile"] ??
            context.Request.Form.Files["name"] ??
            throw new BadHttpRequestException("Invalid nzbFile/name param");
        var fileName = AddNzbExtension(context.GetRequestParam("nzbname")) ?? file.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
            throw new BadHttpRequestException("NZB filename could not be determined.");

        return new AddFileRequest()
        {
            FileName = fileName,
            ContentType = file.ContentType,
            NzbFileStream = file.OpenReadStream(),
            Category = context.GetRequestParam("cat") ?? configManager.GetManualUploadCategory(),
            ArchivePassword = GetArchivePassword(context),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    public static string? GetArchivePassword(HttpContext context)
    {
        return (context.GetRequestParam("password")
                ?? context.GetRequestParam("nzbpassword")
                ?? context.GetRequestParam("unpack_password")
                ?? context.GetRequestParam("unpackPassword"))
            .ToNullIfEmpty();
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) ? nzbName
            : $"{nzbName}.nzb";
    }

    public static QueueItem.PriorityOption MapPriorityOption(string? priority)
    {
        return priority switch
        {
            "-100" => QueueItem.PriorityOption.Normal,
            "-3" => QueueItem.PriorityOption.Duplicate,
            "-2" => QueueItem.PriorityOption.Paused,
            "-1" => QueueItem.PriorityOption.Low,
            "0" => QueueItem.PriorityOption.Normal,
            "1" => QueueItem.PriorityOption.High,
            "2" => QueueItem.PriorityOption.Force,
            null => QueueItem.PriorityOption.Normal,
            _ => throw new BadHttpRequestException("Invalid priority")
        };
    }

    public static QueueItem.PostProcessingOption MapPostProcessingOption(string? postProcessing)
    {
        return postProcessing switch
        {
            "-1" => QueueItem.PostProcessingOption.None,
            "0" => QueueItem.PostProcessingOption.None,
            "1" => QueueItem.PostProcessingOption.Repair,
            "2" => QueueItem.PostProcessingOption.RepairUnpack,
            "3" => QueueItem.PostProcessingOption.RepairUnpackDelete,
            null => QueueItem.PostProcessingOption.None,
            _ => throw new BadHttpRequestException("Invalid pp param")
        };
    }
}

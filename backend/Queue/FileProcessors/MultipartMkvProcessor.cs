using System.Text.RegularExpressions;
using System.Globalization;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileProcessors;

public class MultipartMkvProcessor : BaseProcessor
{
    private readonly List<GetFileInfosStep.FileInfo> _fileInfos;
    private readonly INntpClient _usenetClient;
    private readonly CancellationToken _ct;

    public MultipartMkvProcessor
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        INntpClient usenetClient,
        CancellationToken ct
    )
    {
        _fileInfos = fileInfos;
        _usenetClient = usenetClient;
        _ct = ct;
    }

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        var sortedFileInfos = _fileInfos.OrderBy(f => GetPartNumber(f.FileName)).ToList();
        var fileParts = new List<DavMultipartFile.FilePart>();
        foreach (var fileInfo in sortedFileInfos)
        {
            var partSize = fileInfo.FileSize ?? await _usenetClient
                .GetFileSizeAsync(fileInfo.NzbFile, _ct)
                .ConfigureAwait(false);

            fileParts.Add(new DavMultipartFile.FilePart
            {
                SegmentIds = fileInfo.NzbFile.GetSegmentIds(),
                SegmentIdByteRange = LongRange.FromStartAndSize(0, partSize),
                FilePartByteRange = LongRange.FromStartAndSize(0, partSize),
                SegmentSlices = SegmentSliceUtil.CreateSlices(
                    fileInfo.NzbFile,
                    partSize,
                    LongRange.FromStartAndSize(0, partSize)),
            });
        }

        return new Result
        {
            Filename = GetBaseName(sortedFileInfos.First().FileName),
            Parts = fileParts,
            ReleaseDate = sortedFileInfos.First().ReleaseDate,
        };
    }

    private static string GetBaseName(string filename)
    {
        var extensionIndex = filename.LastIndexOf(".mkv", StringComparison.Ordinal);
        return filename[..(extensionIndex + 4)];
    }

    private static int GetPartNumber(string filename)
    {
        var match = Regex.Match(filename, @"\.mkv\.(\d+)?$", RegexOptions.IgnoreCase);
        if (string.IsNullOrEmpty(match.Groups[1].Value)) return -1;
        return int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var partNumber)
            ? partNumber
            : int.MaxValue;
    }

    public new class Result : BaseProcessor.Result
    {
        public required string Filename { get; init; }
        public required List<DavMultipartFile.FilePart> Parts { get; init; } = [];
        public required DateTimeOffset ReleaseDate { get; init; }
    }
}

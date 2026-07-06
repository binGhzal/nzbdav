using System.Text.RegularExpressions;

namespace NzbWebDAV.Models.Nzb;

public class NzbFile
{
    public required string Subject { get; init; }
    public List<NzbSegment> Segments { get; } = [];

    public string[] GetSegmentIds()
    {
        var logicalSegments = GetLogicalSegmentInfos();
        var segmentIds = new string[logicalSegments.Count];
        for (var i = 0; i < logicalSegments.Count; i++)
            segmentIds[i] = logicalSegments[i].SegmentId;
        return segmentIds;
    }

    public IReadOnlyList<LogicalSegmentInfo> GetLogicalSegmentInfos()
    {
        if (Segments.Count == 0)
            return [];

        var allNumbered = true;
        var strictlyAscending = true;
        var previousNumber = 0;
        foreach (var segment in Segments)
        {
            if (segment.Number <= 0)
            {
                allNumbered = false;
                break;
            }

            if (segment.Number <= previousNumber)
                strictlyAscending = false;
            previousNumber = segment.Number;
        }

        if (!allNumbered || strictlyAscending)
            return CreateSingleMessageLogicalSegments(Segments);

        var groupedSegments = new SortedDictionary<int, List<NzbSegment>>();
        foreach (var segment in Segments)
        {
            if (!groupedSegments.TryGetValue(segment.Number, out var group))
            {
                group = [];
                groupedSegments[segment.Number] = group;
            }

            group.Add(segment);
        }

        var logicalSegments = new List<LogicalSegmentInfo>(groupedSegments.Count);
        foreach (var group in groupedSegments.Values)
        {
            logicalSegments.Add(group.Count == 1
                ? new LogicalSegmentInfo(group[0].MessageId, group[0].Bytes)
                : new LogicalSegmentInfo(
                    NzbSegmentIdSet.Encode(group.Select(segment => segment.MessageId).ToArray()),
                    group[0].Bytes));
        }

        return logicalSegments;
    }

    public long GetTotalYencodedSize()
    {
        return GetLogicalSegmentInfos()
            .Select(x => x.Bytes)
            .Sum();
    }

    public int GetLogicalSegmentCount()
    {
        return GetLogicalSegmentInfos().Count;
    }

    public string GetSubjectFileName()
    {
        return GetFirstValidNonEmptyFilename(
            TryParseSubjectFilename1,
            TryParseSubjectFilename2
        );
    }

    private string TryParseSubjectFilename1()
    {
        // The most common format is when filename appears in double quotes
        // example: `[1/8] - "file.mkv" yEnc 12345 (1/54321)`
        var match = Regex.Match(Subject, "\\\"(.*)\\\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private string TryParseSubjectFilename2()
    {
        // Otherwise, use sabnzbd's regex
        // https://github.com/sabnzbd/sabnzbd/blob/b6b0d10367fd4960bad73edd1d3812cafa7fc002/sabnzbd/nzbstuff.py#L106
        var match = Regex.Match(Subject, @"\b([\w\-+()' .,]+(?:\[[\w\-\/+()' .,]*][\w\-+()' .,]*)*\.[A-Za-z0-9]{2,4})\b");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string GetFirstValidNonEmptyFilename(params Func<string>[] funcs)
    {
        return funcs
            .Select(x => x.Invoke())
            .Where(x => x == Path.GetFileName(x))
            .FirstOrDefault(x => x != "") ?? "";
    }

    private static IReadOnlyList<LogicalSegmentInfo> CreateSingleMessageLogicalSegments(
        IReadOnlyList<NzbSegment> segments)
    {
        var logicalSegments = new LogicalSegmentInfo[segments.Count];
        for (var i = 0; i < segments.Count; i++)
            logicalSegments[i] = new LogicalSegmentInfo(segments[i].MessageId, segments[i].Bytes);
        return logicalSegments;
    }

    public sealed record LogicalSegmentInfo(string SegmentId, long Bytes);
}

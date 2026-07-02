using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;

namespace NzbWebDAV.Utils;

public static class SegmentSliceUtil
{
    public static DavMultipartFile.SegmentSlice[] CreateSlices(
        NzbFile nzbFile,
        long partSize,
        LongRange filePartByteRange)
    {
        if (partSize <= 0 || filePartByteRange.Count <= 0) return [];

        var segments = nzbFile.GetLogicalSegmentInfos().ToArray();
        if (segments.Length == 0) return [];
        if (segments.Any(x => x.Bytes <= 0)) return [];
        if (segments.Sum(x => x.Bytes) != partSize) return [];

        var segmentRanges = CreateSegmentRanges(segments, partSize);
        return segmentRanges
            .Select(segment => CreateSlice(segment, filePartByteRange))
            .Where(slice => slice != null)
            .Select(slice => slice!)
            .ToArray();
    }

    private static SegmentRange[] CreateSegmentRanges(
        IReadOnlyList<NzbFile.LogicalSegmentInfo> segments,
        long partSize)
    {
        var ranges = new SegmentRange[segments.Count];
        long offset = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            ranges[i] = new SegmentRange(
                segments[i].SegmentId,
                LongRange.FromStartAndSize(offset, segments[i].Bytes));
            offset += segments[i].Bytes;
        }

        return ranges;
    }

    private static DavMultipartFile.SegmentSlice? CreateSlice(
        SegmentRange segment,
        LongRange filePartByteRange)
    {
        var overlapStart = Math.Max(segment.ByteRange.StartInclusive, filePartByteRange.StartInclusive);
        var overlapEnd = Math.Min(segment.ByteRange.EndExclusive, filePartByteRange.EndExclusive);
        if (overlapStart >= overlapEnd) return null;

        var overlapLength = overlapEnd - overlapStart;
        var segmentOffset = overlapStart - segment.ByteRange.StartInclusive;
        var filePartOffset = overlapStart - filePartByteRange.StartInclusive;
        return new DavMultipartFile.SegmentSlice
        {
            SegmentId = segment.SegmentId,
            SegmentByteRange = LongRange.FromStartAndSize(segmentOffset, overlapLength),
            FilePartByteRange = LongRange.FromStartAndSize(filePartOffset, overlapLength)
        };
    }

    private sealed record SegmentRange(string SegmentId, LongRange ByteRange);
}

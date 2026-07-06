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

        var segments = nzbFile.GetLogicalSegmentInfos();
        if (segments.Count == 0) return [];

        var slices = new List<DavMultipartFile.SegmentSlice>();
        long offset = 0;
        foreach (var segment in segments)
        {
            if (segment.Bytes <= 0) return [];
            var segmentRange = LongRange.FromStartAndSize(offset, segment.Bytes);
            var slice = CreateSlice(segment.SegmentId, segmentRange, filePartByteRange);
            if (slice is not null)
                slices.Add(slice);
            offset += segment.Bytes;
        }

        return offset == partSize ? slices.ToArray() : [];
    }

    private static DavMultipartFile.SegmentSlice? CreateSlice(
        string segmentId,
        LongRange segmentByteRange,
        LongRange filePartByteRange)
    {
        var overlapStart = Math.Max(segmentByteRange.StartInclusive, filePartByteRange.StartInclusive);
        var overlapEnd = Math.Min(segmentByteRange.EndExclusive, filePartByteRange.EndExclusive);
        if (overlapStart >= overlapEnd) return null;

        var overlapLength = overlapEnd - overlapStart;
        var segmentOffset = overlapStart - segmentByteRange.StartInclusive;
        var filePartOffset = overlapStart - filePartByteRange.StartInclusive;
        return new DavMultipartFile.SegmentSlice
        {
            SegmentId = segmentId,
            SegmentByteRange = LongRange.FromStartAndSize(segmentOffset, overlapLength),
            FilePartByteRange = LongRange.FromStartAndSize(filePartOffset, overlapLength)
        };
    }
}

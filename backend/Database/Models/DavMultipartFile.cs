using MemoryPack;
using NzbWebDAV.Models;

namespace NzbWebDAV.Database.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavMultipartFile
{
    [MemoryPackOrder(0)]
    public Guid Id { get; set; } // foreign key to DavItem.Id

    [MemoryPackOrder(1)]
    public Meta Metadata { get; set; } = new();

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class Meta
    {
        [MemoryPackOrder(0)]
        public AesParams? AesParams { get; set; }

        [MemoryPackOrder(1)]
        public FilePart[] FileParts { get; set; } = [];
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class FilePart
    {
        // a subsequence of segments from an NzbFile
        [MemoryPackOrder(0)]
        public string[] SegmentIds { get; set; } = [];

        // what byte range is contained within the segmentIds? (relative to the full NzbFile)
        [MemoryPackOrder(1)]
        public LongRange SegmentIdByteRange { get; set; }

        // what byte range contains the file part contents? (relative to the full NzbFile)
        // note: this range should always be fully contained within the SegmentIdByteRange above.
        [MemoryPackOrder(2)]
        public LongRange FilePartByteRange { get; set; }

        // Minimal segment-level slices for the file part. Older records may not have this field;
        // SegmentIds/SegmentIdByteRange/FilePartByteRange remain the compatibility fallback.
        [MemoryPackOrder(3)]
        public SegmentSlice[] SegmentSlices { get; set; } = [];
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SegmentSlice
    {
        [MemoryPackOrder(0)]
        public string SegmentId { get; set; } = "";

        [MemoryPackOrder(1)]
        public LongRange SegmentByteRange { get; set; }

        [MemoryPackOrder(2)]
        public LongRange FilePartByteRange { get; set; }
    }
}

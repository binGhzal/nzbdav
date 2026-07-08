namespace NzbWebDAV.Streams.Caching;

public sealed record SparseSegmentCacheSnapshot(
    long Bytes,
    long MaxBytes,
    long Hits,
    long Misses,
    long Evictions,
    int Files,
    int ActiveReaders,
    int ReadAheadActive,
    int PendingFetches,
    long FirstByteReads,
    double FirstByteAverageMilliseconds,
    long ProviderFetchErrors
);

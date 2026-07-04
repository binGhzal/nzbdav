namespace NzbWebDAV.Streams.Caching;

public sealed record SparseSegmentCacheOptions
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "/config/cache/segments";
    public long MaxBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public int ChunkBytes { get; init; } = 4 * 1024 * 1024;
    public int ReadAheadBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan IdleTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

namespace NzbWebDAV.Streams.Caching;

public interface IFileRangeReader
{
    long Length { get; }
    ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct);
}

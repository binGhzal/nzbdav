namespace NzbWebDAV.Clients.Usenet;

public sealed class ArticleCacheBudget
{
    private readonly object _lock = new();
    private long _maxBytes = long.MaxValue;
    private long _currentBytes;

    public static ArticleCacheBudget Shared { get; } = new();

    public long CurrentBytes
    {
        get
        {
            lock (_lock)
            {
                return _currentBytes;
            }
        }
    }

    public bool IsOverBudget
    {
        get
        {
            lock (_lock)
            {
                return _currentBytes > _maxBytes;
            }
        }
    }

    public void Configure(long maxBytes)
    {
        lock (_lock)
        {
            _maxBytes = maxBytes > 0 ? maxBytes : long.MaxValue;
        }
    }

    public long Add(long bytes)
    {
        if (bytes <= 0) return CurrentBytes;

        lock (_lock)
        {
            _currentBytes += bytes;
            return _currentBytes;
        }
    }

    public long Remove(long bytes)
    {
        if (bytes <= 0) return CurrentBytes;

        lock (_lock)
        {
            _currentBytes = Math.Max(0, _currentBytes - bytes);
            return _currentBytes;
        }
    }
}

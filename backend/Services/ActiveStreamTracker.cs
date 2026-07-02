using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

public sealed class ActiveStreamTracker
{
    private readonly ConcurrentDictionary<Guid, ActiveStream> _activeStreams = new();
    private long _totalOpened;

    public ActiveStreamLease Open(string? path, string? userAgent)
    {
        var id = Guid.NewGuid();
        _activeStreams[id] = new ActiveStream(
            id,
            path ?? "",
            userAgent ?? "",
            DateTimeOffset.UtcNow);
        Interlocked.Increment(ref _totalOpened);
        return new ActiveStreamLease(this, id);
    }

    public ActiveStreamSnapshot GetSnapshot()
    {
        var active = _activeStreams.Values
            .OrderBy(x => x.OpenedAtUtc)
            .ToArray();
        return new ActiveStreamSnapshot(active.Length, Interlocked.Read(ref _totalOpened), active);
    }

    private void Close(Guid id)
    {
        _activeStreams.TryRemove(id, out _);
    }

    public sealed class ActiveStreamLease : IDisposable
    {
        private readonly ActiveStreamTracker _tracker;
        private readonly Guid _id;
        private int _disposed;

        internal ActiveStreamLease(ActiveStreamTracker tracker, Guid id)
        {
            _tracker = tracker;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _tracker.Close(_id);
        }
    }

    public sealed record ActiveStream(
        Guid Id,
        string Path,
        string UserAgent,
        DateTimeOffset OpenedAtUtc);

    public sealed record ActiveStreamSnapshot(
        int Count,
        long TotalOpened,
        IReadOnlyList<ActiveStream> Streams);
}

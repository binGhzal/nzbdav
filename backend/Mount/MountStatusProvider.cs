using NzbWebDAV.Streams.Caching;

namespace NzbWebDAV.Mount;

public sealed class MountStatusProvider
{
    private readonly Lock _lock = new();
    private string _type = "rclone";
    private string _directory = "/mnt/nzbdav";
    private bool _enabled;
    private bool _ready;
    private string _state = "external-unverified";
    private string? _message = "rclone mount is managed outside NZBDav and has not been verified";
    private long _fuseErrors;
    private int _activeOperations;
    private int _waitingOperations;
    private DateTimeOffset? _lastInvalidationAt;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    public MountStatusSnapshot GetSnapshot(SparseSegmentCacheSnapshot? cache = null)
    {
        lock (_lock)
        {
            return new MountStatusSnapshot(
                Type: _type,
                Directory: _directory,
                Enabled: _enabled,
                Ready: _ready,
                State: _state,
                Message: _message,
                FuseErrors: _fuseErrors,
                ActiveOperations: _activeOperations,
                WaitingOperations: _waitingOperations,
                LastInvalidationAt: _lastInvalidationAt,
                UpdatedAt: _updatedAt,
                Cache: cache
            );
        }
    }

    public void SetExternal(string type, string directory, string? message = null)
    {
        Set(type, directory, enabled: false, ready: false, state: "external-unverified",
            message ?? $"{type} mount is managed outside NZBDav and has not been verified");
    }

    public void SetDisabled(string type, string directory, string? message = null)
    {
        Set(type, directory, enabled: false, ready: true, state: "disabled", message);
    }

    public void SetStarting(string directory)
    {
        Set("dfs", directory, enabled: true, ready: false, state: "starting", "DFS mount is starting");
    }

    public void SetReady(string directory)
    {
        Set("dfs", directory, enabled: true, ready: true, state: "ready", "DFS mount is ready");
    }

    public void SetStopped(string directory)
    {
        Set("dfs", directory, enabled: true, ready: false, state: "stopped", "DFS mount stopped");
    }

    public void SetFailed(string type, string directory, string message)
    {
        Set(type, directory, enabled: type == "dfs", ready: false, state: "failed", message);
        Interlocked.Increment(ref _fuseErrors);
    }

    public IDisposable TrackOperation()
    {
        Interlocked.Increment(ref _activeOperations);
        lock (_lock)
        {
            _activeOperations = Math.Max(_activeOperations, 0);
            _updatedAt = DateTimeOffset.UtcNow;
        }

        return new OperationLease(this);
    }

    public void RecordWaitingOperation()
    {
        Interlocked.Increment(ref _waitingOperations);
        lock (_lock)
        {
            _waitingOperations = Math.Max(_waitingOperations, 0);
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void CompleteWaitingOperation()
    {
        Interlocked.Decrement(ref _waitingOperations);
        lock (_lock)
        {
            _waitingOperations = Math.Max(_waitingOperations, 0);
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordFuseError(string message)
    {
        Interlocked.Increment(ref _fuseErrors);
        lock (_lock)
        {
            _message = message;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordInvalidation()
    {
        lock (_lock)
        {
            _lastInvalidationAt = DateTimeOffset.UtcNow;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    private void Set(string type, string directory, bool enabled, bool ready, string state, string? message)
    {
        lock (_lock)
        {
            _type = type;
            _directory = directory;
            _enabled = enabled;
            _ready = ready;
            _state = state;
            _message = message;
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    private void CompleteOperation()
    {
        Interlocked.Decrement(ref _activeOperations);
        lock (_lock)
        {
            _activeOperations = Math.Max(_activeOperations, 0);
            _updatedAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed class OperationLease(MountStatusProvider provider) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            provider.CompleteOperation();
        }
    }
}

public sealed record MountStatusSnapshot(
    string Type,
    string Directory,
    bool Enabled,
    bool Ready,
    string State,
    string? Message,
    long FuseErrors,
    int ActiveOperations,
    int WaitingOperations,
    DateTimeOffset? LastInvalidationAt,
    DateTimeOffset UpdatedAt,
    SparseSegmentCacheSnapshot? Cache
);

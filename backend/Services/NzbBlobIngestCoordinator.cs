namespace NzbWebDAV.Services;

public sealed class NzbBlobIngestCoordinator
{
    private readonly Lock _entriesLock = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public async ValueTask<IDisposable> AcquireAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = AddReference(id);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, id, entry);
        }
        catch
        {
            ReleaseReference(id, entry);
            throw;
        }
    }

    public IDisposable? TryAcquire(Guid id)
    {
        var entry = AddReference(id);
        if (entry.Gate.Wait(0))
            return new Lease(this, id, entry);

        ReleaseReference(id, entry);
        return null;
    }

    private Entry AddReference(Guid id)
    {
        lock (_entriesLock)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = new Entry();
                _entries.Add(id, entry);
            }

            entry.ReferenceCount++;
            return entry;
        }
    }

    private void Release(Guid id, Entry entry)
    {
        entry.Gate.Release();
        ReleaseReference(id, entry);
    }

    private void ReleaseReference(Guid id, Entry entry)
    {
        lock (_entriesLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0
                && _entries.TryGetValue(id, out var current)
                && ReferenceEquals(current, entry))
                _entries.Remove(id);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class Lease(NzbBlobIngestCoordinator owner, Guid id, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Release(id, entry);
        }
    }
}

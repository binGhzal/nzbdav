using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public sealed class StreamingConnectionLimiter : IConnectionLimiter, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly PrioritizedSemaphore _semaphore;
    private readonly PrioritizedSemaphore _streamSemaphore;
    private int _maxAllowedConnections;
    private int _maxAllowedStreams;

    public StreamingConnectionLimiter(ConfigManager configManager)
    {
        _configManager = configManager;
        _maxAllowedConnections = configManager.GetAdaptiveMaxTotalStreamingConnections();
        _maxAllowedStreams = configManager.GetAdaptiveMaxActiveStreams();
        _semaphore = new PrioritizedSemaphore(_maxAllowedConnections, _maxAllowedConnections);
        _streamSemaphore = new PrioritizedSemaphore(_maxAllowedStreams, _maxAllowedStreams);
        configManager.OnConfigChanged += OnConfigChanged;
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        RefreshMaxAllowedConnections();
        return _semaphore.WaitAsync(SemaphorePriority.High, cancellationToken);
    }

    public void Release()
    {
        _semaphore.Release();
    }

    public async Task<IDisposable> WaitForStreamAsync(CancellationToken cancellationToken)
    {
        RefreshMaxAllowedConnections();
        await _streamSemaphore.WaitAsync(SemaphorePriority.High, cancellationToken).ConfigureAwait(false);
        return new StreamLease(this);
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
            if (e.ChangedConfig.ContainsKey("usenet.max-total-streaming-connections")
                || e.ChangedConfig.ContainsKey("usenet.max-streaming-connections")
                || e.ChangedConfig.ContainsKey("usenet.max-active-streams")
                || e.ChangedConfig.ContainsKey("usenet.article-buffer-size")
                || e.ChangedConfig.ContainsKey("usenet.max-download-connections")
            || e.ChangedConfig.ContainsKey("usenet.providers")
            || e.ChangedConfig.ContainsKey("usenet.adaptive-connections-enabled"))
        {
            RefreshMaxAllowedConnections();
        }
    }

    private void RefreshMaxAllowedConnections()
    {
        var maxStreamingConnections = _configManager.GetAdaptiveMaxTotalStreamingConnections();
        if (Interlocked.Exchange(ref _maxAllowedConnections, maxStreamingConnections) == maxStreamingConnections)
        {
            RefreshMaxAllowedStreams();
            return;
        }

        _semaphore.UpdateMaxAllowed(maxStreamingConnections);
        RefreshMaxAllowedStreams();
    }

    private void RefreshMaxAllowedStreams()
    {
        var maxStreams = _configManager.GetAdaptiveMaxActiveStreams();
        if (Interlocked.Exchange(ref _maxAllowedStreams, maxStreams) == maxStreams)
            return;

        _streamSemaphore.UpdateMaxAllowed(maxStreams);
    }

    private void ReleaseStream()
    {
        _streamSemaphore.Release();
    }

    public void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _semaphore.Dispose();
        _streamSemaphore.Dispose();
    }

    private sealed class StreamLease(StreamingConnectionLimiter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.ReleaseStream();
        }
    }
}

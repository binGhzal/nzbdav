using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public sealed class StreamingConnectionLimiter : IConnectionLimiter, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly PrioritizedSemaphore _semaphore;
    private int _maxAllowedConnections;

    public StreamingConnectionLimiter(ConfigManager configManager)
    {
        _configManager = configManager;
        _maxAllowedConnections = configManager.GetAdaptiveMaxTotalStreamingConnections();
        _semaphore = new PrioritizedSemaphore(_maxAllowedConnections, _maxAllowedConnections);
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

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (e.ChangedConfig.ContainsKey("usenet.max-total-streaming-connections")
            || e.ChangedConfig.ContainsKey("usenet.max-streaming-connections")
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
            return;

        _semaphore.UpdateMaxAllowed(maxStreamingConnections);
    }

    public void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _semaphore.Dispose();
    }
}

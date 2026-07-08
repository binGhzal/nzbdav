using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient, IProviderPoolSnapshotSource
{
    private const int MaxConcurrentPipelinedStatBatches = 64;

    private readonly ConfigManager _configManager;

    public UsenetStreamingClient(ConfigManager configManager, WebsocketManager websocketManager)
        : base(CreateDownloadingNntpClient(configManager, websocketManager))
    {
        _configManager = configManager;

        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            // update the connection-pool according to the new config
            var newUsenetClient = CreateDownloadingNntpClient(configManager, websocketManager);
            ReplaceUnderlyingClient(
                newUsenetClient,
                TimeSpan.FromSeconds(configManager.GetConnectionIdleTimeoutSeconds()));
        };
    }

    /// <summary>
    /// STAT-checks every segment and returns one result per segment.
    /// When the NNTP pipelining master switch is on, segments are checked in pipelined batches
    /// (per-provider pipelining is gated further down the chain); otherwise this defers to the
    /// base one-command-per-round-trip implementation.
    /// </summary>
    public override async Task<SegmentCheckBatch> CheckSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        // When pipelining is disabled or ineffective, run individual STAT checks concurrently
        // through the same priority/connection gates instead of serializing a whole batch on one
        // borrowed connection.
        var depth = _configManager.GetNntpPipeliningDepth();
        var anyProviderPipelined = _configManager.GetUsenetProviderConfig()
            .Providers.Any(p => p.IsStatPipeliningEnabled());
        if (!_configManager.GetNntpPipeliningEnabled() || depth <= 1 || !anyProviderPipelined)
        {
            return await CheckSegmentsConcurrentlyAsync(segmentIds, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        // Check the segments in pipelined batches of `depth`. Keep simultaneous batches bounded so a
        // post-download verify can still be fast without stampeding every provider connection at once.
        return await CheckSegmentsPipelinedAsync(
                segmentIds,
                batchSize: depth,
                maxConcurrentBatches: GetPipelinedStatBatchConcurrency(concurrency),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static int GetPipelinedStatBatchConcurrency(int requestedConcurrency)
    {
        var runtimeLimit = Math.Clamp(Environment.ProcessorCount * 4, 8, MaxConcurrentPipelinedStatBatches);
        return Math.Clamp(requestedConcurrency, 1, runtimeLimit);
    }

    public IReadOnlyList<ProviderPoolSnapshot> GetProviderSnapshots()
    {
        return CurrentClient is IProviderPoolSnapshotSource source
            ? source.GetProviderSnapshots()
            : [];
    }

    public override async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int articleBufferSize, CancellationToken ct)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await GetFileSizeAsync(nzbFile, ct).ConfigureAwait(false);
        return GetFileStream(segmentIds, fileSize, articleBufferSize);
    }

    public override NzbFileStream GetFileStream(NzbFile nzbFile, long fileSize, int articleBufferSize)
    {
        return GetFileStream(nzbFile.GetSegmentIds(), fileSize, articleBufferSize);
    }

    public override NzbFileStream GetFileStream(
        string[] segmentIds, long fileSize, int articleBufferSize, long? requestedEndByte = null)
    {
        return new NzbFileStream(
            segmentIds,
            fileSize,
            this,
            articleBufferSize,
            requestedEndByte,
            _configManager.GetSparseSegmentCacheOptions());
    }

    private static DownloadingNntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        var multiProviderClient = CreateMultiProviderClient(configManager, websocketManager);
        return new DownloadingNntpClient(multiProviderClient, configManager);
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var idleTimeout = TimeSpan.FromSeconds(configManager.GetConnectionIdleTimeoutSeconds());
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index),
                idleTimeout
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        TimeSpan idleTimeout
    )
    {
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            onConnectionPoolChanged,
            idleTimeout
        );
        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            circuitBreaker,
            connectionDetails.Host,
            connectionDetails.Priority,
            connectionDetails.IsStatPipeliningEnabled());
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        TimeSpan idleTimeout
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(maxConnections, connectionFactory, idleTimeout);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        var host = connectionDetails.Host;
        var port = connectionDetails.Port;
        var useSsl = connectionDetails.GetEffectiveUseSsl();
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}

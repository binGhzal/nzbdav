using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
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
        // Stay on the original one-at-a-time path unless pipelining is switched on AND at least one
        // provider has actually opted in -- so enabling the master switch alone changes nothing until
        // a provider is tested and enabled.
        var depth = _configManager.GetNntpPipeliningDepth();
        var anyProviderPipelined = _configManager.GetUsenetProviderConfig()
            .Providers.Any(p => p.StatPipeliningEnabled);
        if (!_configManager.GetNntpPipeliningEnabled() || depth <= 1 || !anyProviderPipelined)
        {
            return await base.CheckSegmentsAsync(segmentIds, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var segmentList = segmentIds.ToList();
        var results = new SegmentCheckResult[segmentList.Count];

        // Check the segments in pipelined batches of `depth`, running up to `concurrency` batches at
        // once so every pooled connection stays busy. Depth is the user-facing lever: a deeper
        // pipeline hides more round-trip latency; if a provider handles deep batches poorly, lower it.
        var batches = segmentList
            .Select((segmentId, index) => new IndexedSegment(index, segmentId))
            .Chunk(depth);
        var tasks = batches
            .Select(batch => CheckPipelinedBatchAsync(batch, cancellationToken))
            .WithConcurrencyAsync(Math.Max(1, concurrency), cancellationToken);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            foreach (var result in task)
            {
                results[result.Index] = result.Result;
                progress?.Report(++processed);
            }
        }

        return SegmentCheckBatch.FromResults(results);
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

    private async Task<IReadOnlyList<IndexedSegmentCheckResult>> CheckPipelinedBatchAsync
    (
        IReadOnlyList<IndexedSegment> batch,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var batchSegmentIds = batch.Select(x => x.SegmentId).ToArray();
            var statResults = await StatPipelinedAsync(batchSegmentIds, cancellationToken).ConfigureAwait(false);
            return batch
                .Select((segment, i) => new IndexedSegmentCheckResult(
                    segment.Index,
                    CreateSegmentCheckResult(segment.SegmentId, statResults[i])
                ))
                .ToArray();
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            return batch
                .Select(segment => new IndexedSegmentCheckResult(
                    segment.Index,
                    new SegmentCheckResult(
                        segment.SegmentId,
                        SegmentCheckState.ProviderError,
                        Provider: null,
                        Error: e.Message)
                ))
                .ToArray();
        }
    }

    private readonly record struct IndexedSegment(int Index, string SegmentId);

    private readonly record struct IndexedSegmentCheckResult(int Index, SegmentCheckResult Result);

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
            connectionDetails.StatPipeliningEnabled);
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
        var useSsl = connectionDetails.UseSsl;
        var user = connectionDetails.User;
        var pass = connectionDetails.Pass;
        await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
        await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
        return connection;
    }
}

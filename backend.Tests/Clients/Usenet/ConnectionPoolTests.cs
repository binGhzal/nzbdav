using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace backend.Tests.Clients.Usenet;

public sealed class ConnectionPoolTests
{
    [Fact]
    public async Task DisposeWaitsForCachedConnectionsToBeDisposed()
    {
        using var releaseDispose = new ManualResetEventSlim(false);
        using var disposeStarted = new ManualResetEventSlim(false);
        var connection = new BlockingDisposableConnection(disposeStarted, releaseDispose);
        var pool = new ConnectionPool<BlockingDisposableConnection>(
            maxConnections: 1,
            _ => ValueTask.FromResult(connection),
            idleTimeout: TimeSpan.FromMinutes(10));
        var connectionLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        connectionLock.Dispose();

        var disposeTask = Task.Run(() => pool.Dispose());

        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
        var completedTask = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(disposeTask, completedTask);
        releaseDispose.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task DisposePublishesZeroConnectionStatsAfterDrainingIdleConnections()
    {
        var connection = new DisposableConnection();
        var pool = new ConnectionPool<DisposableConnection>(
            maxConnections: 1,
            _ => ValueTask.FromResult(connection),
            idleTimeout: TimeSpan.FromMinutes(10));
        var snapshots = new List<(int Live, int Idle, int Active)>();
        pool.OnConnectionPoolChanged += (_, args) =>
            snapshots.Add((args.Live, args.Idle, args.Active));

        var connectionLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        connectionLock.Dispose();

        pool.Dispose();

        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(0, pool.IdleConnections);
        Assert.Contains(snapshots, snapshot =>
            snapshot.Live == 0 &&
            snapshot.Idle == 0 &&
            snapshot.Active == 0);
    }

    [Fact]
    public async Task DisposeContinuesWhenCachedConnectionDisposeFails()
    {
        var firstConnection = new ThrowingDisposableConnection();
        var secondConnection = new DisposableConnection();
        var connections = new Queue<IDisposable>([firstConnection, secondConnection]);
        var pool = new ConnectionPool<IDisposable>(
            maxConnections: 2,
            _ => ValueTask.FromResult(connections.Dequeue()),
            idleTimeout: TimeSpan.FromMinutes(10));
        var firstLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var secondLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        firstLock.Dispose();
        secondLock.Dispose();

        var exception = Record.Exception(pool.Dispose);

        Assert.Null(exception);
        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(0, pool.IdleConnections);
        Assert.True(firstConnection.DisposeAttempted);
        Assert.True(secondConnection.IsDisposed);
    }

    [Fact]
    public async Task DisposeDoesNotSurfaceSweeperFaultWhenIdleTimeoutIsVerySmall()
    {
        var connection = new DisposableConnection();
        var pool = new ConnectionPool<DisposableConnection>(
            maxConnections: 1,
            _ => ValueTask.FromResult(connection),
            idleTimeout: TimeSpan.FromTicks(1));
        var connectionLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        connectionLock.Dispose();

        await Task.Delay(TimeSpan.FromMilliseconds(20));

        await pool.DisposeAsync();
        Assert.Equal(0, pool.LiveConnections);
    }

    [Fact]
    public async Task GetConnectionLockAsyncDoesNotReturnConnectionCreatedAfterPoolDisposal()
    {
        var connection = new DisposableConnection();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<DisposableConnection>(
            maxConnections: 1,
            async _ =>
            {
                factoryStarted.SetResult();
                await releaseFactory.Task.ConfigureAwait(false);
                return connection;
            },
            idleTimeout: TimeSpan.FromMinutes(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var borrowTask = pool.GetConnectionLockAsync(SemaphorePriority.Low, timeout.Token);
        await factoryStarted.Task.WaitAsync(timeout.Token);

        await pool.DisposeAsync();
        releaseFactory.SetResult();

        ConnectionLock<DisposableConnection>? leakedLock = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            leakedLock = await borrowTask.WaitAsync(timeout.Token);
        });
        leakedLock?.Dispose();

        Assert.IsType<ObjectDisposedException>(exception);
        Assert.True(connection.IsDisposed);
        Assert.Equal(0, pool.LiveConnections);
    }

    [Fact]
    public async Task StatsObserverExceptionsDoNotBreakConnectionReturn()
    {
        var connection = new DisposableConnection();
        await using var pool = new ConnectionPool<DisposableConnection>(
            maxConnections: 1,
            _ => ValueTask.FromResult(connection),
            idleTimeout: TimeSpan.FromMinutes(10));
        var connectionLock = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        pool.OnConnectionPoolChanged += (_, _) => throw new InvalidOperationException("stats observer failed");

        var exception = Record.Exception(() => connectionLock.Dispose());

        Assert.Null(exception);
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
        Assert.Equal(0, pool.ActiveConnections);
    }

    private sealed class BlockingDisposableConnection(
        ManualResetEventSlim disposeStarted,
        ManualResetEventSlim releaseDispose) : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            disposeStarted.Set();
            releaseDispose.Wait(TimeSpan.FromSeconds(5));
            IsDisposed = true;
        }
    }

    private sealed class DisposableConnection : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class ThrowingDisposableConnection : IDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public void Dispose()
        {
            DisposeAttempted = true;
            throw new InvalidOperationException("dispose failed");
        }
    }
}

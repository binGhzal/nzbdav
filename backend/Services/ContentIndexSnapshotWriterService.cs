using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class ContentIndexSnapshotWriterService : BackgroundService
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxCoalesceDelay = TimeSpan.FromMinutes(2);
    private static readonly Channel<byte> Requests = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private static readonly object RequestStateLock = new();
    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static bool _wakeSignalQueued;
    private static long _pendingRequestCount;
    private static long _requestedGeneration;
    private static long _persistedGeneration;
    private static Func<long, CancellationToken, Task> WriteSnapshotCore = WriteSnapshotCoreAsync;

    public static void RequestSnapshot()
    {
        lock (RequestStateLock)
        {
            _requestedGeneration++;
            _pendingRequestCount++;
            QueueWakeSignalUnderLock();
        }
    }

    public static async Task<bool> FlushNowAsync(CancellationToken cancellationToken)
    {
        var targetGeneration = GetRequestedGeneration();
        if (IsPersistedThrough(targetGeneration)) return true;

        await FlushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsPersistedThrough(targetGeneration)) return true;

            var batch = TakePendingRequestsForFlush();
            if (batch.RequestCount <= 0) return IsPersistedThrough(targetGeneration);

            try
            {
                if (!await TryWriteSnapshotAsync(batch.RequestCount, cancellationToken).ConfigureAwait(false))
                {
                    RestorePendingRequests(batch);
                    return false;
                }

                RecordPersistedGeneration(batch.Generation);
                return IsPersistedThrough(targetGeneration);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RestorePendingRequests(batch);
                throw;
            }
        }
        finally
        {
            FlushGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await Requests.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                DrainRequests();
                await DebounceAsync(stoppingToken).ConfigureAwait(false);
                await FlushNowAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Flush pending work below.
        }
        finally
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await FlushNowAsync(flushCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Timed out while flushing pending /content recovery snapshot during shutdown.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to flush pending /content recovery snapshot during shutdown.");
            }
        }
    }

    private static async Task DebounceAsync(CancellationToken cancellationToken)
    {
        var firstRequestAt = Stopwatch.GetTimestamp();

        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(firstRequestAt);
            var remaining = MaxCoalesceDelay - elapsed;
            if (remaining <= TimeSpan.Zero) return;

            var delay = remaining < QuietPeriod ? remaining : QuietPeriod;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (!Requests.Reader.TryRead(out _))
                return;

            DrainRequests();
        }
    }

    private static void DrainRequests()
    {
        lock (RequestStateLock)
        {
            DrainRequestsUnderLock();
            _wakeSignalQueued = false;
        }
    }

    private static long GetRequestedGeneration()
    {
        lock (RequestStateLock)
            return _requestedGeneration;
    }

    private static bool IsPersistedThrough(long targetGeneration)
    {
        lock (RequestStateLock)
            return _persistedGeneration >= targetGeneration;
    }

    private static void RecordPersistedGeneration(long generation)
    {
        lock (RequestStateLock)
            _persistedGeneration = Math.Max(_persistedGeneration, generation);
    }

    private static void RestorePendingRequests(FlushBatch batch)
    {
        if (batch.RequestCount <= 0) return;
        lock (RequestStateLock)
        {
            _pendingRequestCount += batch.RequestCount;
            QueueWakeSignalUnderLock();
        }
    }

    private static FlushBatch TakePendingRequestsForFlush()
    {
        lock (RequestStateLock)
        {
            var batch = new FlushBatch(_pendingRequestCount, _requestedGeneration);
            _pendingRequestCount = 0;
            DrainRequestsUnderLock();
            _wakeSignalQueued = false;
            return batch;
        }
    }

    private static void DrainRequestsUnderLock()
    {
        while (Requests.Reader.TryRead(out _))
        {
        }
    }

    private static void QueueWakeSignalUnderLock()
    {
        if (_wakeSignalQueued) return;
        if (Requests.Writer.TryWrite(0))
            _wakeSignalQueued = true;
    }

    private static async Task<bool> TryWriteSnapshotAsync(long requestCount, CancellationToken cancellationToken)
    {
        try
        {
            await WriteSnapshotCore(requestCount, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist /content recovery snapshot.");
            return false;
        }
    }

    private static async Task WriteSnapshotCoreAsync(long requestCount, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        await ContentIndexSnapshotStore.WriteAsync(dbContext, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "Persisted /content recovery snapshot in {ElapsedMs} ms after coalescing {RequestCount} content-index change(s).",
            sw.ElapsedMilliseconds,
            requestCount);
    }

    private readonly record struct FlushBatch(long RequestCount, long Generation);
}

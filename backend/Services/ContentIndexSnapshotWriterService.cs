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

    private static long _pendingRequestCount;

    public static void RequestSnapshot()
    {
        Interlocked.Increment(ref _pendingRequestCount);
        Requests.Writer.TryWrite(0);
    }

    public static async Task FlushNowAsync(CancellationToken cancellationToken)
    {
        var requestCount = Interlocked.Exchange(ref _pendingRequestCount, 0);
        DrainRequests();
        if (requestCount <= 0) return;

        await WriteSnapshotAsync(requestCount, cancellationToken).ConfigureAwait(false);
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
        while (Requests.Reader.TryRead(out _))
        {
        }
    }

    private static async Task WriteSnapshotAsync(long requestCount, CancellationToken cancellationToken)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await using var dbContext = new DavDatabaseContext();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, cancellationToken).ConfigureAwait(false);
            Log.Information(
                "Persisted /content recovery snapshot in {ElapsedMs} ms after coalescing {RequestCount} content-index change(s).",
                sw.ElapsedMilliseconds,
                requestCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist /content recovery snapshot.");
        }
    }
}

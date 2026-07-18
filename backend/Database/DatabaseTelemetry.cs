namespace NzbWebDAV.Database;

public sealed class DatabaseTelemetry
{
    public static DatabaseTelemetry Shared { get; } = new();

    private readonly LatencyWindow _queryLatency;
    private readonly LatencyWindow _transactionLatency;
    private long _busyRetries;
    private long _leaseRetries;

    public DatabaseTelemetry(int sampleCapacity = 4096)
    {
        if (sampleCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCapacity), "Sample capacity must be positive.");
        _queryLatency = new LatencyWindow(sampleCapacity);
        _transactionLatency = new LatencyWindow(sampleCapacity);
    }

    public void RecordBusyRetry() => Interlocked.Increment(ref _busyRetries);

    public void RecordLeaseRetry() => Interlocked.Increment(ref _leaseRetries);

    public void RecordQuery(TimeSpan duration) => _queryLatency.Record(duration.TotalMilliseconds);

    public void RecordTransaction(TimeSpan duration) => _transactionLatency.Record(duration.TotalMilliseconds);

    public DatabaseTelemetrySnapshot GetSnapshot() => new(
        BusyRetries: Interlocked.Read(ref _busyRetries),
        LeaseRetries: Interlocked.Read(ref _leaseRetries),
        Query: _queryLatency.Snapshot(),
        Transaction: _transactionLatency.Snapshot());

    private sealed class LatencyWindow
    {
        private readonly double[] _samples;
        private readonly object _lock = new();
        private int _next;
        private int _count;

        public LatencyWindow(int capacity)
        {
            _samples = new double[capacity];
        }

        public void Record(double milliseconds)
        {
            lock (_lock)
            {
                _samples[_next] = Math.Max(0, milliseconds);
                _next = (_next + 1) % _samples.Length;
                if (_count < _samples.Length) _count++;
            }
        }

        public LatencyPercentiles Snapshot()
        {
            double[] samples;
            lock (_lock)
            {
                samples = _samples[.._count].ToArray();
            }

            if (samples.Length == 0) return new LatencyPercentiles(0, 0, 0);
            Array.Sort(samples);
            return new LatencyPercentiles(
                samples.Length,
                Percentile(samples, 0.95),
                Percentile(samples, 0.99));
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
            return Math.Round(sorted[index], 3);
        }
    }
}

public sealed record DatabaseTelemetrySnapshot(
    long BusyRetries,
    long LeaseRetries,
    LatencyPercentiles Query,
    LatencyPercentiles Transaction);

public sealed record LatencyPercentiles(
    long Count,
    double P95Milliseconds,
    double P99Milliseconds);

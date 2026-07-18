using System.Diagnostics;

namespace NzbWebDAV.Telemetry;

public enum CriticalPathStage
{
    AddFileBlobWrite = 0,
    AddFileNzbScan = 1,
    AddFileAtomicCommit = 2,
    QueueParse = 3,
    QueueFirstSegmentDiscovery = 4,
    QueuePar2Discovery = 5,
    QueueProcessors = 6,
    QueueCompletion = 7
}

public sealed class CriticalPathTelemetry
{
    public const int DefaultSampleCapacity = 512;

    public static CriticalPathTelemetry Shared { get; } = new();

    private readonly StageWindow[] _windows;

    /// <summary>
    /// Keeps lifetime attempt/failure counters and the most recent <paramref name="sampleCapacity"/>
    /// elapsed samples per fixed stage. Percentiles use the bounded sample window; counters reset only
    /// when the process (or this instance) is recreated. Any exceptional exit, including cancellation,
    /// is recorded as a failure by callers.
    /// </summary>
    public CriticalPathTelemetry(int sampleCapacity = DefaultSampleCapacity)
    {
        if (sampleCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCapacity), "Sample capacity must be positive.");

        _windows = new StageWindow[(int)CriticalPathStage.QueueCompletion + 1];
        for (var i = 0; i < _windows.Length; i++)
            _windows[i] = new StageWindow(sampleCapacity);
    }

    public void RecordElapsed(CriticalPathStage stage, long startTimestamp, bool failed) =>
        Record(stage, Stopwatch.GetElapsedTime(startTimestamp), failed);

    public void Record(CriticalPathStage stage, TimeSpan duration, bool failed) =>
        _windows[(int)stage].Record(duration.TotalMilliseconds, failed);

    public CriticalPathTelemetrySnapshot GetSnapshot() => new(
        AddFileBlobWrite: Snapshot(CriticalPathStage.AddFileBlobWrite),
        AddFileNzbScan: Snapshot(CriticalPathStage.AddFileNzbScan),
        AddFileAtomicCommit: Snapshot(CriticalPathStage.AddFileAtomicCommit),
        QueueParse: Snapshot(CriticalPathStage.QueueParse),
        QueueFirstSegmentDiscovery: Snapshot(CriticalPathStage.QueueFirstSegmentDiscovery),
        QueuePar2Discovery: Snapshot(CriticalPathStage.QueuePar2Discovery),
        QueueProcessors: Snapshot(CriticalPathStage.QueueProcessors),
        QueueCompletion: Snapshot(CriticalPathStage.QueueCompletion));

    private CriticalPathStageSnapshot Snapshot(CriticalPathStage stage) => _windows[(int)stage].Snapshot();

    private sealed class StageWindow
    {
        private readonly double[] _samples;
        private readonly object _lock = new();
        private int _next;
        private int _sampleCount;
        private long _count;
        private long _failures;

        public StageWindow(int capacity)
        {
            _samples = new double[capacity];
        }

        public void Record(double milliseconds, bool failed)
        {
            lock (_lock)
            {
                _count++;
                if (failed) _failures++;
                _samples[_next] = Math.Max(0, milliseconds);
                _next = (_next + 1) % _samples.Length;
                if (_sampleCount < _samples.Length) _sampleCount++;
            }
        }

        public CriticalPathStageSnapshot Snapshot()
        {
            double[] samples;
            long count;
            long failures;
            lock (_lock)
            {
                samples = _samples[.._sampleCount].ToArray();
                count = _count;
                failures = _failures;
            }

            if (samples.Length == 0)
                return new CriticalPathStageSnapshot(count, failures, 0, 0, 0);

            Array.Sort(samples);
            return new CriticalPathStageSnapshot(
                count,
                failures,
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

public sealed record CriticalPathTelemetrySnapshot(
    CriticalPathStageSnapshot AddFileBlobWrite,
    CriticalPathStageSnapshot AddFileNzbScan,
    CriticalPathStageSnapshot AddFileAtomicCommit,
    CriticalPathStageSnapshot QueueParse,
    CriticalPathStageSnapshot QueueFirstSegmentDiscovery,
    CriticalPathStageSnapshot QueuePar2Discovery,
    CriticalPathStageSnapshot QueueProcessors,
    CriticalPathStageSnapshot QueueCompletion);

public sealed record CriticalPathStageSnapshot(
    long Count,
    long Failures,
    int LatencySamples,
    double P95Milliseconds,
    double P99Milliseconds);

namespace NzbWebDAV.Database.Transfer;

internal interface ITransferV3PostgreSqlOperationFence : IDisposable
{
    CancellationToken CancellationToken { get; }

    bool IsExpired { get; }
}

internal sealed class TransferV3PostgreSqlDeadline
{
    internal static readonly TimeSpan MaximumDuration =
        TimeSpan.FromMilliseconds(0xfffffffeL);

    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly long _durationTicks;
    private long _minimumRemainingTicks;

    private TransferV3PostgreSqlDeadline(
        TimeProvider timeProvider,
        long startTimestamp,
        long durationTicks)
    {
        _timeProvider = timeProvider;
        _startTimestamp = startTimestamp;
        _durationTicks = durationTicks;
        _minimumRemainingTicks = durationTicks;
    }

    internal static TransferV3PostgreSqlDeadline Start(
        TimeProvider timeProvider,
        TimeSpan duration)
    {
        if (timeProvider is null
            || duration.Ticks <= 0
            || duration.Ticks > MaximumDuration.Ticks)
        {
            throw TransferV3Phase4Exception.Create(
                new ArgumentOutOfRangeException(nameof(duration)),
                TransferV3Phase4Boundary.Argument);
        }

        try
        {
            if (timeProvider.TimestampFrequency <= 0)
                throw new InvalidOperationException();

            return new TransferV3PostgreSqlDeadline(
                timeProvider,
                timeProvider.GetTimestamp(),
                duration.Ticks);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    internal TimeSpan Remaining => TimeSpan.FromTicks(SampleRemainingTicks());

    internal bool IsExpired => SampleRemainingTicks() == 0;

    internal TransferV3PostgreSqlCommandFence CreateCommandFence(
        int ordinaryMaximumSeconds)
    {
        if (ordinaryMaximumSeconds <= 0)
        {
            throw TransferV3Phase4Exception.Create(
                new ArgumentOutOfRangeException(nameof(ordinaryMaximumSeconds)),
                TransferV3Phase4Boundary.Argument);
        }

        var remainingTicks = SampleRemainingTicks();
        if (remainingTicks == 0)
            return TransferV3PostgreSqlCommandFence.CreateExpired();

        var roundedSeconds =
            (remainingTicks + TimeSpan.TicksPerSecond - 1)
            / TimeSpan.TicksPerSecond;
        var timeoutSeconds = checked((int)Math.Min(
            roundedSeconds,
            ordinaryMaximumSeconds));

        try
        {
            var source = new CancellationTokenSource(
                TimeSpan.FromTicks(remainingTicks),
                _timeProvider);
            return new TransferV3PostgreSqlCommandFence(timeoutSeconds, source);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    internal ITransferV3PostgreSqlOperationFence CreateOperationFence()
    {
        var remainingTicks = SampleRemainingTicks();
        if (remainingTicks == 0)
        {
            var expiredSource = new CancellationTokenSource();
            expiredSource.Cancel();
            return new TransferV3PostgreSqlOperationFence(expiredSource);
        }

        try
        {
            var source = new CancellationTokenSource(
                TimeSpan.FromTicks(remainingTicks),
                _timeProvider);
            return new TransferV3PostgreSqlOperationFence(source);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    private long SampleRemainingTicks()
    {
        long elapsedTicks;
        try
        {
            var now = _timeProvider.GetTimestamp();
            elapsedTicks = _timeProvider.GetElapsedTime(_startTimestamp, now).Ticks;
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }

        var candidate = elapsedTicks <= 0
            ? _durationTicks
            : elapsedTicks >= _durationTicks
                ? 0
                : _durationTicks - elapsedTicks;
        return RetainMinimum(candidate);
    }

    private long RetainMinimum(long candidate)
    {
        var observed = Volatile.Read(ref _minimumRemainingTicks);
        while (candidate < observed)
        {
            var prior = Interlocked.CompareExchange(
                ref _minimumRemainingTicks,
                candidate,
                observed);
            if (prior == observed)
                return candidate;

            observed = prior;
        }

        return observed;
    }

    private sealed class TransferV3PostgreSqlOperationFence
        : ITransferV3PostgreSqlOperationFence
    {
        private CancellationTokenSource? _source;

        internal TransferV3PostgreSqlOperationFence(CancellationTokenSource source)
        {
            _source = source;
            CancellationToken = source.Token;
        }

        public CancellationToken CancellationToken { get; }

        public bool IsExpired => CancellationToken.IsCancellationRequested;

        public void Dispose()
        {
            var source = Interlocked.Exchange(ref _source, null);
            if (source is null)
                return;

            try
            {
                source.Dispose();
            }
            catch (Exception raw)
            {
                throw TransferV3Phase4FailureMapper.Sanitize(
                    raw,
                    TransferV3Phase4Boundary.Unexpected,
                    default);
            }
        }
    }
}

internal sealed class TransferV3PostgreSqlCommandFence : IDisposable
{
    private CancellationTokenSource? _source;

    internal TransferV3PostgreSqlCommandFence(
        int commandTimeoutSeconds,
        CancellationTokenSource source)
    {
        CommandTimeoutSeconds = commandTimeoutSeconds;
        _source = source;
        CancellationToken = source.Token;
    }

    internal int CommandTimeoutSeconds { get; }

    internal CancellationToken CancellationToken { get; }

    internal bool IsExpired => CancellationToken.IsCancellationRequested;

    internal static TransferV3PostgreSqlCommandFence CreateExpired()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return new TransferV3PostgreSqlCommandFence(1, source);
    }

    public void Dispose()
    {
        var source = Interlocked.Exchange(ref _source, null);
        if (source is null)
            return;

        try
        {
            source.Dispose();
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }
}
